using System.Globalization;
using CodeFlow.Ai;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Dbml;

/// <summary>
/// The three things the AI does to a schema document: rewrite it, judge it, explain it.
/// See <c>docs/business-rules/15-dbml.md</c>, <c>DBML-016</c>.
/// </summary>
/// <remarks>
/// <para>
/// One operation with three modes rather than three commands, because what differs between them is
/// the system prompt and nothing else: the same schema goes in, the same run plumbing carries it,
/// and only the shape of the answer changes. Splitting it would have meant three registrations and
/// three near-identical handlers to keep in step.
/// </para>
/// <para>
/// It lives beside the rest of the schema designer rather than in <c>Ai/AiOperations.cs</c>: the AI
/// plumbing is a dependency here, the way <c>Tickets/</c> depends on it, and a feature has to be
/// findable in one place. What it borrows is the seam — <see cref="AiRunner"/> — so the tests drive
/// it with a scripted engine and no subprocess.
/// </para>
/// </remarks>
internal static class DbmlAssistant
{
    /// <summary>The three modes, verbatim as the renderer sends them.</summary>
    /// <remarks>
    /// Matched by string rather than parsed into an enum at the edge so an unknown mode fails with
    /// its own name in the message, which is what a renderer/backend drift looks like from a log.
    /// </remarks>
    public const string EditMode = "edit";

    public const string ReviewMode = "review";

    public const string ExplainMode = "explain";

    /// <summary>
    /// Cap on the schema text handed to the model.
    /// </summary>
    /// <remarks>
    /// Above the 20 000 a commit diff gets and below the 40 000 per conflict side: a DBML document
    /// is dense — a hundred-table schema is well under this — and unlike a diff it cannot be
    /// usefully truncated, since the edit mode is asked to return the whole document. A schema that
    /// would be cut is refused instead, because silently dropping its tail would come back as a
    /// proposal that deletes every table past the cut.
    /// </remarks>
    private const int MaxSchemaChars = 60_000;

    /// <summary>Cap on the free-text instruction.</summary>
    private const int MaxInstructionChars = 4_000;

    /// <summary>What the schema designer's assistant may reach for: nothing.</summary>
    /// <remarks>
    /// An empty list is a decision here, not an oversight — it is handed the whole document on
    /// stdin and asked about that document alone, so a tool call could only re-read what it already
    /// has, or wander into a repository that may not even be one. A user who has set a toolset for
    /// the provider in Settings keeps it; <see cref="AiRouting.Bound"/> only fills a gap.
    /// </remarks>
    private static readonly string[] NoTools = [];

    /// <summary>Narrows a resolved config to this task's toolset.</summary>
    public static AiConfig Bound(SqliteConnection connection, AiConfig config) =>
        AiRouting.Bound(connection, config, NoTools);

    /// <summary>Runs one mode over one document.</summary>
    /// <returns>
    /// DBML for <see cref="EditMode"/> — the whole document, fence stripped, ready for the renderer
    /// to parse before it offers it. Spanish markdown for the other two, which are read, not applied.
    /// </returns>
    /// <exception cref="AiRunFailedException">
    /// The document is empty or too large, the mode is not one of the three, or the engine failed.
    /// </exception>
    public static async Task<string> AssistAsync(
        AiRunner runner,
        AiConfig config,
        string mode,
        string dbml,
        string instruction,
        AiRunContext? run,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dbml))
        {
            throw new AiRunFailedException("El documento está vacío");
        }

        if (dbml.Length > MaxSchemaChars)
        {
            throw new AiRunFailedException(string.Create(
                CultureInfo.InvariantCulture,
                $"El esquema es demasiado grande para la IA ({dbml.Length} caracteres, máximo {MaxSchemaChars})"));
        }

        var systemPrompt = mode switch
        {
            EditMode => Prompts.DbmlEditPrompt,
            ReviewMode => Prompts.DbmlReviewPrompt,
            ExplainMode => Prompts.DbmlExplainPrompt,
            _ => throw new AiRunFailedException($"unknown DBML assist mode '{mode}'"),
        };

        // The edit mode is the one that cannot run on its own: "apply the instruction" with no
        // instruction has no meaning, while a review and an explanation of the whole schema do.
        if (mode == EditMode && string.IsNullOrWhiteSpace(instruction))
        {
            throw new AiRunFailedException("Escribe qué quieres cambiar en el esquema");
        }

        var invocation = new AiInvocation(
            Prompt: Ask(mode, instruction),
            StdinContent: Payload(dbml, instruction),
            SystemPrompt: systemPrompt,
            Model: config.Model,
            AllowedTools: config.AllowedTools);

        var result = await runner(config, invocation, run, cancellationToken).ConfigureAwait(false);

        // Only the edit mode's answer is meant to be used verbatim. Stripping a fence off markdown
        // would eat a legitimate code block out of a review (AI-018).
        return mode == EditMode ? AiText.StripCodeFence(result.Text) : result.Text.Trim();
    }

    /// <summary>The ask, which rides on argv (<c>AI-002</c>).</summary>
    private static string Ask(string mode, string instruction) => mode switch
    {
        EditMode => "Aplica la instrucción al esquema DBML y devuelve el documento completo.",
        ReviewMode => string.IsNullOrWhiteSpace(instruction)
            ? "Revisa el diseño de este esquema DBML."
            : "Revisa el diseño de este esquema DBML atendiendo a lo que pide el autor.",
        _ => string.IsNullOrWhiteSpace(instruction)
            ? "Explica qué modela este esquema DBML."
            : "Explica este esquema DBML respondiendo a lo que pregunta quien lo lee.",
    };

    /// <summary>The data, which rides on stdin: the schema, and the instruction when there is one.</summary>
    /// <remarks>
    /// The instruction goes after the schema so the last thing the model reads is what it was asked
    /// for, and it is left out entirely when blank rather than sent as an empty heading — a section
    /// with nothing under it reads as a question the user declined to answer.
    /// </remarks>
    private static string Payload(string dbml, string instruction)
    {
        var schema = $"""
                      === ESQUEMA DBML ===
                      {dbml}
                      """;

        return string.IsNullOrWhiteSpace(instruction)
            ? schema
            : $"""
               {schema}

               === INSTRUCCIÓN ===
               {AiOperations.Cap(instruction, MaxInstructionChars)}
               """;
    }
}
