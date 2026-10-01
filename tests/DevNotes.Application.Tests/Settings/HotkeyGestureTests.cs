using DevNotes.Application.Settings;

namespace DevNotes.Application.Tests.Settings;

public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+N", HotkeyModifiers.Control | HotkeyModifiers.Alt, "N", "Ctrl+Alt+N")]
    [InlineData(" control + shift + f2 ", HotkeyModifiers.Control | HotkeyModifiers.Shift, "F2", "Ctrl+Shift+F2")]
    [InlineData("Cmd+Option+Space", HotkeyModifiers.Meta | HotkeyModifiers.Alt, "Space", "Alt+Meta+Space")]
    [InlineData("Win+9", HotkeyModifiers.Meta, "9", "Meta+9")]
    [InlineData("ALT+n", HotkeyModifiers.Alt, "N", "Alt+N")]
    [InlineData("Super+F24", HotkeyModifiers.Meta, "F24", "Meta+F24")]
    public void TryParse_AcceptsModifierAliasesAndNormalizes(string text, HotkeyModifiers modifiers, string key, string canonical)
    {
        HotkeyGesture.TryParse(text, out var gesture).Should().BeTrue();

        gesture.Modifiers.Should().Be(modifiers);
        gesture.Key.Should().Be(key);
        gesture.ToString().Should().Be(canonical);
        HotkeyGesture.TryParse(canonical, out var reparsed).Should().BeTrue();
        reparsed.Should().Be(gesture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("N")]
    [InlineData("Shift+N")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+N+M")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Ñ")]
    [InlineData("Ctrl++N")]
    [InlineData("Ctrl+Unknown")]
    public void TryParse_RejectsWhatCannotBeAGlobalShortcut(string? text)
    {
        HotkeyGesture.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public void Default_IsCtrlAltN()
    {
        HotkeyGesture.Default.ToString().Should().Be(HotkeyGesture.DefaultText);
        new QuickCaptureSettings().Gesture.Should().Be(HotkeyGesture.Default);
        new QuickCaptureSettings { Hotkey = "garbage" }.Gesture.Should().Be(HotkeyGesture.Default);
    }

    [Fact]
    public void Normalize_RepairsHotkeyAndProjects()
    {
        // "Absolute" depends on the system: a Windows path is not one on Linux or macOS.
        var repository = OperatingSystem.IsWindows() ? "C:\\src\\azure\\" : "/src/azure/";
        var settings = new AppSettings
        {
            QuickCapture = new QuickCaptureSettings { Hotkey = "ctrl + alt + k", GlobalHotkeyEnabled = false },
            Projects =
            [
                new ProjectSettings("  Azure ", $"  {repository} "),
                new ProjectSettings("AZURE", null),
                new ProjectSettings("relative", "src/relative"),
                new ProjectSettings("   ", repository),
                null!,
            ],
        };

        var normalized = settings.Normalize();

        normalized.QuickCapture.Should().Be(new QuickCaptureSettings { Hotkey = "Ctrl+Alt+K", GlobalHotkeyEnabled = false });
        normalized.Projects.Should().Equal(
            new ProjectSettings("Azure", repository),
            new ProjectSettings("relative", null));
        new AppSettings { QuickCapture = new QuickCaptureSettings { Hotkey = "broken" } }.Normalize().QuickCapture.Hotkey.Should().Be("Ctrl+Alt+N");
    }

    [Fact]
    public void FindProject_AndWithProjectRepository_IgnoreCase()
    {
        var settings = new AppSettings().WithProjectRepository("Azure", "C:\\src\\azure");

        settings.FindProject("azure")!.RepositoryPath.Should().Be("C:\\src\\azure");
        settings.FindProject("other").Should().BeNull();
        settings.FindProject(" ").Should().BeNull();

        var replaced = settings.WithProjectRepository("AZURE", "D:\\azure");
        replaced.Projects.Should().ContainSingle().Which.Should().Be(new ProjectSettings("AZURE", "D:\\azure"));

        settings.WithProjectRepository("azure", null).Projects.Should().BeEmpty();
        settings.WithProjectRepository("azure", "  ").Projects.Should().BeEmpty();
    }
}
