using System.Globalization;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Notes;

/// <summary>
/// The templates the app ships, in the language of the UI. They are the starting point of every
/// vault; a copy saved under <c>.devnotes/templates</c> takes over and can be edited freely.
/// </summary>
public static class BuiltInTemplates
{
    /// <summary>Keys in the order they are offered; the first one is the plain note.</summary>
    public static IReadOnlyList<string> Keys { get; } = ["note", "bug", "adr", "runbook", "learning", "snippet"];

    public static NoteType TypeOf(string key) => key switch
    {
        "bug" => NoteType.Bug,
        "adr" => NoteType.Adr,
        "runbook" => NoteType.Runbook,
        "learning" => NoteType.Learning,
        "snippet" => NoteType.Snippet,
        _ => NoteType.Note,
    };

    public static bool Contains(string key) => Keys.Contains(key, StringComparer.Ordinal);

    /// <summary>Template text for a built-in key in the given culture (Spanish or, for anything else, English).</summary>
    public static string Get(string key, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var spanish = culture.TwoLetterISOLanguageName.Equals("es", StringComparison.OrdinalIgnoreCase);
        return Frontmatter(key) + (spanish ? SpanishBody(key) : EnglishBody(key));
    }

    private static string Frontmatter(string key)
    {
        var type = TypeOf(key).ToKey();
        var tags = key switch
        {
            "bug" => "[bug]",
            "adr" => "[design-decision]",
            "runbook" => "[runbook]",
            "learning" => "[learning]",
            "snippet" => "[snippet]",
            _ => "[]",
        };
        // Three dollars: "{{placeholder}}" is content, "{{{value}}}" is interpolated.
        return $$$"""
            ---
            id: {{id}}
            title: {{title}}
            project: {{project}}
            tags: {{{tags}}}
            type: {{{type}}}
            created: {{date}}
            updated: {{date}}
            ---

            # {{title}}


            """;
    }

    private static string SpanishBody(string key) => key switch
    {
        "bug" => """
            ## Síntoma

            Qué se observó, dónde y desde cuándo.

            ## Causa raíz

            Por qué ocurría de verdad (no el primer síntoma).

            ## Solución

            Qué se cambió y cómo se verificó.

            ## Cómo evitarlo

            Test, alerta o revisión que lo detecta la próxima vez.

            {{body}}
            """,
        "adr" => """
            ## Contexto

            Qué problema o fuerza obliga a decidir.

            ## Decisión

            Qué se ha decidido y por qué.

            ## Alternativas consideradas

            - Alternativa y motivo del descarte.

            ## Consecuencias

            Qué mejora, qué empeora y qué habrá que vigilar.

            {{body}}
            """,
        "runbook" => """
            ## Cuándo usarlo

            Síntoma o alerta que dispara este procedimiento.

            ## Requisitos

            Accesos, herramientas y comprobaciones previas.

            ## Pasos

            1. Primer paso, con el comando exacto.
            2. Cómo verificar que ha funcionado.

            ## Vuelta atrás

            Cómo deshacer cada paso si algo falla.

            {{body}}
            """,
        "learning" => """
            ## Qué creía

            ## Qué aprendí

            ## Fuente

            Enlace, libro, conversación o experimento.

            {{body}}
            """,
        "snippet" => """
            ## Uso

            Cuándo sirve y qué hay que adaptar.

            ```csharp
            // Código
            ```

            {{body}}
            """,
        _ => """
            {{body}}
            """,
    };

    private static string EnglishBody(string key) => key switch
    {
        "bug" => """
            ## Symptom

            What was observed, where and since when.

            ## Root cause

            Why it really happened (not the first symptom).

            ## Fix

            What changed and how it was verified.

            ## Prevention

            The test, alert or review that catches it next time.

            {{body}}
            """,
        "adr" => """
            ## Context

            The problem or force that requires a decision.

            ## Decision

            What was decided and why.

            ## Alternatives considered

            - Alternative and why it was rejected.

            ## Consequences

            What gets better, what gets worse and what to watch.

            {{body}}
            """,
        "runbook" => """
            ## When to use it

            The symptom or alert that triggers this procedure.

            ## Prerequisites

            Access, tools and checks to do first.

            ## Steps

            1. First step, with the exact command.
            2. How to verify it worked.

            ## Rollback

            How to undo each step if something fails.

            {{body}}
            """,
        "learning" => """
            ## What I believed

            ## What I learned

            ## Source

            Link, book, conversation or experiment.

            {{body}}
            """,
        "snippet" => """
            ## Usage

            When it helps and what to adapt.

            ```csharp
            // Code
            ```

            {{body}}
            """,
        _ => """
            {{body}}
            """,
    };
}
