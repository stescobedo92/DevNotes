using Avalonia;

namespace DevNotes.Desktop;

internal static class Program
{
    // Avalonia types must not be touched before the app builder has been started.
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the IDE previewer infrastructure.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
