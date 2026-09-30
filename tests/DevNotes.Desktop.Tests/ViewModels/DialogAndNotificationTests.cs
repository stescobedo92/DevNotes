using DevNotes.Application.Abstractions;
using DevNotes.Desktop.Tests.Support;
using DevNotes.Desktop.ViewModels;
using DevNotes.Desktop.ViewModels.Dialogs;
using DevNotes.Domain.Notes;
using Microsoft.Extensions.Time.Testing;

namespace DevNotes.Desktop.Tests.ViewModels;

public sealed class DialogAndNotificationTests
{
    private readonly DialogHostViewModel _host = new();

    [Fact]
    public async Task ConfirmAsync_ReturnsTrueOnlyWhenConfirmed()
    {
        var confirmed = _host.ConfirmAsync("Delete?", "Really?", "Delete", isDestructive: true);
        var dialog = _host.Current.Should().BeOfType<ConfirmDialogViewModel>().Subject;
        dialog.Title.Should().Be("Delete?");
        dialog.Message.Should().Be("Really?");
        dialog.ConfirmText.Should().Be("Delete");
        dialog.IsDestructive.Should().BeTrue();
        _host.IsOpen.Should().BeTrue();

        dialog.ConfirmCommand.Execute(null);

        (await confirmed).Should().BeTrue();
        _host.IsOpen.Should().BeFalse();

        var cancelled = _host.ConfirmAsync("Delete?", "Really?", "Delete");
        _host.CancelCurrent();
        (await cancelled).Should().BeFalse();
    }

    [Fact]
    public async Task PromptAsync_ValidatesBeforeAccepting()
    {
        var prompt = _host.PromptAsync("New note", "Title", "initial", "Create", value => value.Length < 3 ? "Too short" : null, ["a", "b"]);
        var dialog = _host.Current.Should().BeOfType<PromptDialogViewModel>().Subject;
        dialog.Value.Should().Be("initial");
        dialog.HasSuggestions.Should().BeTrue();

        dialog.Value = "ab";
        dialog.ConfirmCommand.Execute(null);

        dialog.Error.Should().Be("Too short");
        dialog.HasError.Should().BeTrue();
        _host.IsOpen.Should().BeTrue("invalid input keeps the dialog open");

        dialog.Value = "abc";
        dialog.HasError.Should().BeFalse("editing clears the stale message");
        dialog.ConfirmCommand.Execute(null);

        (await prompt).Should().Be("abc");
    }

    [Fact]
    public async Task PromptAsync_Cancelled_ReturnsNull_AndSuggestionsFillTheValue()
    {
        var prompt = _host.PromptAsync("Move", "Folder", string.Empty, "Move", _ => null, ["bugs", "adr"]);
        var dialog = (PromptDialogViewModel)_host.Current!;

        dialog.UseSuggestionCommand.Execute("adr");
        dialog.Value.Should().Be("adr");
        dialog.CancelCommand.Execute(null);

        (await prompt).Should().BeNull();
    }

