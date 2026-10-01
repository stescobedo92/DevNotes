using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using DevNotes.Application.Settings;
using DevNotes.Desktop.Resources;
using Microsoft.Extensions.Logging;
using SharpHook;
using SharpHook.Data;
using SharpHook.Providers;

namespace DevNotes.Desktop.Services;

/// <summary>
/// macOS and Linux: a keyboard-only global hook (libuiohook through SharpHook). The handler
/// compares each key press with the configured combination and discards it; nothing is stored,
/// logged or forwarded. On macOS the hook needs the Accessibility permission, which is only
/// requested when the user asks for it; on Wayland the hook needs input-device permissions and
/// simply reports itself unavailable without them.
/// </summary>
public sealed partial class SharpHookGlobalHotkeyService : IGlobalHotkeyService
{
    private static readonly EventMask _lockKeys = EventMask.CapsLock | EventMask.NumLock | EventMask.ScrollLock;

    /// <summary>How long the previous hook may take to unwind before a new one is started (libuiohook allows one per process).</summary>
    private static readonly TimeSpan _stopGracePeriod = TimeSpan.FromSeconds(3);

    /// <summary>After asking for the macOS permission, how often and for how long the answer is polled.</summary>
    private static readonly TimeSpan _permissionPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _permissionPollDuration = TimeSpan.FromMinutes(3);

    private readonly ILogger<SharpHookGlobalHotkeyService> _logger;
    private readonly Lock _gate = new();
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Disposed by StopHook, which Dispose calls; the field is exchanged atomically because the hook thread may stop it first.")]
    private SimpleGlobalHook? _hook;
    private Task _previousRun = Task.CompletedTask;
    private CancellationTokenSource? _permissionPoll;
    private HotkeyGesture? _wanted;
    private KeyCode _keyCode;
    private EventMask _requiredMask;
    private bool _disposed;

    public SharpHookGlobalHotkeyService(ILogger<SharpHookGlobalHotkeyService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        State = HotkeyState.Inactive;
    }

    public event EventHandler? Pressed;

    public event EventHandler? StateChanged;

    public HotkeyState State { get; private set; }

    public bool CanRequestPermission => OperatingSystem.IsMacOS() && State.Status != HotkeyStatus.Active;

    public void Attach(TopLevel window)
    {
    }

    public void Register(HotkeyGesture gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryGetKeyCode(gesture.Key, out var keyCode))
        {
            SetState(new HotkeyState(HotkeyStatus.Failed, Strings.Error_Hotkey));
            return;
        }

        lock (_gate)
        {
            _wanted = gesture;
            _keyCode = keyCode;
            _requiredMask = (gesture.HasModifier(HotkeyModifiers.Control) ? EventMask.Ctrl : EventMask.None)
                | (gesture.HasModifier(HotkeyModifiers.Alt) ? EventMask.Alt : EventMask.None)
                | (gesture.HasModifier(HotkeyModifiers.Shift) ? EventMask.Shift : EventMask.None)
                | (gesture.HasModifier(HotkeyModifiers.Meta) ? EventMask.Meta : EventMask.None);
        }

        if (_hook is not null)
        {
            return; // The running hook reads the new combination on the next key press.
        }

        if (OperatingSystem.IsMacOS() && !UioHookProvider.Instance.IsAxApiEnabled(promptUserIfDisabled: false))
        {
            SetState(new HotkeyState(HotkeyStatus.Unsupported, Strings.Hotkey_NeedsAccessibility));
            return;
        }

