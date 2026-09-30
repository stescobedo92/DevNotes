using DevNotes.Application.Indexing;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Tests.Indexing;

public sealed class VaultEventHubTests
{
    [Fact]
    public void Publish_NotifiesEverySubscriber_AndKeepsTheLatestStatus()
    {
        var hub = new VaultEventHub();
        var changes = new List<NotesChangedEventArgs>();
        var statuses = new List<IndexStatus>();
        hub.NotesChanged += (_, e) => changes.Add(e);
        hub.IndexStatusChanged += (_, status) => statuses.Add(status);
        var indexing = new IndexStatus(IndexState.Indexing, new IndexProgress(1, 2), null);

        hub.PublishNotesChanged(NotesChangeSource.Local, NotePath.Create("a.md"));
        hub.PublishStatus(indexing);

        changes.Should().ContainSingle().Which.Paths.Should().Equal(NotePath.Create("a.md"));
        statuses.Should().Equal(indexing);
        hub.Status.Should().BeSameAs(indexing);
    }

    [Fact]
    public void FailingSubscriber_IsReported_AndTheOthersAreStillNotified()
    {
        var reported = new List<Exception>();
        var hub = new VaultEventHub(reported.Add);
        var notified = 0;
        hub.NotesChanged += (_, _) => throw new InvalidOperationException("first subscriber");
        hub.NotesChanged += (_, _) => notified++;
        hub.IndexStatusChanged += (_, _) => throw new InvalidOperationException("status subscriber");

        hub.PublishNotesChanged(NotesChangeSource.Scan);
        hub.PublishStatus(IndexStatus.Idle);

        notified.Should().Be(1);
        reported.Select(exception => exception.Message).Should().Equal("first subscriber", "status subscriber");
    }

    [Fact]
    public void FailingSubscriber_WithoutAReporter_IsRethrownAfterEveryoneWasNotified()
    {
        var hub = new VaultEventHub();
        var notified = 0;
        hub.NotesChanged += (_, _) => throw new InvalidOperationException("bug");
        hub.NotesChanged += (_, _) => notified++;

        var act = () => hub.PublishNotesChanged(NotesChangeSource.Scan);

        act.Should().Throw<AggregateException>().Which.InnerExceptions.Should().ContainSingle().Which.Message.Should().Be("bug");
        notified.Should().Be(1, "a failure is never silent, but it does not rob the other subscribers of the event");
    }

    [Fact]
    public void Publish_WithoutSubscribers_DoesNothing()
    {
        var hub = new VaultEventHub();

        hub.PublishNotesChanged(NotesChangeSource.Scan);
        hub.PublishStatus(IndexStatus.Idle);

        hub.Status.Should().Be(IndexStatus.Idle);
    }
}