    [Fact]
    public async Task Dialogs_Stack_SoAConfirmationCanSitOnTopOfAnotherDialog()
    {
        var first = _host.PromptAsync("First", "Label", string.Empty, "OK", _ => null);
        var firstDialog = _host.Current;
        var second = _host.ConfirmAsync("Second", "Sure?", "Yes");

        _host.Current.Should().BeOfType<ConfirmDialogViewModel>();
        ((ConfirmDialogViewModel)_host.Current!).ConfirmCommand.Execute(null);
        (await second).Should().BeTrue();

        _host.Current.Should().BeSameAs(firstDialog, "closing the top dialog reveals the one below");
        _host.CancelCurrent();
        (await first).Should().BeNull();
        _host.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task TrashDialog_RestoresPurgesAndEmpties()
    {
        var entries = Enumerable.Range(1, 3)
            .Select(i => new TrashEntry($"id-{i}", NotePath.Create($"folder/note-{i}.md"), new DateTimeOffset(2026, 9, i, 10, 0, 0, TimeSpan.Zero)))
            .ToList();
        var restored = new List<string>();
        var purged = new List<string>();
        var confirmEmpty = false;
        var dialog = new TrashDialogViewModel(
            entries,
            restore: item =>
            {
                restored.Add(item.Entry.Id);
                return Task.FromResult(item.Entry.Id != "id-3");
            },
            deleteForever: item =>
            {
                purged.Add(item.Entry.Id);
                return Task.FromResult(true);
            },
            emptyAll: _ => Task.FromResult(confirmEmpty));

        dialog.Items.Should().HaveCount(3);
        dialog.Items[0].Name.Should().Be("note-1");
        dialog.Items[0].Path.Should().Be("folder/note-1.md");
        dialog.Items[0].DeletedAt.Should().NotBeNullOrWhiteSpace();

        await dialog.RestoreCommand.ExecuteAsync(dialog.Items[0]);
        dialog.Items.Should().HaveCount(2);

        await dialog.RestoreCommand.ExecuteAsync(dialog.Items[1]);
        dialog.Items.Should().HaveCount(2, "a failed restore keeps the entry in the list");

        await dialog.DeleteForeverCommand.ExecuteAsync(dialog.Items[0]);
        dialog.Items.Should().ContainSingle();

        await dialog.EmptyAllCommand.ExecuteAsync(null);
        dialog.IsEmpty.Should().BeFalse("the user declined the confirmation");

        confirmEmpty = true;
        await dialog.EmptyAllCommand.ExecuteAsync(null);
        dialog.IsEmpty.Should().BeTrue();
        dialog.EmptyAllCommand.CanExecute(null).Should().BeFalse();
        restored.Should().Equal("id-1", "id-3");
        purged.Should().Equal("id-2");
    }

    [Fact]
    public void Notification_InfoDisappearsOnItsOwn_ErrorsStayUntilDismissed()
    {
        var time = new FakeTimeProvider();
        using var notification = new NotificationViewModel(new ImmediateUiDispatcher(), time);

        notification.Show("Saved a copy");
        notification.IsVisible.Should().BeTrue();
        notification.IsError.Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(4));
        notification.IsVisible.Should().BeFalse();

        notification.Show("Disk full", NotificationKind.Error);
        time.Advance(TimeSpan.FromMinutes(10));
        notification.IsVisible.Should().BeTrue("an error must not vanish before the user has read it");
        notification.IsError.Should().BeTrue();
        notification.Message.Should().Be("Disk full");

        notification.DismissCommand.Execute(null);
        notification.IsVisible.Should().BeFalse();
    }

    [Fact]
    public void Notification_RoutineMessage_NeverHidesAnErrorThatIsWaitingToBeRead()
    {
        var time = new FakeTimeProvider();
        using var notification = new NotificationViewModel(new ImmediateUiDispatcher(), time);

        notification.Show("The note could not be saved: disk full", NotificationKind.Error);
        notification.Show("Code copied to the clipboard.");
        time.Advance(TimeSpan.FromMinutes(1));

        notification.IsError.Should().BeTrue();
        notification.Message.Should().Be("The note could not be saved: disk full");

        notification.Show("The vault could not be opened", NotificationKind.Error);
        notification.Message.Should().Be("The vault could not be opened", "a newer error replaces the older one");

        notification.DismissCommand.Execute(null);
        notification.Show("Code copied to the clipboard.");
        notification.Message.Should().Be("Code copied to the clipboard.");
    }

    [Fact]
    public void Notification_NewerMessageReplacesTheCurrentOne_AndRestartsItsTimer()
    {
        var time = new FakeTimeProvider();
        using var notification = new NotificationViewModel(new ImmediateUiDispatcher(), time);

        notification.Show("first");
        time.Advance(TimeSpan.FromSeconds(3));
        notification.Show("second");
        time.Advance(TimeSpan.FromSeconds(3));

        notification.Message.Should().Be("second");

        time.Advance(TimeSpan.FromSeconds(1));
        notification.IsVisible.Should().BeFalse();
    }
}
