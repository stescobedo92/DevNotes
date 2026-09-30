using System.Diagnostics;
using System.Globalization;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace DevNotes.Desktop.Services;

/// <param name="Text">Piece of source text.</param>
/// <param name="Foreground">Colour as 0xAARRGGBB, or null for the default text colour.</param>
/// <param name="IsBold">Whether the theme renders the token in bold.</param>
/// <param name="IsItalic">Whether the theme renders the token in italics.</param>
public readonly record struct CodeToken(string Text, uint? Foreground, bool IsBold, bool IsItalic);

/// <summary>Syntax highlighting for code blocks of the Markdown preview.</summary>
public interface ICodeHighlighter
{
    /// <summary>
    /// Splits <paramref name="code"/> into coloured tokens, one list per line. Unknown languages
    /// and oversized blocks come back as plain text (a single token per line).
    /// </summary>
    IReadOnlyList<IReadOnlyList<CodeToken>> Highlight(string code, string? language, bool dark);

    /// <summary>TextMate scope for a language name or alias ("cs", "c++", "ps1"…), or null when unknown.</summary>
    string? ResolveScope(string? language);
}

/// <summary>
/// TextMate-based highlighter (same grammars and themes as the editor). Not thread-safe: it is
/// used from the UI thread while rendering the preview.
/// </summary>
public sealed class TextMateCodeHighlighter : ICodeHighlighter
{
    public const int MaxHighlightedLines = 2_000;
    public const int MaxHighlightedCharacters = 200_000;

    private static readonly TimeSpan _defaultLineBudget = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan _defaultBlockBudget = TimeSpan.FromMilliseconds(750);

