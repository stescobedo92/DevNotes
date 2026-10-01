using Avalonia.Headless.XUnit;
using DevNotes.Application.Notes;
using DevNotes.Application.Settings;
using DevNotes.Application.Vaults;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.Tests.Support;
using DevNotes.Desktop.ViewModels;
using DevNotes.Domain.Notes;
using Microsoft.Extensions.Time.Testing;

namespace DevNotes.Desktop.Tests.ViewModels;

public sealed class QuickCaptureTests
{
    [AvaloniaFact]
    public async Task Save_WritesANoteFromTheNoteTemplate_AndClearsTheDraft()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var capture = harness.CaptureViewModel;
        var closed = 0;
        NotePath? saved = null;
        capture.CloseRequested += (_, _) => closed++;
        capture.Saved += (_, path) => saved = path;

        capture.Title = "Idea rápida";
        capture.Text = "Primera línea\n\nSegunda línea.";
        capture.Project = "oatpp-api";
        await capture.SaveCommand.ExecuteAsync(null);

        saved!.Value.Value.Should().Be("idea-rapida.md");
        closed.Should().Be(1);
        capture.HasDraft.Should().BeFalse();
        capture.HasError.Should().BeFalse();
        var text = harness.VaultFolder.Read("idea-rapida.md");
        var document = NoteDocumentParser.Parse(text, "f");
        document.Metadata.Title.Should().Be("Idea rápida");
        document.Metadata.Project.Should().Be("oatpp-api");
        document.Metadata.Id.Should().NotBeNull();
        document.Body.Should().Contain("# Idea rápida").And.Contain("Primera línea\n\nSegunda línea.").And.NotContain("{{");
        harness.Shell.Notification.Message.Should().Be(ErrorMessages.Format(Strings.Notify_Captured, "Idea rápida"));

