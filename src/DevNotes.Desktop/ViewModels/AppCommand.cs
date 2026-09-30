using System.Windows.Input;

namespace DevNotes.Desktop.ViewModels;

/// <summary>
/// Platform-neutral keyboard shortcut. <see cref="Primary"/> is Ctrl on Windows and Linux and
/// Cmd on macOS; the window translates it when it registers the key bindings.
/// </summary>
/// <param name="Key">Name of the key as in <c>Avalonia.Input.Key</c> ("N", "F2", "OemPlus"…).</param>
/// <param name="Primary">Whether the primary command modifier is part of the shortcut.</param>
/// <param name="Shift">Whether Shift is part of the shortcut.</param>
/// <param name="Alt">Whether Alt (Option on macOS) is part of the shortcut.</param>
public sealed record ShortcutKey(string Key, bool Primary = false, bool Shift = false, bool Alt = false)
{
    /// <summary>Text shown to the user, e.g. "Ctrl+Shift+P" or "⌘⇧P".</summary>
    public string DisplayText => Format(OperatingSystem.IsMacOS());

    public string Format(bool macStyle)
    {
        var key = Key switch
        {
            "OemPlus" => "+",
            "OemMinus" => "-",
            "D0" => "0",
            _ => Key,
        };

        if (macStyle)
        {
            return (Primary ? "⌘" : string.Empty) + (Alt ? "⌥" : string.Empty) + (Shift ? "⇧" : string.Empty) + key;
        }

        return (Primary ? "Ctrl+" : string.Empty) + (Alt ? "Alt+" : string.Empty) + (Shift ? "Shift+" : string.Empty) + key;
    }
}

/// <summary>An action of the app: shown in the command palette and optionally bound to a shortcut.</summary>
public sealed record AppCommand(string Id, string Title, ICommand Command, ShortcutKey? Shortcut = null)
{
    public string ShortcutText => Shortcut?.DisplayText ?? string.Empty;
}