        StartHook();
    }

    public void Unregister()
    {
        lock (_gate)
        {
            _wanted = null;
        }

        StopHook();
        SetState(HotkeyState.Inactive);
    }

    public void RequestPermission()
    {
        if (!OperatingSystem.IsMacOS() || _disposed)
        {
            return;
        }

        // The system dialog sends the user to System Settings and returns at once with the current
        // (still denied) answer, so the grant is polled for a while and the hook starts when it arrives.
        if (UioHookProvider.Instance.IsAxApiEnabled(promptUserIfDisabled: true))
        {
            RegisterWanted();
            return;
        }

        _permissionPoll?.Cancel();
        _permissionPoll?.Dispose();
        var poll = new CancellationTokenSource(_permissionPollDuration);
        _permissionPoll = poll;
        _ = PollPermissionAsync(poll.Token);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _permissionPoll?.Cancel();
        _permissionPoll?.Dispose();
        _permissionPoll = null;
        StopHook();
    }

    private async Task PollPermissionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_permissionPollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (UioHookProvider.Instance.IsAxApiEnabled(promptUserIfDisabled: false))
                {
                    RegisterWanted();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The user did not grant access within the polling window, or the service was disposed.
        }
    }

    private void RegisterWanted()
    {
        HotkeyGesture? wanted;
        lock (_gate)
        {
            wanted = _wanted;
        }

        if (wanted is { } gesture && !_disposed)
        {
            Register(gesture);
        }
    }

    /// <summary>Key codes of the keys <see cref="HotkeyGesture"/> accepts.</summary>
    internal static bool TryGetKeyCode(string key, out KeyCode keyCode)
    {
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return Enum.TryParse("Vc" + char.ToUpperInvariant(key[0]), out keyCode);
        }

        if (key.Length is 2 or 3 && key[0] == 'F')
        {
            return Enum.TryParse("Vc" + key, out keyCode);
        }

        var name = key switch
        {
            "Back" => "VcBackspace",
            "Enter" => "VcEnter",
            _ => "Vc" + key,
        };
        return Enum.TryParse(name, out keyCode);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "libuiohook reports platform failures in several ways; every one of them only means the shortcut is unavailable.")]
    private void StartHook()
    {
        if (!_previousRun.IsCompleted)
        {
            // Only one hook may run per process: let the one being stopped finish unwinding first.
            _ = _previousRun.ContinueWith(_ => StartHookNow(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return;
        }

        StartHookNow();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "libuiohook reports platform failures in several ways; every one of them only means the shortcut is unavailable.")]
    private void StartHookNow()
    {
        if (_disposed || _hook is not null)
        {
            return;
        }

        SimpleGlobalHook? hook = null;
        try
        {
            UioHookProvider.Instance.PromptUserIfAxApiDisabled = false;
            hook = new SimpleGlobalHook();
            hook.HookEnabled += OnHookEnabled;
            hook.KeyPressed += OnKeyPressed;
            _hook = hook;
            var running = hook.RunAsync(GlobalHookType.Keyboard, useBackgroundThread: true);
            _previousRun = running.ContinueWith(
                task => OnHookStopped(hook, task.Exception?.GetBaseException()),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).WaitAsync(_stopGracePeriod).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        catch (Exception exception)
        {
            OnHookStopped(hook, exception);
        }
    }

    private void StopHook()
    {
        var hook = Interlocked.Exchange(ref _hook, null);
        if (hook is null)
        {
            return;
        }

        hook.KeyPressed -= OnKeyPressed;
        hook.HookEnabled -= OnHookEnabled;
        try
        {
            hook.Dispose();
        }
        catch (HookException exception)
        {
            LogHookStopFailed(exception);
        }
    }

    private void OnHookEnabled(object? sender, HookEventArgs e) => SetState(HotkeyState.Active);

    private void OnHookStopped(SimpleGlobalHook? hook, Exception? exception)
    {
        if (hook is null || !ReferenceEquals(hook, Interlocked.CompareExchange(ref _hook, null, hook)))
        {
            return; // Stopped on purpose (Unregister / Dispose).
        }

        hook.KeyPressed -= OnKeyPressed;
        hook.HookEnabled -= OnHookEnabled;
        hook.Dispose();
        if (exception is null)
        {
            SetState(HotkeyState.Inactive);
            return;
        }

        LogHookFailed(exception);
        var isWayland = OperatingSystem.IsLinux()
            && string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);
        SetState(new HotkeyState(
            isWayland ? HotkeyStatus.Unsupported : HotkeyStatus.Failed,
            isWayland ? Strings.Hotkey_Wayland : ErrorMessages.Format(Strings.Hotkey_HookFailed, exception.Message)));
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        KeyCode keyCode;
        EventMask required;
        lock (_gate)
        {
            if (_wanted is null)
            {
                return;
            }

            keyCode = _keyCode;
            required = _requiredMask;
        }

        // Only the configured combination is of interest; lock keys do not count and every other key press is dropped here.
        var mask = e.RawEvent.Mask & ~_lockKeys & ~EventMask.SimulatedEvent;
        if (e.Data.KeyCode == keyCode && Normalize(mask) == required)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Left and right variants collapse into the combined flags so either side of the keyboard works.</summary>
    private static EventMask Normalize(EventMask mask)
    {
        var result = EventMask.None;
        if ((mask & EventMask.Ctrl) != 0)
        {
            result |= EventMask.Ctrl;
        }

        if ((mask & EventMask.Alt) != 0)
        {
            result |= EventMask.Alt;
        }

        if ((mask & EventMask.Shift) != 0)
        {
            result |= EventMask.Shift;
        }

        if ((mask & EventMask.Meta) != 0)
        {
            result |= EventMask.Meta;
        }

        return result;
    }

    private void SetState(HotkeyState state)
    {
        if (State != state)
        {
            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [LoggerMessage(EventId = 1001, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "The global keyboard hook could not run; the quick-capture shortcut only works inside the app")]
    private partial void LogHookFailed(Exception exception);

    [LoggerMessage(EventId = 1002, Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Stopping the global keyboard hook failed")]
    private partial void LogHookStopFailed(Exception exception);
}
