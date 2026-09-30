namespace DevNotes.Infrastructure.Storage;

/// <summary>
/// Crash-safe file replacement: the content is written to a temporary file in the same folder,
/// flushed to the device and then renamed over the destination. A reader (or a crash) sees either
/// the old file or the new one, never a partial write.
/// </summary>
internal static class AtomicFile
{
    /// <summary>Temporary files start with a dot so vault scans and the file watcher ignore them.</summary>
    public static string GetTemporaryPath(string destination)
    {
        var directory = Path.GetDirectoryName(destination) ?? throw new ArgumentException("Destination has no directory.", nameof(destination));
        return Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
    }

    /// <param name="destination">Full path of the file to create or replace.</param>
    /// <param name="content">Bytes to write.</param>
    /// <param name="overwrite">When false the operation fails with <see cref="IOException"/> if the destination exists.</param>
    /// <param name="cancellationToken">Cancels the write; the destination is left untouched.</param>
    public static async Task WriteAsync(string destination, ReadOnlyMemory<byte> content, bool overwrite, CancellationToken cancellationToken)
    {
        var temporary = GetTemporaryPath(destination);
        try
        {
            var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1, // Content is written in one call; no intermediate buffer needed.
                FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            PreserveUnixMode(destination, temporary);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

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