    // Names developers actually type after ``` mapped to TextMate language ids.
    private static readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = "csharp",
        ["c#"] = "csharp",
        ["csharp"] = "csharp",
        ["c++"] = "cpp",
        ["cpp"] = "cpp",
        ["cxx"] = "cpp",
        ["h"] = "cpp",
        ["hpp"] = "cpp",
        ["c"] = "c",
        ["sql"] = "sql",
        ["tsql"] = "sql",
        ["ps"] = "powershell",
        ["ps1"] = "powershell",
        ["pwsh"] = "powershell",
        ["powershell"] = "powershell",
        ["json"] = "json",
        ["jsonc"] = "jsonc",
        ["yaml"] = "yaml",
        ["yml"] = "yaml",
        ["sh"] = "shellscript",
        ["bash"] = "shellscript",
        ["zsh"] = "shellscript",
        ["shell"] = "shellscript",
        ["xml"] = "xml",
        ["html"] = "html",
        ["js"] = "javascript",
        ["ts"] = "typescript",
        ["py"] = "python",
        ["md"] = "markdown",
        ["bat"] = "bat",
        ["cmd"] = "bat",
        ["diff"] = "diff",
        ["dockerfile"] = "dockerfile",
        ["ini"] = "ini",
        ["toml"] = "ini",
    };

    private readonly ThemedRegistry _dark = new(ThemeName.DarkPlus);
    private readonly ThemedRegistry _light = new(ThemeName.LightPlus);
    private readonly TimeSpan _lineBudget;
    private readonly TimeSpan _blockBudget;

    public TextMateCodeHighlighter()
        : this(_defaultLineBudget, _defaultBlockBudget)
    {
    }

    /// <param name="lineBudget">Longest time spent tokenizing one line.</param>
    /// <param name="blockBudget">Longest time spent on one code block; the remaining lines stay plain.</param>
    internal TextMateCodeHighlighter(TimeSpan lineBudget, TimeSpan blockBudget)
    {
        _lineBudget = lineBudget;
        _blockBudget = blockBudget;
    }

    /// <summary>A highlighter that never gives up on time, for tests that assert colours on loaded machines.</summary>
    internal static TextMateCodeHighlighter WithoutTimeLimits() => new(TimeSpan.MaxValue, TimeSpan.MaxValue);

    public string? ResolveScope(string? language)
    {
        var name = NormalizeLanguage(language);
        if (name is null)
        {
            return null;
        }

        var options = _dark.Options;
        var id = _aliases.GetValueOrDefault(name, name);
        return TryGetScope(() => options.GetScopeByLanguageId(id))
            ?? TryGetScope(() => options.GetScopeByExtension("." + name));
    }

    public IReadOnlyList<IReadOnlyList<CodeToken>> Highlight(string code, string? language, bool dark)
    {
        ArgumentNullException.ThrowIfNull(code);

        var lines = code.ReplaceLineEndings("\n").Split('\n');
        var registry = dark ? _dark : _light;
        var grammar = code.Length <= MaxHighlightedCharacters && lines.Length <= MaxHighlightedLines
            ? registry.GetGrammar(ResolveScope(language))
            : null;

        var result = new List<IReadOnlyList<CodeToken>>(lines.Length);
        if (grammar is null)
        {
            foreach (var line in lines)
            {
                result.Add([new CodeToken(line, null, false, false)]);
            }

            return result;
        }

        // Highlighting runs on the UI thread, so it is bounded twice: per line (pathological regular
        // expressions) and per block. Whatever is not tokenized in time is shown as plain text.
        var stopwatch = Stopwatch.StartNew();
        IStateStack? state = null;
        foreach (var line in lines)
        {
            if (stopwatch.Elapsed > _blockBudget)
            {
                result.Add([new CodeToken(line, null, false, false)]);
                continue;
            }

            var tokenized = grammar.TokenizeLine(line, state, _lineBudget);
            state = tokenized.RuleStack;
            result.Add(ToTokens(line, tokenized.Tokens, registry.Theme));
        }

        return result;
    }

    /// <summary>First word of a fence info string, lower-cased: "C# title=x" → "c#".</summary>
    internal static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var span = language.AsSpan().Trim();
        var end = span.IndexOfAny(' ', '\t', '{');
        if (end == 0)
        {
            return null;
        }

        var name = (end > 0 ? span[..end] : span).TrimStart('.').ToString().ToLowerInvariant();
        return name.Length is > 0 and <= 32 ? name : null;
    }

    private static List<CodeToken> ToTokens(string line, IToken[] tokens, Theme theme)
    {
        var result = new List<CodeToken>(tokens.Length + 1);
        var covered = 0;
        foreach (var token in tokens)
        {
            // Tokens arrive in order; clamp them so the pieces always add up to exactly the line.
            var start = Math.Clamp(token.StartIndex, covered, line.Length);
            var end = Math.Clamp(token.EndIndex, start, line.Length);
            if (start > covered)
            {
                result.Add(new CodeToken(line[covered..start], null, false, false));
            }

            covered = end;
            if (end <= start)
            {
                continue;
            }

            var foreground = 0;
            var style = FontStyle.NotSet;
            foreach (var rule in theme.Match(token.Scopes))
            {
                if (foreground == 0 && rule.foreground > 0)
                {
                    foreground = rule.foreground;
                }

                if (style == FontStyle.NotSet && rule.fontStyle != FontStyle.NotSet)
                {
                    style = rule.fontStyle;
                }
            }

            result.Add(new CodeToken(
                line[start..end],
                foreground > 0 ? ParseColor(theme.GetColor(foreground)) : null,
                style != FontStyle.NotSet && style.HasFlag(FontStyle.Bold),
                style != FontStyle.NotSet && style.HasFlag(FontStyle.Italic)));
        }

        // The tokenizer stops early when it runs out of time: the rest of the line must not disappear.
        if (covered < line.Length || result.Count == 0)
        {
            result.Add(new CodeToken(line[covered..], null, false, false));
        }

        return result;
    }

    /// <summary>Parses "#RRGGBB" or "#RRGGBBAA" (TextMate theme notation) into 0xAARRGGBB.</summary>
    internal static uint? ParseColor(string? hex)
    {
        if (hex is null || hex.Length is not (7 or 9) || hex[0] != '#'
            || !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return hex.Length == 7
            ? 0xFF000000 | value
            : ((value & 0xFF) << 24) | (value >> 8);
    }

    private static string? TryGetScope(Func<string?> lookup)
    {
        try
        {
            return lookup() is { Length: > 0 } scope ? scope : null;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException or InvalidOperationException or NullReferenceException)
        {
            // The registry throws for languages it does not ship; unknown simply means "no highlighting".
            return null;
        }
    }

    private sealed class ThemedRegistry(ThemeName themeName)
    {
        private readonly Dictionary<string, IGrammar?> _grammars = new(StringComparer.Ordinal);
        private Registry? _registry;

        public RegistryOptions Options { get; } = new(themeName);

        public Theme Theme => (_registry ??= new Registry(Options)).GetTheme();

        public IGrammar? GetGrammar(string? scope)
        {
            if (scope is null)
            {
                return null;
            }

            if (!_grammars.TryGetValue(scope, out var grammar))
            {
                // Loading a grammar parses its JSON definition: do it once per language and theme.
                grammar = (_registry ??= new Registry(Options)).LoadGrammar(scope);
                _grammars[scope] = grammar;
            }

            return grammar;
        }
    }
}
