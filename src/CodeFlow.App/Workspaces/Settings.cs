using CodeFlow.Ai;
using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Workspaces;

/// <summary>
/// The generic settings store and the per-workspace prompt overrides.
/// </summary>
/// <remarks>
/// See <c>docs/business-rules/09-workspace-scoped.md</c> §Settings and §"Prompt templates", and
/// <c>03-storage.md</c> <c>STORE-012</c>.
/// </remarks>
internal static class Settings
{
    /// <summary>The prompt kinds every new workspace starts with, and the text they start at.</summary>
    /// <remarks>
    /// <c>sdd_stages</c> is deliberately absent: it has no built-in text, and it only ever gets a
    /// row when the user saves one.
    /// </remarks>
    public static IEnumerable<(string Kind, string Content)> SeededPrompts =>
    [
        ("review_standard", Prompts.DefaultPrReviewStandard),
        ("ticket_review_standard", Prompts.DefaultTicketReviewStandard),
        ("pr_description", Prompts.DefaultPrDescriptionTemplate),
    ];

    /// <summary>Reads one <c>app_settings</c> value, or <see langword="null"/> if the key has no row.</summary>
    /// <remarks>
    /// A stored empty string is a real row and comes back as <c>""</c>, not as "unset"
    /// (<c>WS-004</c>). Several readers treat blank as unset, but each does so at its own call
    /// site — this one does not, and callers that conflate the two would change behaviour.
    /// </remarks>
    public static string? GetSetting(SqliteConnection connection, string key) =>
        Sql.QueryText(connection, "SELECT value FROM app_settings WHERE key = $key", ("$key", key));

    /// <summary>Writes one <c>app_settings</c> value.</summary>
    /// <remarks>
    /// No allow-list, no enum, no type coercion — any key, any value, stored and returned as text.
    /// That is 1.7.2's contract and the frontend relies on it: several keys, notably
    /// <c>github_connections</c>, are written only from the renderer and have no backend writer at
    /// all (<c>WS-004</c>).
    /// </remarks>
    public static void SetSetting(SqliteConnection connection, string key, string value) =>
        Sql.Execute(connection,
            """
            INSERT INTO app_settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """,
            ("$key", key), ("$value", value));

    /// <summary>
    /// The built-in text for a prompt kind.
    /// </summary>
    /// <remarks>
    /// Two levels, not the three the brief describes: there is no global layer, no version and no
    /// upgrade diff anywhere in 1.7.2. <c>sdd_stages</c> legitimately resolves to the empty
    /// string — the SDD stages start blank because the user defines them — so this is the one kind
    /// for which "always non-empty" does not hold.
    /// </remarks>
    public static string DefaultWorkspacePrompt(string kind) => kind switch
    {
        "pr_description" => Prompts.DefaultPrDescriptionTemplate,
        // Explicit, and it has to be: the catch-all below returns the PR methodology, so without
        // this arm "restore default" on the ticket standard would hand back a prompt that never
        // mentions a work item and never emits the two verdict sections. Nothing would fail — the
        // review would simply stop reporting criteria, which reads as the model refusing to.
        "ticket_review_standard" => Prompts.DefaultTicketReviewStandard,
        "sdd_stages" => string.Empty,
        // Every other kind, including "review_standard", lands here. The catch-all is the
        // reference's own shape, so an unrecognised kind returns the review methodology rather
        // than failing.
        _ => Prompts.DefaultPrReviewStandard,
    };

    /// <summary>Resolves a workspace's prompt for a kind, falling back to the built-in.</summary>
    /// <remarks>
    /// The stored row wins only if it is non-blank after trimming. A blank row is indistinguishable
    /// from an absent one, which is what makes <see cref="SetWorkspacePrompt"/> with an empty string
    /// the "restore default" action (<c>STORE-012</c>).
    /// </remarks>
    public static string GetWorkspacePrompt(SqliteConnection connection, string workspaceId, string kind)
    {
        var stored = Sql.QueryText(connection,
            "SELECT content FROM workspace_prompts WHERE workspace_id = $workspaceId AND kind = $kind",
            ("$workspaceId", workspaceId), ("$kind", kind));

        return string.IsNullOrWhiteSpace(stored) ? DefaultWorkspacePrompt(kind) : stored;
    }

    /// <summary>Saves an override; an empty string resets the kind to its built-in.</summary>
    /// <remarks>
    /// Always an upsert, never a delete — the row survives a reset with blank content, and
    /// <see cref="GetWorkspacePrompt"/> falls through on the next read.
    /// </remarks>
    public static void SetWorkspacePrompt(SqliteConnection connection, string workspaceId, string kind, string content) =>
        Sql.Execute(connection,
            """
            INSERT INTO workspace_prompts (workspace_id, kind, content, updated_at)
            VALUES ($workspaceId, $kind, $content, $updatedAt)
            ON CONFLICT(workspace_id, kind) DO UPDATE SET content = excluded.content, updated_at = excluded.updated_at
            """,
            ("$workspaceId", workspaceId), ("$kind", kind), ("$content", content), ("$updatedAt", Clock.Now()));
}
