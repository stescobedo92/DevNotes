using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevNotes.Desktop.Services;

namespace DevNotes.Desktop.ViewModels;

public enum NotificationKind
{
    Info,
    Error,
}

/// <summary>Brief, non-blocking feedback for the user (a toast).</summary>
public interface INotificationService
{
    void Show(string message, NotificationKind kind = NotificationKind.Info);
}

/// <summary>
/// Shows one message at a time. Informational messages disappear on their own; errors stay until
/// dismissed so an actionable message is never missed.
/// </summary>
public sealed partial class NotificationViewModel : ObservableObject, INotificationService, IDisposable
{
    private static readonly TimeSpan _infoDuration = TimeSpan.FromSeconds(4);

    private readonly IUiDispatcher _dispatcher;
    private readonly ITimer _timer;

    public NotificationViewModel(IUiDispatcher dispatcher, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timer = timeProvider.CreateTimer(_ => _dispatcher.Post(Dismiss), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsError))]
    public partial NotificationKind Kind { get; private set; }

    public bool IsVisible => !string.IsNullOrEmpty(Message);

    public bool IsError => Kind == NotificationKind.Error;

    public void Show(string message, NotificationKind kind = NotificationKind.Info)
    {
        _dispatcher.Post(() =>
        {
            if (kind == NotificationKind.Info && IsVisible && IsError)
            {
                return; // The error stays until the user dismisses it; routine feedback is not worth losing it.
            }

            Kind = kind;
            Message = message;
            _timer.Change(kind == NotificationKind.Info ? _infoDuration : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        });
    }

    [RelayCommand]
    public void Dismiss()
    {
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Message = null;
    }

    public void Dispose() => _timer.Dispose();
}
