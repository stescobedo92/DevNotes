using Avalonia.Controls;
using DevNotes.Application.Settings;

namespace DevNotes.Desktop.Services;

public enum HotkeyStatus
{
    /// <summary>Nothing registered (disabled in the settings, or not registered yet).</summary>
    Inactive,

    /// <summary>The shortcut works system-wide.</summary>
    Active,

    /// <summary>This platform or session cannot provide system-wide shortcuts; the in-app shortcut still works.</summary>
    Unsupported,

    /// <summary>Registration was attempted and failed (shortcut in use, hook could not start…).</summary>
    Failed,
}

/// <param name="Status">Whether the shortcut works.</param>
/// <param name="Reason">Localized explanation for <see cref="HotkeyStatus.Unsupported"/> and <see cref="HotkeyStatus.Failed"/>.</param>
public sealed record HotkeyState(HotkeyStatus Status, string? Reason = null)
{
    public static HotkeyState Inactive { get; } = new(HotkeyStatus.Inactive);

    public static HotkeyState Active { get; } = new(HotkeyStatus.Active);
}

/// <summary>
/// A shortcut that works while another application has the focus. Implementations never see
/// (let alone keep) anything but whether the configured combination was pressed.
/// </summary>
public interface IGlobalHotkeyService : IDisposable
{
    HotkeyState State { get; }

    /// <summary>Raised when the shortcut is pressed. May be raised on any thread.</summary>
    event EventHandler? Pressed;

    /// <summary>Raised after <see cref="State"/> changed. May be raised on any thread.</summary>
    event EventHandler? StateChanged;

    /// <summary>Whether the user can be asked for the permission the platform needs (macOS Accessibility).</summary>
    bool CanRequestPermission { get; }

    /// <summary>Gives the service the main window; some platforms register shortcuts against a window.</summary>
    void Attach(TopLevel window);

    /// <summary>Registers (or replaces) the shortcut. The outcome is reported through <see cref="State"/>.</summary>
    void Register(HotkeyGesture gesture);

    void Unregister();

    /// <summary>Asks the operating system for the permission, then registers again.</summary>
    void RequestPermission();
}

/// <summary>Used where system-wide shortcuts cannot exist (headless tests, unknown platforms).</summary>
public sealed class UnavailableGlobalHotkeyService(string reason) : IGlobalHotkeyService
{
    public HotkeyState State { get; } = new(HotkeyStatus.Unsupported, reason);

    public event EventHandler? Pressed
    {
        add
        {
        }

        remove
        {
        }
    }

    public event EventHandler? StateChanged
    {
        add
        {
        }

        remove
        {
        }
    }

    public bool CanRequestPermission => false;

    public void Attach(TopLevel window)
    {
    }

    public void Register(HotkeyGesture gesture)
    {
    }

    public void Unregister()
    {
    }

    public void RequestPermission()
    {
    }

    public void Dispose()
    {
    }
}
