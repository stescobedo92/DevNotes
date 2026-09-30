using Avalonia;
using Avalonia.Headless;
using DevNotes.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace DevNotes.Desktop.Tests;

public static class TestAppBuilder
{
    /// <summary>
    /// The real <see cref="App"/> (tokens, styles, Fluent theme) on the headless platform. Skia
    /// rendering is enabled so tests can capture frames; the bundled Inter font makes text
    /// rendering independent of the fonts installed on the machine.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
