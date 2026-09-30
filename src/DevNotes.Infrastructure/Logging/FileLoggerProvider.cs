using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace DevNotes.Infrastructure.Logging;

/// <summary>
/// Minimal structured file logger: one UTF-8 file per day under the app log folder, written by a
/// single background task so logging never blocks the caller. Old files are pruned on start.
/// Messages are produced by <c>LoggerMessage</c> templates that carry paths and counters only;
/// note content is never logged.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    private const int QueueCapacity = 2_048;
    private const int RetainedFiles = 7;

    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest, // Losing old log lines beats blocking or growing without bound.
        });

    private readonly string _directory;
    private readonly TimeProvider _timeProvider;
    private readonly Task _writer;
    private int _disposed;

    public FileLoggerProvider(IAppPaths paths, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _directory = paths.LogDirectory;
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, static (name, provider) => new FileLogger(name, provider), this);

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    private void Enqueue(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        var builder = new StringBuilder(160)
            .Append(_timeProvider.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(ToLabel(level))
            .Append(' ')
            .Append(category)
            .Append('[')
            .Append(eventId.Id.ToString(CultureInfo.InvariantCulture))
            .Append("] ")
            .Append(message.ReplaceLineEndings(" "));

        if (exception is not null)
        {
            builder.AppendLine().Append("    ").Append(exception.ToString().ReplaceLineEndings(Environment.NewLine + "    "));
        }

        _queue.Writer.TryWrite(builder.ToString());
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            PruneOldFiles();

            await foreach (var first in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var day = _timeProvider.GetUtcNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                var path = Path.Combine(_directory, $"devnotes-{day}.log");
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous);
                await using (stream.ConfigureAwait(false))
                {
                    var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    await using (writer.ConfigureAwait(false))
                    {
                        // Drain everything that is already queued with a single open/flush.
                        await writer.WriteLineAsync(first).ConfigureAwait(false);
                        while (_queue.Reader.TryRead(out var next))
                        {
                            await writer.WriteLineAsync(next).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The log folder is not writable. There is nowhere left to report it: stop logging to
            // file and let the queue drop lines (DropOldest) rather than take the application down.
        }
    }

    private void PruneOldFiles()
    {
        var files = Directory.GetFiles(_directory, "devnotes-*.log");
        Array.Sort(files, StringComparer.Ordinal);
        for (var i = 0; i < files.Length - RetainedFiles; i++)
        {
            File.Delete(files[i]);
        }
    }

    private static string ToLabel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && provider._disposed == 0;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                provider.Enqueue(logLevel, category, eventId, formatter(state, exception), exception);
            }
        }
    }
}
