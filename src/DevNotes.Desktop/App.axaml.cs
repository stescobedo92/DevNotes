using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DevNotes.Desktop.Views;

namespace DevNotes.Desktop;

// Fully qualified: inside this assembly `Application` binds to the DevNotes.Application namespace.
public sealed class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
