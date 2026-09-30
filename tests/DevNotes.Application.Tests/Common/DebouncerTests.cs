using DevNotes.Application.Common;
using Microsoft.Extensions.Time.Testing;

namespace DevNotes.Application.Tests.Common;

public sealed class DebouncerTests
{
    private static readonly TimeSpan _delay = TimeSpan.FromMilliseconds(300);
    private readonly FakeTimeProvider _time = new();
    private readonly List<Exception> _errors = [];

    [Fact]
    public void Signal_RunsOnceAfterQuietPeriod()
    {
        var runs = 0;
        using var debouncer = Create(() => runs++);

        debouncer.Signal();
        _time.Advance(TimeSpan.FromMilliseconds(299));
        runs.Should().Be(0);
        debouncer.IsPending.Should().BeTrue();

        _time.Advance(TimeSpan.FromMilliseconds(1));

        runs.Should().Be(1);
        debouncer.IsPending.Should().BeFalse();
    }

    [Fact]
    public void Signal_BurstOfSignals_CollapsesIntoOneRunAndRestartsTheTimer()
    {
        var runs = 0;
        using var debouncer = Create(() => runs++);

        for (var i = 0; i < 10; i++)
        {
            debouncer.Signal();
            _time.Advance(TimeSpan.FromMilliseconds(200));
        }

        runs.Should().Be(0, "each signal restarts the quiet period");

        _time.Advance(_delay);

        runs.Should().Be(1);
    }

    [Fact]
    public void Cancel_DropsThePendingRun()
    {
        var runs = 0;
        using var debouncer = Create(() => runs++);

        debouncer.Signal();
        debouncer.Cancel();
        _time.Advance(_delay * 2);

        runs.Should().Be(0);
        debouncer.IsPending.Should().BeFalse();
    }

    [Fact]
    public async Task FlushAsync_RunsPendingWorkImmediately()
    {
        var runs = 0;
        using var debouncer = Create(() => runs++);

        debouncer.Signal();
        await debouncer.FlushAsync();

        runs.Should().Be(1);

        _time.Advance(_delay * 2);
        runs.Should().Be(1, "the flushed run must not be repeated by the timer");
    }

    [Fact]
    public async Task FlushAsync_NothingPending_DoesNotRun()
    {
        var runs = 0;
        using var debouncer = Create(() => runs++);

        await debouncer.FlushAsync();

        runs.Should().Be(0);
    }

    [Fact]
    public async Task Runs_NeverOverlap_AndSignalsDuringARunScheduleAnother()
    {
        var concurrent = 0;
        var maxConcurrent = 0;
        var runs = 0;
        var firstRunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var debouncer = new Debouncer(
            _time,
            _delay,
            async _ =>
            {
                maxConcurrent = Math.Max(maxConcurrent, Interlocked.Increment(ref concurrent));
                if (Interlocked.Increment(ref runs) == 1)
                {
                    firstRunStarted.SetResult();
                    await releaseFirstRun.Task;
                }

                Interlocked.Decrement(ref concurrent);
            },
            _errors.Add);

        debouncer.Signal();
        _time.Advance(_delay);
        await firstRunStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        debouncer.Signal(); // arrives while the first run is still executing
        var flush = debouncer.FlushAsync();
        flush.IsCompleted.Should().BeFalse("the second run must wait for the first one");

        releaseFirstRun.SetResult();
        await flush.WaitAsync(TestContext.Current.CancellationToken);

        runs.Should().Be(2);
        maxConcurrent.Should().Be(1);
        _errors.Should().BeEmpty();
    }

    [Fact]
    public void ActionFailure_IsReportedToTheErrorHandler_AndDebouncerKeepsWorking()
    {
        var attempts = 0;
        using var debouncer = new Debouncer(
            _time,
            _delay,
            _ => ++attempts == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask,
            _errors.Add);

        debouncer.Signal();
        _time.Advance(_delay);
        debouncer.Signal();
        _time.Advance(_delay);

        attempts.Should().Be(2);
        _errors.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("boom");
    }

    [Fact]
    public async Task Dispose_StopsEverything()
    {
        var runs = 0;
        var debouncer = Create(() => runs++);

        debouncer.Signal();
        debouncer.Dispose();
        debouncer.Dispose(); // idempotent
        debouncer.Signal();
        debouncer.Cancel();
        _time.Advance(_delay * 2);
        await debouncer.FlushAsync();

        runs.Should().Be(0);
        debouncer.IsPending.Should().BeFalse();
    }

    [Fact]
    public void Constructor_InvalidArguments_Throw()
    {
        FluentActions.Invoking(() => new Debouncer(null!, _delay, _ => Task.CompletedTask, _errors.Add)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new Debouncer(_time, _delay, null!, _errors.Add)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new Debouncer(_time, _delay, _ => Task.CompletedTask, null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new Debouncer(_time, TimeSpan.FromSeconds(-1), _ => Task.CompletedTask, _errors.Add))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    private Debouncer Create(Action action) =>
        new(
            _time,
            _delay,
            _ =>
            {
                action();
                return Task.CompletedTask;
            },
            _errors.Add);
}
