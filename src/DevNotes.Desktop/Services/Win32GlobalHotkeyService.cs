using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using DevNotes.Application.Settings;
using DevNotes.Desktop.Resources;
using Microsoft.Extensions.Logging;

namespace DevNotes.Desktop.Services;

/// <summary>
/// Windows: <c>RegisterHotKey</c> against the main window. The system delivers a single
/// <c>WM_HOTKEY</c> message when the combination is pressed; no keyboard hook is installed, so
/// the app never sees any other keystroke.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class Win32GlobalHotkeyService : IGlobalHotkeyService
{
    private const int HotkeyId = 0x4E4F; // "NO": arbitrary, unique within the window.
    private const uint WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly ILogger<Win32GlobalHotkeyService> _logger;
    private readonly Win32Properties.CustomWndProcHookCallback _hook;
    private TopLevel? _window;
    private IntPtr _handle;
    private HotkeyGesture? _wanted;
    private bool _registered;
    private bool _disposed;

    public Win32GlobalHotkeyService(ILogger<Win32GlobalHotkeyService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hook = OnWindowMessage;
        State = HotkeyState.Inactive;
    }

    public event EventHandler? Pressed;

    public event EventHandler? StateChanged;

    public HotkeyState State { get; private set; }

    public bool CanRequestPermission => false;

    public void Attach(TopLevel window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_window is not null)
        {
            Win32Properties.RemoveWndProcHookCallback(_window, _hook);
        }

        _window = window;
        _handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Win32Properties.AddWndProcHookCallback(window, _hook);
        if (_wanted is { } wanted)
        {
            Register(wanted);
        }
    }

    public void Register(HotkeyGesture gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _wanted = gesture;
        UnregisterCore();

        if (_handle == IntPtr.Zero)
        {
            SetState(new HotkeyState(HotkeyStatus.Unsupported, Strings.Hotkey_NoWindow));
            return;
        }

        if (!TryGetVirtualKey(gesture.Key, out var virtualKey))
        {
            SetState(new HotkeyState(HotkeyStatus.Failed, Strings.Error_Hotkey));
            return;
        }

        var modifiers = ModNoRepeat
            | (gesture.HasModifier(HotkeyModifiers.Control) ? ModControl : 0)
            | (gesture.HasModifier(HotkeyModifiers.Alt) ? ModAlt : 0)
            | (gesture.HasModifier(HotkeyModifiers.Shift) ? ModShift : 0)
            | (gesture.HasModifier(HotkeyModifiers.Meta) ? ModWin : 0);

        if (RegisterHotKey(_handle, HotkeyId, modifiers, virtualKey))
        {
            _registered = true;
            SetState(HotkeyState.Active);
            return;
        }

        var error = Marshal.GetLastWin32Error();
        LogRegistrationFailed(gesture.ToString(), error);
        SetState(new HotkeyState(
            HotkeyStatus.Failed,
            error == ErrorHotkeyAlreadyRegistered ? Strings.Hotkey_InUse : new Win32Exception(error).Message));
    }

    public void Unregister()
    {
        _wanted = null;
        UnregisterCore();
        SetState(HotkeyState.Inactive);
    }

    public void RequestPermission()
    {
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterCore();
        if (_window is not null)
        {
            Win32Properties.RemoveWndProcHookCallback(_window, _hook);
            _window = null;
        }
    }

    /// <summary>Virtual-key codes of the keys <see cref="HotkeyGesture"/> accepts.</summary>
    internal static bool TryGetVirtualKey(string key, out uint virtualKey)
    {
        virtualKey = 0;
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            virtualKey = char.ToUpperInvariant(key[0]); // VK_A..VK_Z and VK_0..VK_9 equal the ASCII codes.
            return true;
        }

        if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var number) && number is >= 1 and <= 24)
        {
            virtualKey = 0x70u + (uint)(number - 1); // VK_F1
            return true;
        }

        virtualKey = key switch
        {
            "Space" => 0x20,
            "Insert" => 0x2D,
            "Delete" => 0x2E,
            "Home" => 0x24,
            "End" => 0x23,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            "Up" => 0x26,
            "Down" => 0x28,
            "Left" => 0x25,
            "Right" => 0x27,
            "Enter" => 0x0D,
            "Tab" => 0x09,
            "Escape" => 0x1B,
            "Back" => 0x08,
            _ => 0,
        };
        return virtualKey != 0;
    }

    private IntPtr OnWindowMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam == (IntPtr)HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    private void UnregisterCore()
    {
        if (_registered && _handle != IntPtr.Zero)
        {
            UnregisterHotKey(_handle, HotkeyId);
        }

        _registered = false;
    }

    private void SetState(HotkeyState state)
    {
        if (State != state)
        {
            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // DllImport instead of LibraryImport: the generated marshalling would require unsafe code in the whole
    // project for two calls that only pass integers.
#pragma warning disable SYSLIB1054
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
#pragma warning restore SYSLIB1054

    [LoggerMessage(EventId = 1000, Level = LogLevel.Warning, Message = "The global shortcut {Shortcut} could not be registered (Win32 error {Error})")]
    private partial void LogRegistrationFailed(string shortcut, int error);
}
