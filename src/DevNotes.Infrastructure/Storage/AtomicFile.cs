namespace DevNotes.Infrastructure.Storage;

/// <summary>
/// Crash-safe file replacement: the content is written to a temporary file in the same folder,
/// flushed to the device and then renamed over the destination. A reader (or a crash) sees either
/// the old file or the new one, never a partial write.
/// </summary>
internal static class AtomicFile
{
    private const int MaxReplaceAttempts = 4;

    /// <summary>
    /// Temporary files start with a dot so vault scans and the file watcher ignore them. The name
    /// is short and independent of the destination, so a note whose name is close to the limit of
    /// the file system can still be saved.
    /// </summary>
    public static string GetTemporaryPath(string destination)
    {
        var directory = Path.GetDirectoryName(destination) ?? throw new ArgumentException("Destination has no directory.", nameof(destination));
        return Path.Combine(directory, $".~{Guid.NewGuid():N}"[..12] + ".tmp");
    }

    /// <param name="destination">Full path of the file to create or replace.</param>
    /// <param name="content">Bytes to write.</param>
    /// <param name="overwrite">When false the operation fails with <see cref="IOException"/> if the destination exists.</param>
    /// <param name="beforeReplace">
    /// Runs when the new content is safely on disk, immediately before it takes the place of the
    /// destination. Throwing from it abandons the write and leaves the destination untouched.
    /// </param>
    /// <param name="cancellationToken">Cancels the write; the destination is left untouched.</param>
    public static async Task WriteAsync(
        string destination,
        ReadOnlyMemory<byte> content,
        bool overwrite,
        Action? beforeReplace,
        CancellationToken cancellationToken)
    {
        var temporary = GetTemporaryPath(destination);
        try
        {
            var stream = new FileStream(temporary, CreateOptions(destination));
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            PreserveUnixMode(destination, temporary);
            cancellationToken.ThrowIfCancellationRequested();
            beforeReplace?.Invoke();
            await ReplaceAsync(temporary, destination, overwrite, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public static Task WriteAsync(string destination, ReadOnlyMemory<byte> content, bool overwrite, CancellationToken cancellationToken) =>
        WriteAsync(destination, content, overwrite, beforeReplace: null, cancellationToken);

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover dot-prefixed temporary file is ignored by scans and harmless.
        }
    }

    private static FileStreamOptions CreateOptions(string destination)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 1, // Content is written in one call; no intermediate buffer needed.
            Options = FileOptions.Asynchronous,
        };

        if (!OperatingSystem.IsWindows() && File.Exists(destination))
        {
            // The file being replaced may be private (0600): its new content must not be readable by
            // others in the instant before the original permissions are copied onto the temporary file.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return options;
    }

    /// <summary>
    /// Renames the temporary file over the destination. On Windows an antivirus, an indexer or a sync
    /// client often holds the destination open for a few milliseconds, which fails the rename with a
    /// sharing violation; that is retried briefly instead of failing the save.
    /// </summary>
    private static async Task ReplaceAsync(string temporary, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, destination, overwrite);
                return;
            }
            catch (Exception exception) when (overwrite && attempt < MaxReplaceAttempts && IsTransient(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception exception) =>
        exception is UnauthorizedAccessException
        || (exception is IOException and not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException or DriveNotFoundException));

    private static void PreserveUnixMode(string destination, string temporary)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(destination))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(temporary, File.GetUnixFileMode(destination));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Permissions are a nicety; failing to copy them must not fail the save.
        }
    }
}
