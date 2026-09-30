using System.Text;
using DevNotes.Application.Abstractions;
using DevNotes.Domain.Notes;
using DevNotes.Infrastructure.Storage;
using Microsoft.Extensions.Time.Testing;

namespace DevNotes.Infrastructure.Tests.Storage;

public sealed class VaultFileStoreTests : IDisposable
{
    private readonly TempDirectory _vault = new("devnotes-vault-");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
    private readonly VaultFileStore _store;

    public VaultFileStoreTests()
    {
        _store = new VaultFileStore(_vault.Path, _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _vault.Dispose();

    [Fact]
    public void Constructor_RelativeRoot_IsRejected()
    {
        FluentActions.Invoking(() => new VaultFileStore("relative/vault", _time)).Should().Throw<ArgumentException>();
        _store.RootPath.Should().Be(Path.TrimEndingDirectorySeparator(_vault.Path));
    }

    [Fact]
    public async Task ListNotesAsync_ReturnsOnlyVisibleMarkdownFilesSortedByPath()
    {
        _vault.Write("b.md", "b");
        _vault.Write("a/nested/deep.MD", "deep");
        _vault.Write("a/first.md", "first");
        _vault.Write("readme.txt", "not a note");
        _vault.Write("image.png", "not a note");
        _vault.Write(".git/config.md", "hidden folder");
        _vault.Write(".obsidian/workspace.md", "hidden folder");
        _vault.Write(".devnotes/trash/x/note.md", "trash");
        _vault.Write("a/.hidden.md", "hidden file");
        _vault.Write("a/.draft.md.123.tmp", "temporary");

        var notes = await _store.ListNotesAsync(Ct);

        notes.Select(note => note.Path.Value).Should().Equal("a/first.md", "a/nested/deep.MD", "b.md");
        notes[0].Size.Should().Be(5);
        notes[0].LastWriteTimeUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task ListNotesAsync_MissingVaultFolder_ReturnsEmpty()
    {
        var store = new VaultFileStore(_vault.Combine("does-not-exist"), _time);

        (await store.ListNotesAsync(Ct)).Should().BeEmpty();
        (await store.ListFoldersAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task ListFoldersAsync_ReturnsVisibleFoldersRecursively()
    {
        _vault.Write("bugs/2026/a.md", "x");
        _vault.Write("adr/b.md", "x");
        _vault.Write(".git/objects/c.md", "x");
        Directory.CreateDirectory(_vault.Combine("empty"));

        (await _store.ListFoldersAsync(Ct)).Should().Equal("adr", "bugs", "bugs/2026", "empty");
    }

    [Fact]
    public async Task ReadAsync_ReturnsTextHashOfExactBytesAndMatchingInfo()
    {
        var text = "---\ntitle: Ñandú\n---\nCuerpo con acentos: áéí\n";
        _vault.Write("note.md", text);
        var path = NotePath.Create("note.md");

        var file = await _store.ReadAsync(path, Ct);
        var info = await _store.GetInfoAsync(path, Ct);

        file.Should().NotBeNull();
        file!.Text.Should().Be(text);
        file.Hash.Should().Be(ContentHash.Compute(Encoding.UTF8.GetBytes(text)));
        file.Info.Should().Be(info);
        file.Info.Size.Should().Be(Encoding.UTF8.GetByteCount(text));
    }

    [Fact]
    public async Task ReadAsync_MissingFileOrFolder_ReturnsNull()
    {
        (await _store.ReadAsync(NotePath.Create("missing.md"), Ct)).Should().BeNull();
        (await _store.ReadAsync(NotePath.Create("no/such/folder/missing.md"), Ct)).Should().BeNull();
        (await _store.GetInfoAsync(NotePath.Create("missing.md"), Ct)).Should().BeNull();
    }

    [Theory]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    public async Task ReadAsync_DecodesByteOrderMarks(string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf16-le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            _ => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
        };
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("# Título\n")).ToArray();
        await File.WriteAllBytesAsync(_vault.Combine("encoded.md"), bytes, Ct);

        var file = await _store.ReadAsync(NotePath.Create("encoded.md"), Ct);

        file!.Text.Should().Be("# Título\n");
        file.Hash.Should().Be(ContentHash.Compute(bytes), "the hash always covers the exact bytes on disk");
    }

    [Fact]
    public async Task NotUnicode_IsReadWithoutLosingBytes_AndSavedBackInTheSameEncoding()
    {
        // "canción € “q”" as Windows PowerShell's Set-Content or an old editor writes it (Windows-1252).
        byte[] original = [0x63, 0x61, 0x6E, 0x63, 0x69, 0xF3, 0x6E, 0x20, 0x80, 0x20, 0x93, 0x71, 0x94, 0x0A];
        var fullPath = _vault.Combine("legacy.md");
        await File.WriteAllBytesAsync(fullPath, original, Ct);
        var path = NotePath.Create("legacy.md");

        var file = await _store.ReadAsync(path, Ct);

        file!.Text.Should().Be("canción € “q”\n");
        file.Encoding.Should().Be(NoteTextEncoding.Legacy);
        file.Hash.Should().Be(ContentHash.Compute(original));

        // An edit that fits the code page keeps every other byte exactly as it was.
        var saved = await _store.WriteAsync(path, file.Text + "más\n", NoteWriteMode.Overwrite, new NoteWriteOptions(file.Encoding), Ct);

        saved.Encoding.Should().Be(NoteTextEncoding.Legacy);
        (await File.ReadAllBytesAsync(fullPath, Ct)).Should().Equal([.. original, 0x6D, 0xE1, 0x73, 0x0A]);

        // A character the code page cannot hold turns the note into UTF-8: the text always wins.
        var converted = await _store.WriteAsync(path, file.Text + "✓\n", NoteWriteMode.Overwrite, new NoteWriteOptions(file.Encoding), Ct);

        converted.Encoding.Should().Be(NoteTextEncoding.Utf8);
        var reread = await _store.ReadAsync(path, Ct);
        reread!.Text.Should().Be("canción € “q”\n✓\n");
        reread.Encoding.Should().Be(NoteTextEncoding.Utf8);
    }

    [Theory]
    [InlineData("utf8-bom", NoteTextEncoding.Utf8WithBom)]
    [InlineData("utf16-le", NoteTextEncoding.Utf16LittleEndian)]
    [InlineData("utf16-be", NoteTextEncoding.Utf16BigEndian)]
    public async Task WriteAsync_KeepsTheEncodingTheNoteWasReadIn(string encodingName, NoteTextEncoding expected)
    {
        Encoding encoding = encodingName switch
        {
            "utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf16-le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            _ => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
        };
        var fullPath = _vault.Combine("encoded.md");
        await File.WriteAllBytesAsync(fullPath, [.. encoding.GetPreamble(), .. encoding.GetBytes("# Título\n")], Ct);
        var path = NotePath.Create("encoded.md");

        var file = await _store.ReadAsync(path, Ct);
        var saved = await _store.WriteAsync(path, file!.Text + "línea ✓\n", NoteWriteMode.Overwrite, new NoteWriteOptions(file.Encoding), Ct);

        file.Encoding.Should().Be(expected);
        saved.Encoding.Should().Be(expected);
        var bytes = await File.ReadAllBytesAsync(fullPath, Ct);
        bytes.Should().Equal([.. encoding.GetPreamble(), .. encoding.GetBytes("# Título\nlínea ✓\n")]);
        saved.Hash.Should().Be(ContentHash.Compute(bytes));
    }

    [Fact]
    public async Task ReadAsync_FileLargerThanLimit_IsRejected()
    {
        var path = _vault.Combine("huge.md");
        await using (var stream = File.Create(path))
        {
            stream.SetLength(VaultFileStore.MaxNoteSizeBytes + 1);
        }

        var act = () => _store.ReadAsync(NotePath.Create("huge.md"), Ct);

        await act.Should().ThrowAsync<InvalidDataException>();
        (await _store.ListNotesAsync(Ct)).Should().BeEmpty("oversized files are not treated as notes");
    }

    [Fact]
    public async Task ReadAsync_WhileAnotherProcessHoldsTheFileOpenForWriting_StillWorks()
    {
        var fullPath = _vault.Write("shared.md", "content");
        await using var otherEditor = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        var file = await _store.ReadAsync(NotePath.Create("shared.md"), Ct);

        file!.Text.Should().Be("content");
    }

    [Fact]
    public async Task WriteAsync_CreatesFoldersWritesUtf8WithoutBomAndLeavesNoTemporaryFiles()
    {
        var path = NotePath.Create("new/folder/note.md");
        const string text = "# Título con ñ\n";

        var written = await _store.WriteAsync(path, text, NoteWriteMode.CreateNew, Ct);

        var bytes = await File.ReadAllBytesAsync(_vault.Combine("new", "folder", "note.md"), Ct);
        bytes.Should().Equal(Encoding.UTF8.GetBytes(text));
        written.Hash.Should().Be(ContentHash.Compute(bytes));
        written.Info.Should().Be(await _store.GetInfoAsync(path, Ct));
        Directory.GetFiles(_vault.Combine("new", "folder")).Should().ContainSingle("no temporary file may be left behind");
    }

    [Fact]
    public async Task WriteAsync_LargerThanTheReadLimit_IsRejectedBeforeTouchingTheDisk()
    {
        var path = NotePath.Create("huge.md");
        await _store.WriteAsync(path, "small", NoteWriteMode.CreateNew, Ct);

        var act = () => _store.WriteAsync(path, new string('a', (int)VaultFileStore.MaxNoteSizeBytes + 1), NoteWriteMode.Overwrite, Ct);

        await act.Should().ThrowAsync<InvalidDataException>("a note saved past the limit could never be opened again");
        (await _store.ReadAsync(path, Ct))!.Text.Should().Be("small");
        Directory.GetFiles(_vault.Path).Should().ContainSingle();
    }

    [Fact]
    public async Task WriteAsync_WithExpectedVersion_OnlyReplacesTheVersionThatWasRead()
    {
        var path = NotePath.Create("note.md");
        var fullPath = _vault.Combine("note.md");
        var read = await _store.WriteAsync(path, "version read by the app", NoteWriteMode.CreateNew, Ct);

        // Another program saves the note after the app compared it and before the app replaces it.
        await File.WriteAllTextAsync(fullPath, "saved by another editor", Ct);
        File.SetLastWriteTimeUtc(fullPath, read.Info.LastWriteTimeUtc.UtcDateTime.AddSeconds(3));

        var stale = () => _store.WriteAsync(path, "would clobber it", NoteWriteMode.Overwrite, new NoteWriteOptions(ExpectedOnDisk: read.Info), Ct);

        (await stale.Should().ThrowAsync<NoteChangedOnDiskException>()).Which.Path.Should().Be(path);
        (await File.ReadAllTextAsync(fullPath, Ct)).Should().Be("saved by another editor");
        Directory.GetFiles(_vault.Path).Should().ContainSingle("the abandoned write leaves no temporary file");

        var current = await _store.ReadAsync(path, Ct);
        await _store.WriteAsync(path, "merged", NoteWriteMode.Overwrite, new NoteWriteOptions(ExpectedOnDisk: current!.Info), Ct);
        (await File.ReadAllTextAsync(fullPath, Ct)).Should().Be("merged");

        File.Delete(fullPath);
        var deleted = () => _store.WriteAsync(path, "x", NoteWriteMode.Overwrite, new NoteWriteOptions(ExpectedOnDisk: current.Info), Ct);
        await deleted.Should().ThrowAsync<NoteChangedOnDiskException>("a file that disappeared is not the version that was read either");
        File.Exists(fullPath).Should().BeFalse();
    }

    [Fact]
    public async Task DriveRootVault_ResolvesNotesInsideIt()
    {
        // Only reads of a name that cannot exist: nothing is ever written to the root of the drive.
        var store = new VaultFileStore(Path.GetPathRoot(_vault.Path)!, _time);
        var missing = NotePath.Create($"devnotes-no-such-note-{Guid.NewGuid():N}.md");

        (await store.GetInfoAsync(missing, Ct)).Should().BeNull();
        (await store.ReadAsync(missing, Ct)).Should().BeNull("a note directly under the root is inside the vault");
    }

    [Fact]
    public async Task WriteAsync_CreateNew_NeverOverwrites()
    {
        var path = NotePath.Create("note.md");
        await _store.WriteAsync(path, "original", NoteWriteMode.CreateNew, Ct);

        var act = () => _store.WriteAsync(path, "intruder", NoteWriteMode.CreateNew, Ct);

        (await act.Should().ThrowAsync<NoteAlreadyExistsException>()).Which.Path.Should().Be(path);
        (await _store.ReadAsync(path, Ct))!.Text.Should().Be("original");
        Directory.GetFiles(_vault.Path).Should().ContainSingle();
    }

    [Fact]
    public async Task WriteAsync_Overwrite_ReplacesContentAtomically()
    {
        var path = NotePath.Create("note.md");
        await _store.WriteAsync(path, new string('a', 10_000), NoteWriteMode.CreateNew, Ct);

        var written = await _store.WriteAsync(path, "short", NoteWriteMode.Overwrite, Ct);

        (await _store.ReadAsync(path, Ct))!.Text.Should().Be("short");
        written.Info.Size.Should().Be(5);
        Directory.GetFiles(_vault.Path).Should().ContainSingle();
    }

    [Fact]
    public async Task WriteAsync_Cancelled_LeavesTheOriginalUntouched()
    {
        var path = NotePath.Create("note.md");
        await _store.WriteAsync(path, "original", NoteWriteMode.CreateNew, Ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _store.WriteAsync(path, "never written", NoteWriteMode.Overwrite, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await _store.ReadAsync(path, Ct))!.Text.Should().Be("original");
        Directory.GetFiles(_vault.Path).Should().ContainSingle();
    }

    [Fact]
    public async Task WriteAsync_ManyConcurrentSavesOfTheSameNote_NeverCorruptIt()
    {
        var path = NotePath.Create("contended.md");
        var versions = Enumerable.Range(0, 20).Select(i => new string((char)('a' + i), 20_000)).ToArray();

        var results = await Task.WhenAll(versions.Select(async version =>
        {
            try
            {
                await _store.WriteAsync(path, version, NoteWriteMode.Overwrite, Ct);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false; // Windows can refuse a rename while another one is in flight; the file must stay whole.
            }
        }));

        results.Should().Contain(true);
        var final = (await _store.ReadAsync(path, Ct))!.Text;
        versions.Should().Contain(final, "the file must be exactly one of the written versions, never a mix");
        Directory.GetFiles(_vault.Path).Should().ContainSingle();
    }

    [Theory]
    [InlineData(".git/hooks.md")]
    [InlineData(".devnotes/trash/x/note.md")]
    [InlineData("notes/.secret.md")]
    public async Task Operations_OnHiddenPaths_AreRejected(string path)
    {
        var notePath = NotePath.Create(path);

        await FluentActions.Invoking(() => _store.WriteAsync(notePath, "x", NoteWriteMode.Overwrite, Ct)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => _store.ReadAsync(notePath, Ct)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => _store.MoveAsync(NotePath.Create("a.md"), notePath, Ct)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => _store.ReadAsync(default, Ct)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Operations_ThroughASymbolicLink_AreRejected_AndLinksAreNotListed()
    {
        using var outside = new TempDirectory("devnotes-outside-");
        outside.Write("secret.md", "outside the vault");
        try
        {
            Directory.CreateSymbolicLink(_vault.Combine("link"), outside.Path);
            File.CreateSymbolicLink(_vault.Combine("linked-file.md"), outside.Combine("secret.md"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating symbolic links requires elevated privileges on this machine.");
        }

        _vault.Write("real.md", "inside");

        (await _store.ListNotesAsync(Ct)).Select(note => note.Path.Value).Should().Equal("real.md");
        (await _store.ListFoldersAsync(Ct)).Should().BeEmpty();
        (await _store.GetInfoAsync(NotePath.Create("linked-file.md"), Ct)).Should().BeNull();
        (await _store.ReadAsync(NotePath.Create("linked-file.md"), Ct)).Should().BeNull("a linked file is not a note of this vault");
        await FluentActions.Invoking(() => _store.WriteAsync(NotePath.Create("linked-file.md"), "x", NoteWriteMode.Overwrite, Ct))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        File.ReadAllText(outside.Combine("secret.md")).Should().Be("outside the vault");
        await FluentActions.Invoking(() => _store.ReadAsync(NotePath.Create("link/secret.md"), Ct)).Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Invoking(() => _store.WriteAsync(NotePath.Create("link/new.md"), "x", NoteWriteMode.CreateNew, Ct))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        File.Exists(outside.Combine("new.md")).Should().BeFalse();
    }

    [Fact]
    public async Task MoveAsync_RenamesAndCreatesDestinationFolder()
    {
        await _store.WriteAsync(NotePath.Create("a.md"), "content", NoteWriteMode.CreateNew, Ct);

        await _store.MoveAsync(NotePath.Create("a.md"), NotePath.Create("archive/2026/b.md"), Ct);

        (await _store.ReadAsync(NotePath.Create("a.md"), Ct)).Should().BeNull();
        (await _store.ReadAsync(NotePath.Create("archive/2026/b.md"), Ct))!.Text.Should().Be("content");
    }

    [Fact]
    public async Task MoveAsync_NeverOverwritesAndReportsMissingSource()
    {
        await _store.WriteAsync(NotePath.Create("a.md"), "a", NoteWriteMode.CreateNew, Ct);
        await _store.WriteAsync(NotePath.Create("b.md"), "b", NoteWriteMode.CreateNew, Ct);

        await FluentActions.Invoking(() => _store.MoveAsync(NotePath.Create("a.md"), NotePath.Create("b.md"), Ct))
            .Should().ThrowAsync<NoteAlreadyExistsException>();
        await FluentActions.Invoking(() => _store.MoveAsync(NotePath.Create("missing.md"), NotePath.Create("c.md"), Ct))
            .Should().ThrowAsync<NoteNotFoundException>();

        (await _store.ReadAsync(NotePath.Create("a.md"), Ct))!.Text.Should().Be("a");
        (await _store.ReadAsync(NotePath.Create("b.md"), Ct))!.Text.Should().Be("b");
    }

    [Fact]
    public async Task MoveAsync_CaseOnlyRename_Works()
    {
        await _store.WriteAsync(NotePath.Create("Readme.md"), "content", NoteWriteMode.CreateNew, Ct);

        await _store.MoveAsync(NotePath.Create("Readme.md"), NotePath.Create("readme.md"), Ct);

        Directory.GetFiles(_vault.Path).Select(Path.GetFileName).Should().Equal("readme.md");
    }

    [Fact]
    public async Task Trash_DeleteListRestore_RoundTrips()
    {
        var path = NotePath.Create("bugs/deadlock.md");
        await _store.WriteAsync(path, "important", NoteWriteMode.CreateNew, Ct);

        var entry = await _store.MoveToTrashAsync(path, Ct);

        entry.OriginalPath.Should().Be(path);
        entry.DeletedAtUtc.Should().Be(_time.GetUtcNow());
        (await _store.ReadAsync(path, Ct)).Should().BeNull();
        (await _store.ListNotesAsync(Ct)).Should().BeEmpty("trashed notes are not part of the vault listing");
        (await _store.ListTrashAsync(Ct)).Should().Equal(entry);
        File.ReadAllText(_vault.Combine(".devnotes", ".gitignore")).Should().Contain("trash/\n", "the trash is local, the templates next to it are meant to be versioned");

        var restored = await _store.RestoreFromTrashAsync(entry.Id, Ct);

        restored.Should().Be(path);
        (await _store.ReadAsync(path, Ct))!.Text.Should().Be("important");
        (await _store.ListTrashAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Trash_RestoreWhenNameWasReused_PicksAFreeNameAndKeepsBothNotes()
    {
        var path = NotePath.Create("note.md");
        await _store.WriteAsync(path, "deleted version", NoteWriteMode.CreateNew, Ct);
        var first = await _store.MoveToTrashAsync(path, Ct);
        await _store.WriteAsync(path, "second deleted version", NoteWriteMode.CreateNew, Ct);
        _time.Advance(TimeSpan.FromMinutes(1));
        var second = await _store.MoveToTrashAsync(path, Ct);
        await _store.WriteAsync(path, "current version", NoteWriteMode.CreateNew, Ct);

        (await _store.ListTrashAsync(Ct)).Should().Equal(second, first);
        var restoredFirst = await _store.RestoreFromTrashAsync(first.Id, Ct);
        var restoredSecond = await _store.RestoreFromTrashAsync(second.Id, Ct);

        restoredFirst.Value.Should().Be("note-restored.md");
        restoredSecond.Value.Should().Be("note-restored-2.md");
        (await _store.ReadAsync(path, Ct))!.Text.Should().Be("current version");
        (await _store.ReadAsync(restoredFirst, Ct))!.Text.Should().Be("deleted version");
        (await _store.ReadAsync(restoredSecond, Ct))!.Text.Should().Be("second deleted version");
    }

    [Fact]
    public async Task Trash_PurgeSingleEntryAndEmptyAll()
    {
        var entries = new List<TrashEntry>();
        foreach (var name in new[] { "a.md", "b.md", "c.md" })
        {
            await _store.WriteAsync(NotePath.Create(name), name, NoteWriteMode.CreateNew, Ct);
            entries.Add(await _store.MoveToTrashAsync(NotePath.Create(name), Ct));
        }

        await _store.DeleteFromTrashAsync(entries[0].Id, Ct);
        (await _store.ListTrashAsync(Ct)).Should().HaveCount(2);

        await _store.EmptyTrashAsync(Ct);
        (await _store.ListTrashAsync(Ct)).Should().BeEmpty();
        await _store.EmptyTrashAsync(Ct); // no trash folder content: still fine
    }

    [Theory]
    [InlineData(".devnotes")]
    [InlineData(".devnotes/trash")]
    public async Task Trash_ThatIsALinkToAnotherFolder_IsNeverUsed(string linkedFolder)
    {
        // A vault cloned from a repository could ship its trash as a link to any folder of the machine.
        using var outside = new TempDirectory("devnotes-outside-");
        outside.Write("trash/precious/keep.md", "must survive");
        outside.Write("precious/keep.md", "must survive");
        var link = _vault.Combine(linkedFolder.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!TestLinks.TryCreateDirectoryLink(link, linkedFolder == ".devnotes" ? outside.Path : outside.Combine("trash")))
        {
            Assert.Skip("Directory links cannot be created on this machine.");
        }

        var path = NotePath.Create("note.md");
        await _store.WriteAsync(path, "still here", NoteWriteMode.CreateNew, Ct);

        await FluentActions.Invoking(() => _store.EmptyTrashAsync(Ct)).Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Invoking(() => _store.ListTrashAsync(Ct)).Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Invoking(() => _store.MoveToTrashAsync(path, Ct)).Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Invoking(() => _store.DeleteFromTrashAsync("precious", Ct)).Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Invoking(() => _store.RestoreFromTrashAsync("precious", Ct)).Should().ThrowAsync<UnauthorizedAccessException>();

        File.ReadAllText(outside.Combine("trash", "precious", "keep.md")).Should().Be("must survive");
        File.ReadAllText(outside.Combine("precious", "keep.md")).Should().Be("must survive");
        Directory.GetFiles(outside.Path, "*", SearchOption.AllDirectories).Should().HaveCount(2, "nothing may be written through the link");
        (await _store.ReadAsync(path, Ct))!.Text.Should().Be("still here");
    }

    [Fact]
    public async Task Trash_EntryThatIsALink_IsUnlinkedNotFollowed()
    {
        using var outside = new TempDirectory("devnotes-outside-");
        outside.Write("keep.md", "must survive");
        await _store.WriteAsync(NotePath.Create("a.md"), "a", NoteWriteMode.CreateNew, Ct);
        await _store.MoveToTrashAsync(NotePath.Create("a.md"), Ct);
        if (!TestLinks.TryCreateDirectoryLink(_vault.Combine(".devnotes", "trash", "linked-entry"), outside.Path))
        {
            Assert.Skip("Directory links cannot be created on this machine.");
        }

        (await _store.ListTrashAsync(Ct)).Should().ContainSingle("a linked entry is not a trash entry");
        await _store.EmptyTrashAsync(Ct);

        Directory.GetFileSystemEntries(_vault.Combine(".devnotes", "trash")).Should().BeEmpty();
        File.ReadAllText(outside.Combine("keep.md")).Should().Be("must survive");
    }

    [Theory]
    [InlineData("../../outside")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData(" padded ")]
    [InlineData("does-not-exist")]
    public async Task Trash_UnknownOrUnsafeIds_AreRejectedBeforeTouchingTheDisk(string trashId)
    {
        using var outside = new TempDirectory("devnotes-outside-");

        await FluentActions.Invoking(() => _store.RestoreFromTrashAsync(trashId, Ct)).Should().ThrowAsync<TrashEntryNotFoundException>();
        await FluentActions.Invoking(() => _store.DeleteFromTrashAsync(trashId, Ct)).Should().ThrowAsync<TrashEntryNotFoundException>();

        Directory.Exists(_vault.Path).Should().BeTrue();
        Directory.Exists(outside.Path).Should().BeTrue();
    }

    [Fact]
    public async Task Trash_MissingNote_ThrowsAndLeavesNoEmptyEntry()
    {
        await FluentActions.Invoking(() => _store.MoveToTrashAsync(NotePath.Create("missing.md"), Ct)).Should().ThrowAsync<NoteNotFoundException>();

        (await _store.ListTrashAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Trash_DamagedOrForgedMetadata_IsIgnored()
    {
        _vault.Write(".devnotes/trash/broken/entry.json", "{ not json");
        _vault.Write(".devnotes/trash/broken/note.md", "x");
        _vault.Write(".devnotes/trash/forged/entry.json", """{"originalPath":"../../evil.md","deletedAtUtc":"2026-09-30T00:00:00+00:00"}""");
        _vault.Write(".devnotes/trash/forged/note.md", "x");
        _vault.Write(".devnotes/trash/no-note/entry.json", """{"originalPath":"a.md","deletedAtUtc":"2026-09-30T00:00:00+00:00"}""");

        (await _store.ListTrashAsync(Ct)).Should().BeEmpty();
        await FluentActions.Invoking(() => _store.RestoreFromTrashAsync("forged", Ct)).Should().ThrowAsync<TrashEntryNotFoundException>();
    }

    [Fact]
    public void TemporaryFiles_AreHiddenAndShort_WhateverTheNameOfTheNote()
    {
        var longName = new string('n', 240) + ".md";

        var temporary = Path.GetFileName(AtomicFile.GetTemporaryPath(_vault.Combine(longName)));

        temporary.Should().StartWith(".", "scans and the watcher ignore dot files");
        temporary.Length.Should().BeLessThan(20, "a note whose name is near the file-system limit must still be saveable");
        Path.GetFileName(AtomicFile.GetTemporaryPath(_vault.Combine(longName))).Should().NotBe(temporary);
    }

    [Fact]
    public void Factory_CreatesAStoreForTheGivenRoot()
    {
        new VaultFileStoreFactory(_time).Create(_vault.Path).RootPath.Should().Be(_store.RootPath);
    }
}
