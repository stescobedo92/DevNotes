using DevNotes.Application.Abstractions;

namespace DevNotes.Application.Tests.Fakes;

public sealed class InMemoryTemplateStore : ITemplateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _templates = new(StringComparer.Ordinal);

    public int Saves { get; private set; }

    /// <summary>When set, the next write throws (to test failure handling).</summary>
    public Exception? FailNextWrite { get; set; }

    public IReadOnlyDictionary<string, string> Templates
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_templates, StringComparer.Ordinal);
            }
        }
    }

    public Task<IReadOnlyDictionary<string, string>> LoadAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Templates);

    public Task SaveAsync(string key, string text, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfFailing();
            Saves++;
            _templates[key] = text;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfFailing();
            _templates.Remove(key);
        }

        return Task.CompletedTask;
    }

    private void ThrowIfFailing()
    {
        if (FailNextWrite is { } failure)
        {
            FailNextWrite = null;
            throw failure;
        }
    }
}