        await harness.SettleAsync();
        harness.Shell.NoteList.Items.Should().Contain(item => item.Path.Value == "idea-rapida.md");
    }

    [AvaloniaFact]
    public async Task Save_WithoutTitle_UsesTheFirstLineOfTheText()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var capture = harness.CaptureViewModel;

        capture.Text = "\r\n## Comando para limpiar la caché  \r\n\r\ndotnet nuget locals all --clear\r\n";
        await capture.SaveCommand.ExecuteAsync(null);

        harness.VaultFolder.Exists("comando-para-limpiar-la-cache.md").Should().BeTrue();
        var document = NoteDocumentParser.Parse(harness.VaultFolder.Read("comando-para-limpiar-la-cache.md"), "f");
        document.Metadata.Title.Should().Be("Comando para limpiar la caché");
        document.Body.Should().Be("\n# Comando para limpiar la caché\n\ndotnet nuget locals all --clear\n", "the first line became the title and is not repeated");
    }

    [Fact]
    public void FirstLineTitle_HandlesEveryLineEnding()
    {
        var (title, rest, lossless) = QuickCaptureViewModel.SplitFirstLineForTests("\r\n\r\n  ## Primera  \r\nSegunda\nTercera");
        title.Should().Be("Primera");
        rest.Should().Be("Segunda\nTercera");
        lossless.Should().BeTrue();

        QuickCaptureViewModel.SplitFirstLineForTests("solo").Should().Be(("solo", string.Empty, true));
        QuickCaptureViewModel.SplitFirstLineForTests("#todo cosas").FirstLine.Should().Be("#todo cosas", "a word starting with # is not a heading");
        QuickCaptureViewModel.SplitFirstLineForTests("   \n  ").Should().Be((string.Empty, "   \n  ", true));

        var (longTitle, _, longLossless) = QuickCaptureViewModel.SplitFirstLineForTests(new string('x', 300));
        longTitle.Should().HaveLength(NoteTitle.MaxLength);
        longLossless.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Save_FirstLineThatCannotBeATitleVerbatim_KeepsTheWholeText()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var capture = harness.CaptureViewModel;
        var longLine = new string('x', 300);

        capture.Text = longLine;
        await capture.SaveCommand.ExecuteAsync(null);

        var files = Directory.GetFiles(harness.VaultFolder.Path, "*.md").Where(file => Path.GetFileName(file).StartsWith("xxx", StringComparison.Ordinal));
        var text = File.ReadAllText(files.Should().ContainSingle().Subject);
        text.Should().Contain(longLine, "every character typed is in the note");

        capture.Text = "Dos   espacios\tcon tab\nresto";
        await capture.SaveCommand.ExecuteAsync(null);

        var note = harness.VaultFolder.Read("dos-espacios-con-tab.md");
        note.Should().Contain("Dos   espacios\tcon tab\nresto", "a first line the title cannot hold verbatim stays in the body");
    }

    [AvaloniaFact]
    public async Task Save_TypedTitleThatIsInvalid_IsReportedNotReplaced()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var capture = harness.CaptureViewModel;

        capture.Title = new string('t', NoteTitle.MaxLength + 1);
        capture.Text = "Texto";
        await capture.SaveCommand.ExecuteAsync(null);

        capture.HasError.Should().BeTrue();
        capture.Title.Should().HaveLength(NoteTitle.MaxLength + 1);
        Directory.GetFiles(harness.VaultFolder.Path, "*.md", SearchOption.AllDirectories).Should().HaveCount(3);
    }

    [AvaloniaFact]
    public async Task Draft_IsStoredOnCancel_RestoredOnPrepare_AndClearedAfterASave()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var capture = harness.CaptureViewModel;

        capture.Text = "Pendiente";
        capture.Project = "oatpp-api";
        capture.CancelCommand.Execute(null);

        harness.Drafts.Stored.Should().Be(new CaptureDraft(string.Empty, "Pendiente", "oatpp-api"));

        capture.Title = "Guardada";
        await capture.SaveCommand.ExecuteAsync(null);

        harness.Drafts.Stored.Should().BeNull();
        harness.Drafts.Clears.Should().BeGreaterThan(0);
    }

    [AvaloniaFact]
    public async Task Draft_IsStoredAfterAQuietPeriod_AndATimerOvertakenByASaveDoesNotBringItBack()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var time = new FakeTimeProvider();
        var dispatcher = new QueuedUiDispatcher();
        using var capture = new QuickCaptureViewModel(
            harness.Get<IVaultSessionManager>(),
            harness.Get<INotificationService>(),
            harness.Drafts,
            time,
            dispatcher);

        capture.Text = "Pendiente";
        time.Advance(QuickCaptureViewModel.DraftDelay - TimeSpan.FromMilliseconds(1));
        dispatcher.RunPending();
        harness.Drafts.Saves.Should().Be(0, "the draft waits for the quiet period");

        time.Advance(TimeSpan.FromMilliseconds(1));
        harness.Drafts.Saves.Should().Be(0, "the timer thread only asks for a turn on the UI thread");
        dispatcher.RunPending();
        harness.Drafts.Stored.Should().Be(new CaptureDraft(string.Empty, "Pendiente", string.Empty));

        // The timer fires while the note is being written and gets its turn once the draft was cleared.
        capture.Text = "Pendiente y guardada";
        time.Advance(QuickCaptureViewModel.DraftDelay);
        await capture.SaveCommand.ExecuteAsync(null);
        harness.Drafts.Stored.Should().BeNull();
        dispatcher.RunPending();

        harness.Drafts.Stored.Should().BeNull("a note that was saved is never offered again as a draft");
        harness.VaultFolder.Exists("pendiente-y-guardada.md").Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Draft_LeftByAPreviousSession_ComesBackTheFirstTimeTheWindowOpens()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        harness.Drafts.Stored = new CaptureDraft("De ayer", "Sigue aquí", "cslinq");
        var capture = harness.CaptureViewModel;
        capture.DefaultProject = "otro";

        capture.Prepare();

        capture.Title.Should().Be("De ayer");
        capture.Text.Should().Be("Sigue aquí");
        capture.Project.Should().Be("cslinq", "the draft's project wins over the active one");
        capture.HasDraft.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Shutdown_StoresTheDraft()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        harness.CaptureViewModel.Text = "No se pierde al salir";

        (await harness.Shell.ShutdownAsync(layout: null)).Should().BeTrue();

        harness.Drafts.Stored!.Text.Should().Be("No se pierde al salir");
    }

    [AvaloniaFact]
    public async Task Save_WithNothingTyped_ExplainsAndKeepsTheWindow()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var capture = harness.CaptureViewModel;
        var closed = 0;
        capture.CloseRequested += (_, _) => closed++;

        capture.Text = "   ";
        await capture.SaveCommand.ExecuteAsync(null);

        capture.Error.Should().Be(Strings.Capture_Empty);
        closed.Should().Be(0);
        Directory.GetFiles(harness.VaultFolder.Path, "*.md", SearchOption.AllDirectories).Should().HaveCount(3);
    }

    [AvaloniaFact]
    public async Task Save_WithoutAVault_KeepsTheDraft()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), openVault: false);
        var capture = harness.CaptureViewModel;

        capture.Text = "No debe perderse";
        await capture.SaveCommand.ExecuteAsync(null);

        capture.Error.Should().Be(Strings.Capture_NoVault);
        capture.Text.Should().Be("No debe perderse");
        capture.HasDraft.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Cancel_HidesAndKeepsTheDraft_AndPrepareOnlyPrefillsAnEmptyDraft()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var capture = harness.CaptureViewModel;
        var closed = 0;
        capture.CloseRequested += (_, _) => closed++;
        capture.DefaultProject = "oatpp-api";

        capture.Prepare();
        capture.Project.Should().Be("oatpp-api");

        capture.Text = "Borrador";
        capture.Project = "otro";
        capture.CancelCommand.Execute(null);

        closed.Should().Be(1);
        capture.Text.Should().Be("Borrador");
        capture.Prepare();
        capture.Project.Should().Be("otro", "a pending draft is never overwritten");
    }

    [AvaloniaFact]
    public async Task Save_UsesTheCustomizedNoteTemplate()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        await harness.Session.Templates.SaveAsync("note", "---\ntitle: {{title}}\ntags: [captura]\n---\n# {{title}}\n\n## Captura\n\n{{body}}\n", CancellationToken.None);
        var capture = harness.CaptureViewModel;

        capture.Title = "Con plantilla";
        capture.Text = "cuerpo";
        await capture.SaveCommand.ExecuteAsync(null);

        var document = NoteDocumentParser.Parse(harness.VaultFolder.Read("con-plantilla.md"), "f");
        document.Metadata.Tags.Select(tag => tag.Value).Should().Equal("captura");
        document.Body.Should().Contain("## Captura\n\ncuerpo");
    }

    [AvaloniaFact]
    public async Task Shell_QuickCaptureCommand_ShowsTheWindow_AndTheShortcutIsCtrlAltN()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), openVault: false);
        var shell = harness.Shell;

        shell.QuickCaptureCommand.Execute(null);

        harness.QuickCapture.ShowCalls.Should().Be(1);
        shell.Commands.Single(command => command.Id == "capture.quick").Shortcut.Should().Be(new ShortcutKey("N", Primary: true, Alt: true));
        shell.QuickCaptureShortcut.Should().NotBeEmpty();
    }

    [AvaloniaFact]
    public async Task Coordinator_RegistersTheShortcutFromTheSettings_AndOpensTheWindowWhenPressed()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), openVault: false);
        var window = UiTest.ShowMainWindow(harness);
        using var coordinator = new QuickCaptureCoordinator(harness.Hotkey, harness.Settings, harness.QuickCapture, new AvaloniaUiDispatcher());

        coordinator.Start(window);

        harness.Hotkey.IsAttached.Should().BeTrue();
        harness.Hotkey.Registered.Should().Be(HotkeyGesture.Default);
        harness.Hotkey.State.Should().Be(HotkeyState.Active);

        harness.Hotkey.Press();
        harness.QuickCapture.ShowCalls.Should().Be(1);

        await harness.Settings.UpdateAsync(settings => settings with { QuickCapture = settings.QuickCapture with { Hotkey = "Ctrl+Shift+F9" } }, CancellationToken.None);
        await UiTest.WaitForAsync(() => harness.Hotkey.Registered?.Key == "F9", "the new shortcut to be registered");
        harness.Hotkey.RegisterCalls.Should().Be(2);

        await harness.Settings.UpdateAsync(settings => settings with { QuickCapture = settings.QuickCapture with { GlobalHotkeyEnabled = false } }, CancellationToken.None);
        await UiTest.WaitForAsync(() => harness.Hotkey.Registered is null, "the shortcut to be released");
        harness.Hotkey.UnregisterCalls.Should().Be(1);

        // Unrelated settings do not touch the registration.
        await harness.Settings.UpdateAsync(settings => settings with { FontSize = 18 }, CancellationToken.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        harness.Hotkey.UnregisterCalls.Should().Be(1);
        harness.Hotkey.RegisterCalls.Should().Be(2);
    }

    [AvaloniaFact]
    public async Task Coordinator_DisabledInSettings_RegistersNothing()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), openVault: false);
        await harness.Settings.UpdateAsync(settings => settings with { QuickCapture = new QuickCaptureSettings { GlobalHotkeyEnabled = false } }, CancellationToken.None);
        var window = UiTest.ShowMainWindow(harness);
        using var coordinator = new QuickCaptureCoordinator(harness.Hotkey, harness.Settings, harness.QuickCapture, new AvaloniaUiDispatcher());

        coordinator.Start(window);

        harness.Hotkey.Registered.Should().BeNull();
        harness.Hotkey.State.Should().Be(HotkeyState.Inactive);
    }

    [Theory]
    [InlineData("N", 0x4Eu)]
    [InlineData("7", 0x37u)]
    [InlineData("F1", 0x70u)]
    [InlineData("F24", 0x87u)]
    [InlineData("Space", 0x20u)]
    [InlineData("PageDown", 0x22u)]
    public void Win32VirtualKeys_MatchTheWindowsConstants(string key, uint expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Win32GlobalHotkeyService.TryGetVirtualKey(key, out var virtualKey).Should().BeTrue();
        virtualKey.Should().Be(expected);
        Win32GlobalHotkeyService.TryGetVirtualKey("Ñ", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("N", "VcN")]
    [InlineData("7", "Vc7")]
    [InlineData("F12", "VcF12")]
    [InlineData("Space", "VcSpace")]
    [InlineData("Back", "VcBackspace")]
    [InlineData("Enter", "VcEnter")]
    [InlineData("Insert", "VcInsert")]
    public void SharpHookKeyCodes_ResolveEveryAcceptedKey(string key, string expected)
    {
        SharpHookGlobalHotkeyService.TryGetKeyCode(key, out var keyCode).Should().BeTrue();
        keyCode.ToString().Should().Be(expected);
    }

    [Fact]
    public void LaunchContext_ReadsTheProjectFolderFromTheArguments()
    {
        var folder = Path.GetTempPath();

        LaunchContext.FromArguments(["--project-dir", folder]).ProjectDirectory.Should().Be(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        LaunchContext.FromArguments([$"--PROJECT-DIR={folder}"]).ProjectDirectory.Should().Be(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        LaunchContext.FromArguments(["--project-dir"]).ProjectDirectory.Should().Be(LaunchContext.FromArguments([]).ProjectDirectory, "a missing value is ignored");
        LaunchContext.FromArguments(["--project-dir", "::\0bad"]).ProjectDirectory.Should().BeNull();
    }
}
