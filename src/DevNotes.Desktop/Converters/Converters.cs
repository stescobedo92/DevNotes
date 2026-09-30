using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace DevNotes.Desktop.Converters;

/// <summary>
/// Derives a stable colour from a name (tag or project): the same name always gets the same hue,
/// on every machine and in every session. Text on chips keeps using the primary text token, so
/// contrast does not depend on the generated colour.
/// </summary>
public sealed class NameToBrushConverter : IValueConverter
{
    private const double Saturation = 0.62;
    private const double Lightness = 0.55;

    private readonly byte _alpha;

    private NameToBrushConverter(byte alpha) => _alpha = alpha;

    /// <summary>Solid colour, for borders and the project dot.</summary>
    public static NameToBrushConverter Solid { get; } = new(0xFF);

    /// <summary>Translucent tint of the same colour, for chip backgrounds.</summary>
    public static NameToBrushConverter Tint { get; } = new(0x30);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } name ? new ImmutableSolidColorBrush(GetColor(name, _alpha)) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public static Color GetColor(string name, byte alpha = 0xFF)
    {
        ArgumentNullException.ThrowIfNull(name);

        // FNV-1a: deterministic across processes (string.GetHashCode is randomized per run).
        var hash = 2166136261u;
        foreach (var c in name)
        {
            hash = (hash ^ char.ToLowerInvariant(c)) * 16777619u;
        }

        var rgb = new HslColor(1, hash % 360, Saturation, Lightness).ToRgb();
        return new Color(alpha, rgb.R, rgb.G, rgb.B);
    }
}

/// <summary>Indents outline entries by heading level: (level − 1) × step on the left.</summary>
public sealed class HeadingIndentConverter : IValueConverter
{
    private const double Step = 12;

    public static HeadingIndentConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(value is int level ? Math.Max(level - 1, 0) * Step : 0, 0, 0, 0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
