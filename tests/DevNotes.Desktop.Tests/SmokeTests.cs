using Avalonia.Headless.XUnit;
using DevNotes.Desktop.Views;

namespace DevNotes.Desktop.Tests;

public sealed class SmokeTests
{
    [AvaloniaFact]
    public void MainWindow_Opens_InHeadlessPlatform()
    {
        var window = new MainWindow();

        window.Show();

        window.IsVisible.Should().BeTrue();
        window.Title.Should().Be("DevNotes");
    }
}
