using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace DevNotes.Application.Settings;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,

    /// <summary>Windows key, Command on macOS, Super on Linux.</summary>
    Meta = 8,
}

/// <summary>
/// A system-wide keyboard shortcut as stored in the settings ("Ctrl+Alt+N"). At least one of
/// Ctrl, Alt or Meta is required so a plain letter can never be captured from every application.
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public const string DefaultText = "Ctrl+Alt+N";

    private static readonly string[] _namedKeys =
    [
        "Space", "Insert", "Delete", "Home", "End", "PageUp", "PageDown", "Up", "Down", "Left", "Right", "Enter", "Tab", "Escape", "Back",
    ];

    public static HotkeyGesture Default { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "N");

    public bool HasModifier(HotkeyModifiers modifier) => (Modifiers & modifier) != 0;

    /// <summary>Parses "Ctrl+Alt+N", "Control+Shift+F2", "Cmd+Option+Space"… Case and spaces do not matter.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        string? key = null;
        foreach (var rawPart in text.Split('+'))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
            {
                return false;
            }

            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "ALT" or "OPTION" or "OPT":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "META" or "WIN" or "WINDOWS" or "CMD" or "COMMAND" or "SUPER":
                    modifiers |= HotkeyModifiers.Meta;
                    continue;
                default:
                    break;
            }

            if (key is not null || !TryNormalizeKey(part, out key))
            {
                return false;
            }
        }

        if (key is null || (modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Meta)) == 0)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    /// <summary>Canonical text: "Ctrl+Alt+Shift+Meta+Key".</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (HasModifier(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (HasModifier(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (HasModifier(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (HasModifier(HotkeyModifiers.Meta))
        {
            parts.Add("Meta");
        }

        parts.Add(Key);
        return string.Join('+', parts);
    }

    /// <summary>Letters, digits, F1–F24 and a few named keys.</summary>
    private static bool TryNormalizeKey(string part, [NotNullWhen(true)] out string? key)
    {
        key = null;
        if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
        {
            key = part.ToUpperInvariant();
            return true;
        }

        if (part.Length is 2 or 3 && (part[0] is 'F' or 'f')
            && int.TryParse(part.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is >= 1 and <= 24)
        {
            key = "F" + number.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        foreach (var named in _namedKeys)
        {
            if (named.Equals(part, StringComparison.OrdinalIgnoreCase))
            {
                key = named;
                return true;
            }
        }

        return false;
    }
}
