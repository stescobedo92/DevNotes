using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Styling;
using DevNotes.Application.Settings;

namespace DevNotes.Desktop.Services;

/// <summary>Applies the appearance settings (theme, font scale, density, motion) to the running app.</summary>
public interface IThemeService
{
    /// <summary>True when the effective theme is dark (after resolving "follow system").</summary>
    bool IsDark { get; }

    bool IsMotionEnabled { get; }

    /// <summary>Raised on the UI thread when the effective theme or the motion preference changed.</summary>
    event EventHandler? AppearanceChanged;

    void Apply(AppSettings settings);
}

public sealed class ThemeService : IThemeService
{
    private static readonly Thickness _comfortableListItem = new(12, 8);
    private static readonly Thickness _compactListItem = new(12, 4);
    private static readonly Thickness _comfortableSidebarItem = new(12, 6);
    private static readonly Thickness _compactSidebarItem = new(12, 3);

    private bool _subscribed;

    public event EventHandler? AppearanceChanged;

    public bool IsDark => Avalonia.Application.Current?.ActualThemeVariant != ThemeVariant.Light;

    public bool IsMotionEnabled { get; private set; } = true;

    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Avalonia.Application.Current is not { } app)
        {
            return;
        }

        if (!_subscribed)
        {
            // Covers the operating system switching between light and dark while "follow system" is active.
            app.ActualThemeVariantChanged += (_, _) => AppearanceChanged?.Invoke(this, EventArgs.Empty);
            _subscribed = true;
        }

        app.RequestedThemeVariant = settings.Theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.System => ThemeVariant.Default,
            _ => ThemeVariant.Dark,
        };

        // Typography scale: every size derives from the base size so Ctrl +/- zooms the whole UI.
        var size = settings.FontSize;
        var resources = app.Resources;
        resources["FontSizeBase"] = size;
        resources["FontSizeSmall"] = Math.Round(size * 0.86);
        resources["FontSizeCode"] = Math.Round(size * 0.93);
        resources["FontSizeLarge"] = Math.Round(size * 1.14);
        resources["FontSizeH4"] = Math.Round(size * 1.14);
        resources["FontSizeH3"] = Math.Round(size * 1.29);
        resources["FontSizeH2"] = Math.Round(size * 1.5);
        resources["FontSizeH1"] = Math.Round(size * 1.86);

        var compact = settings.Density == UiDensity.Compact;
        resources["InsetListItem"] = compact ? _compactListItem : _comfortableListItem;
        resources["InsetSidebarItem"] = compact ? _compactSidebarItem : _comfortableSidebarItem;

        var motion = !(settings.ReduceMotion ?? MotionPreference.SystemPrefersReducedMotion());
        var motionChanged = motion != IsMotionEnabled;
        IsMotionEnabled = motion;
        if (motionChanged)
        {
            AppearanceChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// Reads the operating-system "reduce motion" preference. Avalonia does not expose it, so it is
/// queried natively on Windows; on other platforms the explicit app setting is the only source.
/// </summary>
internal static class MotionPreference
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    public static bool SystemPrefersReducedMotion() =>
        OperatingSystem.IsWindows() && WindowsAnimationsDisabled();

    [SupportedOSPlatform("windows")]
    private static bool WindowsAnimationsDisabled() =>
        SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var animationsEnabled, 0) && !animationsEnabled;

    [DllImport("user32.dll", SetLastError = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);
}
