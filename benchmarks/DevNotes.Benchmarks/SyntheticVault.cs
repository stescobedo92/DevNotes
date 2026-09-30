using System.Globalization;
using System.Text;

namespace DevNotes.Benchmarks;

/// <summary>
/// Deterministic vault of realistic technical notes (frontmatter, prose, a code block) so every
/// run of a benchmark measures exactly the same input.
/// </summary>
public sealed class SyntheticVault : IDisposable
{
    private static readonly string[] _projects =
        ["azure-microservices", "cslinq", "oatpp-api", "billing", "inventory", "auth-gateway", "reporting", "tooling"];

    private static readonly string[] _tags =
    [
        "sql-server", "deadlock", "performance", "azure", "deploy", "design-decision", "csharp", "cpp", "linq", "indexing",
        "caching", "retry", "timeout", "migration", "security", "logging", "kubernetes", "queue", "memory", "threading",
    ];

    private static readonly string[] _types = ["bug", "adr", "runbook", "learning", "snippet", "note"];

    private static readonly string[] _words =
    [
        "deadlock", "inventory", "transaction", "index", "query", "latency", "timeout", "retry", "cache", "queue",
        "deployment", "rollback", "migration", "schema", "partition", "replica", "throughput", "allocation", "thread", "lock",
        "connection", "pool", "request", "response", "payload", "serializer", "handler", "pipeline", "benchmark", "profile",
        "regression", "release", "config", "secret", "token", "endpoint", "gateway", "service", "worker", "scheduler",
        "the", "a", "of", "and", "when", "after", "before", "with", "without", "because", "during", "under", "every", "only",
    ];

    private static readonly string[] _identifiers =
    [
        "UpdateInventoryAsync", "GetUserById", "ProcessOrderBatch", "RebuildSearchIndex", "AcquireDistributedLock",
        "FlushPendingWrites", "ResolveConnectionString", "ValidateAccessToken", "ScheduleRetryPolicy", "MapOrderToDto",
    ];

    private SyntheticVault(string root, int noteCount)
    {
        Root = root;
        NoteCount = noteCount;
    }

    public string Root { get; }

    public int NoteCount { get; }

    public static SyntheticVault Create(int noteCount)
    {
        var root = Directory.CreateTempSubdirectory("devnotes-bench-vault-").FullName;
        var random = new Random(20260930);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        for (var i = 0; i < noteCount; i++)
        {
            var project = _projects[i % _projects.Length];
            var folder = Path.Combine(root, project);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"note-{i:D5}.md"), BuildNote(i, project, random), utf8);
        }

        return new SyntheticVault(root, noteCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private static string BuildNote(int number, string project, Random random)
    {
        var created = new DateOnly(2024, 1, 1).AddDays(number % 900);
        var text = new StringBuilder(2_048);
        text.Append("---\n");
        text.Append(CultureInfo.InvariantCulture, $"id: 01BENCH{number:D19}\n");
        text.Append(CultureInfo.InvariantCulture, $"title: {Title(number, random)}\n");
        text.Append(CultureInfo.InvariantCulture, $"project: {project}\n");
        text.Append(CultureInfo.InvariantCulture, $"tags: [{_tags[number % _tags.Length]}, {_tags[(number * 7 + 3) % _tags.Length]}]\n");
        text.Append(CultureInfo.InvariantCulture, $"type: {_types[number % _types.Length]}\n");
        text.Append(CultureInfo.InvariantCulture, $"created: {created:yyyy-MM-dd}\n");
        text.Append(CultureInfo.InvariantCulture, $"updated: {created.AddDays(number % 30):yyyy-MM-dd}\n");
        text.Append("---\n\n# Context\n\n");

        for (var paragraph = 0; paragraph < 5; paragraph++)
        {
            AppendSentence(text, random, words: 40);
            text.Append("\n\n");
        }

        var identifier = _identifiers[number % _identifiers.Length];
        text.Append("## Fix\n\n```csharp\n");
        text.Append(CultureInfo.InvariantCulture, $"public async Task {identifier}(CancellationToken cancellationToken)\n{{\n");
        text.Append(CultureInfo.InvariantCulture, $"    await using var scope = await _locks.AcquireAsync(\"{project}-{number}\", cancellationToken);\n");
        text.Append("    // ...\n}\n```\n\n");
        AppendSentence(text, random, words: 25);
        text.Append('\n');
        return text.ToString();
    }

    private static string Title(int number, Random random)
    {
        var title = new StringBuilder();
        AppendSentence(title, random, words: 4);
        return string.Create(CultureInfo.InvariantCulture, $"{title.ToString().TrimEnd('.')} {number}");
    }

    private static void AppendSentence(StringBuilder text, Random random, int words)
    {
        for (var i = 0; i < words; i++)
        {
            var word = _words[random.Next(_words.Length)];
            if (i == 0)
            {
                text.Append(char.ToUpperInvariant(word[0])).Append(word, 1, word.Length - 1);
            }
            else
            {
                text.Append(' ').Append(word);
            }
        }

        text.Append('.');
    }
}
