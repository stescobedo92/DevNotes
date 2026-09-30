using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DevNotes.Desktop.Views;

namespace DevNotes.Desktop.Tests.Support;

/// <summary>Helpers for headless UI tests: showing the window, keyboard input and waiting for async work.</summary>
public static class UiTest
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(20);

    public static MainWindow ShowMainWindow(DesktopHarness harness)
    {
        var window = new MainWindow { DataContext = harness.Shell };
        window.ApplyLayout(harness.Shell.Layout);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>Pumps the dispatcher until the condition holds (file I/O and indexing run on other threads).</summary>
    public static async Task WaitForAsync(Func<bool> condition, string description = "the expected state")
    {
        var deadline = DateTime.UtcNow + _timeout;
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException($"Timed out waiting for {description}.");
            }

            await Task.Delay(15);
        }
    }

    /// <summary>The modifier the window uses for its primary shortcuts (Ctrl, or Cmd on macOS).</summary>
    public static KeyModifiers PrimaryModifier(this Window window) =>
        window.KeyBindings.First(binding => binding.Gesture.Key == Key.S).Gesture.KeyModifiers;

    /// <summary>Presses and releases a key, then lets the UI process the result.</summary>
    public static void Press(this Window window, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var raw = RawInputModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            raw |= RawInputModifiers.Control;
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            raw |= RawInputModifiers.Meta;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            raw |= RawInputModifiers.Shift;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            raw |= RawInputModifiers.Alt;
        }

        var physical = ToPhysicalKey(key);
        window.KeyPress(key, raw, physical, null);
        window.KeyRelease(key, raw, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Presses a primary-modifier shortcut such as Ctrl+N (Cmd+N on macOS).</summary>
    public static void PressPrimary(this Window window, Key key, bool shift = false) =>
        window.Press(key, window.PrimaryModifier() | (shift ? KeyModifiers.Shift : KeyModifiers.None));

    public static void Type(this Window window, string text)
    {
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    public static T FindDescendant<T>(this Control root, string? name = null)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(control => name is null || control.Name == name);

    public static IEnumerable<T> FindDescendants<T>(this Control root)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>();

    private static PhysicalKey ToPhysicalKey(Key key) => key switch
    {
        Key.B => PhysicalKey.B,
        Key.E => PhysicalKey.E,
        Key.F => PhysicalKey.F,
        Key.Z => PhysicalKey.Z,
        Key.K => PhysicalKey.K,
        Key.N => PhysicalKey.N,
        Key.P => PhysicalKey.P,
        Key.S => PhysicalKey.S,
        Key.I => PhysicalKey.I,
        Key.F2 => PhysicalKey.F2,
        Key.Enter => PhysicalKey.Enter,
        Key.Escape => PhysicalKey.Escape,
        Key.Down => PhysicalKey.ArrowDown,
        Key.Up => PhysicalKey.ArrowUp,
        Key.OemPlus => PhysicalKey.Equal,
        Key.OemMinus => PhysicalKey.Minus,
        Key.D0 => PhysicalKey.Digit0,
        _ => PhysicalKey.None,
    };
}
