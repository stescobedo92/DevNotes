using DevNotes.Application.Settings;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.ViewModels;

namespace DevNotes.Desktop.Tests.Support;

/// <summary>Runs everything inline: for view-model tests that do not involve a UI thread.</summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    private readonly List<Task> _invoked = [];

    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Func<Task> action)
    {
        var task = action();
        lock (_invoked)
        {
            _invoked.Add(task);
        }

        return task;
    }

    /// <summary>Completes when the background work started so far (debounced saves and parses) has finished.</summary>
    public Task WhenIdleAsync()
    {
        lock (_invoked)
        {
            return Task.WhenAll(_invoked);
        }
    }
}

/// <summary>A UI thread that is busy with something else: what is posted waits until the test lets it run.</summary>
public sealed class QueuedUiDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _posted = new();

    public bool CheckAccess() => false;

    public void Post(Action action) => _posted.Enqueue(action);

    public Task InvokeAsync(Func<Task> action) => action();

    public void RunPending()
    {
        while (_posted.TryDequeue(out var action))
        {
            action();
        }
    }
}

public sealed class FakeFolderPicker : IFolderPicker
{
    /// <summary>Folder returned by the next call; null simulates the user cancelling the dialog.</summary>
    public string? NextResult { get; set; }

    public int Calls { get; private set; }

    public Task<string?> PickFolderAsync(string title)
    {
        Calls++;
        return Task.FromResult(NextResult);
    }
}

public sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; private set; }

    public Task SetTextAsync(string text)
    {
        Text = text;
        return Task.CompletedTask;
    }
}

public sealed class FakeLinkOpener : ILinkOpener
{
    public List<string?> Opened { get; } = [];

    public Task<bool> OpenAsync(string? url)
    {
        Opened.Add(url);
        return Task.FromResult(LinkPolicy.TryGetSafeUri(url, out _));
    }
}

public sealed class FakeThemeService : IThemeService
{
    public event EventHandler? AppearanceChanged;

    public bool IsDark { get; private set; } = true;

    public bool IsMotionEnabled { get; private set; } = true;

    public List<AppSettings> Applied { get; } = [];

    public void Apply(AppSettings settings)
    {
        Applied.Add(settings);
        var dark = settings.Theme != AppTheme.Light;
        var motion = !(settings.ReduceMotion ?? false);
        var changed = dark != IsDark || motion != IsMotionEnabled;
        IsDark = dark;
        IsMotionEnabled = motion;
        if (changed)
        {
            AppearanceChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>In-memory settings store whose saves can be held back, to simulate a slow disk.</summary>
public sealed class HeldSettingsStore : ISettingsStore
{
    private volatile TaskCompletionSource? _hold;

    public AppSettings Stored { get; private set; } = new();

    /// <summary>Makes every save wait until the returned action is invoked.</summary>
    public Action HoldSaves()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _hold = hold;
        return () =>
        {
            _hold = null;
            hold.TrySetResult();
        };
    }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Stored);

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (_hold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        Stored = settings;
    }
}

public sealed class FakeQuickCapturePresenter : IQuickCapturePresenter
{
    public int ShowCalls { get; private set; }

    public int HideCalls { get; private set; }

    public bool IsShown { get; private set; }

    public void Show()
    {
        ShowCalls++;
        IsShown = true;
    }

    public void Hide()
    {
        HideCalls++;
        IsShown = false;
    }
}

/// <summary>Global shortcut whose registration and key presses are driven by the test.</summary>
public sealed class FakeGlobalHotkeyService : IGlobalHotkeyService
{
    public HotkeyState State { get; private set; } = HotkeyState.Inactive;

    public event EventHandler? Pressed;

    public event EventHandler? StateChanged;

    public bool CanRequestPermission { get; set; }

    public HotkeyGesture? Registered { get; private set; }

    public int RegisterCalls { get; private set; }

    public int UnregisterCalls { get; private set; }

    public int PermissionRequests { get; private set; }

    public bool IsAttached { get; private set; }

    /// <summary>State reported after the next registration (to simulate a shortcut in use, an unsupported session…).</summary>
    public HotkeyState NextRegistrationState { get; set; } = HotkeyState.Active;

    public void Attach(Avalonia.Controls.TopLevel window) => IsAttached = true;

    public void Register(HotkeyGesture gesture)
    {
        RegisterCalls++;
        Registered = gesture;
        SetState(NextRegistrationState);
    }

    public void Unregister()
    {
        UnregisterCalls++;
        Registered = null;
        SetState(HotkeyState.Inactive);
    }

    public void RequestPermission() => PermissionRequests++;

    public void Press() => Pressed?.Invoke(this, EventArgs.Empty);

    public void SetState(HotkeyState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
    }
}

/// <summary>In-memory draft store: what the capture window would keep on disk.</summary>
public sealed class FakeQuickCaptureDraftStore : IQuickCaptureDraftStore
{
    public CaptureDraft? Stored { get; set; }

    public int Saves { get; private set; }

    public int Clears { get; private set; }

    public CaptureDraft? Load() => Stored;

    public void Save(CaptureDraft draft)
    {
        Saves++;
        Stored = draft.IsEmpty ? null : draft;
    }

    public void Clear()
    {
        Clears++;
        Stored = null;
    }
}

public sealed class RecordingNotifications : INotificationService
{
    public List<(string Message, NotificationKind Kind)> Shown { get; } = [];

    public IEnumerable<string> Errors => Shown.Where(item => item.Kind == NotificationKind.Error).Select(item => item.Message);

    public void Show(string message, NotificationKind kind = NotificationKind.Info) => Shown.Add((message, kind));
}

/// <summary>Unique temporary folder removed when the test finishes.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix = "devnotes-ui-")
    {
        Path = Directory.CreateTempSubdirectory(prefix).FullName;
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relativePath, string content)
    {
        var fullPath = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public string Read(string relativePath) => File.ReadAllText(Combine(relativePath.Split('/')));

    public bool Exists(string relativePath) => File.Exists(Combine(relativePath.Split('/')));

    public void Dispose()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }
}
