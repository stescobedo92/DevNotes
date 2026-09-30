using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace DevNotes.Desktop.Services;

/// <summary>
/// Gives UI services access to the main window once it exists. Services are created by the
/// container before any window, so they resolve the top level lazily through this holder.
/// </summary>
public sealed class TopLevelAccessor
{
    public TopLevel? Current { get; set; }
}

public interface IFolderPicker
{
    /// <summary>Lets the user choose a folder; returns null when the dialog is cancelled.</summary>
    Task<string?> PickFolderAsync(string title);
}

public interface IClipboardService
{
    Task SetTextAsync(string text);
}

public interface ILinkOpener
{
    /// <summary>Opens a link in the default application. Only safe schemes are allowed.</summary>
    Task<bool> OpenAsync(string? url);
}

public sealed class AvaloniaFolderPicker(TopLevelAccessor topLevel) : IFolderPicker
{
    public async Task<string?> PickFolderAsync(string title)
    {
        if (topLevel.Current is not { } window)
        {
            return null;
        }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}

public sealed class AvaloniaClipboardService(TopLevelAccessor topLevel) : IClipboardService
{
    public Task SetTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return topLevel.Current?.Clipboard is { } clipboard ? clipboard.SetTextAsync(text) : Task.CompletedTask;
    }
}

public sealed class AvaloniaLinkOpener(TopLevelAccessor topLevel) : ILinkOpener
{
    public async Task<bool> OpenAsync(string? url)
    {
        if (!LinkPolicy.TryGetSafeUri(url, out var uri) || topLevel.Current is not { } window)
        {
            return false;
        }

        return await window.Launcher.LaunchUriAsync(uri);
    }
}

/// <summary>
/// Decides which links found in notes may be handed to the operating system. Notes can come from
/// cloned repositories, so anything that could start a program (file:, custom protocol handlers,
/// UNC paths…) is refused; only web and mail links are opened.
/// </summary>
public static class LinkPolicy
{
    public static bool TryGetSafeUri(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeMailto)
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}
