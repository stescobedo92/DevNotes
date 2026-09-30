using DevNotes.Application.Settings;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.ViewModels;

namespace DevNotes.Desktop.Tests.Support;

/// <summary>Runs everything inline: for view-model tests that do not involve a UI thread.</summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Func<Task> action) => action();
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
