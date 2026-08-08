using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Activity;

/// <summary>
/// The chat transcript and the conversation list.
/// </summary>
/// <remarks>
/// Append-only: a turn is written once and never edited. The only mutations are deleting a whole
/// conversation and writing a title into the separate <c>conversation_titles</c> table — the
/// transcript itself is never touched. See <c>docs/business-rules/03-storage.md</c>.
/// </remarks>
internal static class ActivityLogStore
{
    /// <summary>
    /// The column list every read shares, so the ordinals below cannot drift apart.
    /// </summary>
    private const string Columns =
        "id, project_id, session_id, engine_session_id, question, answer, trace, created_at, " +
        "response_time_ms, is_error, provider, model, engine_version";

    /// <summary>Records one turn and returns it, stamped with the instant it was filed.</summary>
    /// <remarks>
    /// <paramref name="conversationId"/> lands in <c>session_id</c> and
    /// <paramref name="engineSessionId"/> in <c>engine_session_id</c> — the column names are the
    /// reference's and predate the two ids being separated, which is why the app-level id sits in the
    /// one that sounds like the engine's.
    /// </remarks>
    public static ActivityLogEntry Add(
        SqliteConnection connection,
        string projectId,
        string conversationId,
        string? engineSessionId,
        string question,
        string answer,
        string? trace,
        TurnMeta meta,
        bool isError)
    {
        var entry = new ActivityLogEntry(
            Guid.NewGuid().ToString(),
            projectId,
            conversationId,
            engineSessionId,
            question,
            answer,
            trace,
            Clock.Now(),
            meta.ResponseTimeMs,
            isError,
            meta.Provider,
            meta.Model,
            meta.EngineVersion);

        Sql.Execute(connection,
            $"INSERT INTO activity_log ({Columns}) VALUES ($id, $projectId, $sessionId, $engineSessionId, " +
            "$question, $answer, $trace, $createdAt, $responseTimeMs, $isError, $provider, $model, $engineVersion)",
            ("$id", entry.Id),
            ("$projectId", entry.ProjectId),
            ("$sessionId", entry.SessionId),
            ("$engineSessionId", entry.EngineSessionId),
            ("$question", entry.Question),
            ("$answer", entry.Answer),
            ("$trace", entry.Trace),
            ("$createdAt", entry.CreatedAt),
            ("$responseTimeMs", entry.ResponseTimeMs),
            ("$isError", entry.IsError),
            ("$provider", entry.Provider),
            ("$model", entry.Model),
            ("$engineVersion", entry.EngineVersion));

        return entry;
    }

    /// <summary>Every turn of one conversation, oldest first.</summary>
    /// <remarks>
    /// The frontend flattens these into <c>[user, assistant, user, assistant, …]</c> to redisplay a
    /// past conversation exactly like a live one.
    /// </remarks>
    public static List<ActivityLogEntry> Messages(SqliteConnection connection, string projectId, string sessionId) =>
        Sql.Query(connection,
            $"SELECT {Columns} FROM activity_log WHERE project_id = $projectId AND session_id = $sessionId " +
            "ORDER BY created_at ASC",
            Read,
            ("$projectId", projectId),
            ("$sessionId", sessionId));

    /// <summary>
    /// One summary per conversation, most recently updated first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Grouped in memory rather than in SQL because the title is "the first turn's question" in
    /// insertion order and the search matches against <em>any</em> turn's question or answer — both
    /// awkward in one statement, and the row count per project is small.
    /// </para>
    /// <para>
    /// <b>Rows with a null <c>session_id</c> are excluded, permanently.</b> They were written before
    /// session tracking existed and no migration backfills a synthetic id for them, so they are
    /// invisible to every chat-history feature. That is 1.7.2's behaviour (<c>STORE-014</c>),
    /// reproduced rather than corrected: inventing ids here would surface conversations 1.7.2
    /// never showed.
    /// </para>
    /// </remarks>
    public static List<ChatConversationSummary> Conversations(
        SqliteConnection connection, string projectId, string? search)
    {
        var turns = Sql.Query(connection,
            $"SELECT {Columns} FROM activity_log WHERE project_id = $projectId AND session_id IS NOT NULL " +
            "ORDER BY created_at ASC",
            Read,
            ("$projectId", projectId));

        var needle = string.IsNullOrEmpty(search) ? null : search.ToLowerInvariant();

        // Insertion-ordered, so the first turn of each conversation names it.
        var order = new List<string>();
        var summaries = new Dictionary<string, ChatConversationSummary>(StringComparer.Ordinal);
        var matched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var turn in turns)
        {
            if (turn.SessionId is not { } sessionId)
            {
                continue;
            }

            if (needle is not null && Contains(turn, needle))
            {
                matched.Add(sessionId);
            }

            if (summaries.TryGetValue(sessionId, out var summary))
            {
                summaries[sessionId] = summary with
                {
                    UpdatedAt = turn.CreatedAt,
                    TurnCount = summary.TurnCount + 1,
                };
                continue;
            }

            order.Add(sessionId);
            summaries[sessionId] = new ChatConversationSummary(
                sessionId, projectId, turn.Question, turn.CreatedAt, turn.CreatedAt, 1);
        }

        var titles = Titles(connection, projectId);

        return [.. order
            .Where(sessionId => needle is null || matched.Contains(sessionId))
            .Select(sessionId => summaries[sessionId])
            // Applied after grouping: a rename overrides the derived title, it does not become a turn.
            .Select(summary => titles.TryGetValue(summary.SessionId, out var title)
                ? summary with { Title = title }
                : summary)
            .OrderByDescending(summary => summary.UpdatedAt, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The provider that answered a conversation's most recent turn, when one was recorded.
    /// </summary>
    /// <remarks>
    /// How the chat command tells whether a stored resume token still belongs to the engine about to
    /// run. <see langword="null"/> for a conversation with no turns yet, or whose turns predate
    /// provider tracking.
    /// </remarks>
    public static string? LastTurnProvider(SqliteConnection connection, string projectId, string conversationId) =>
        Sql.QueryText(connection,
            "SELECT provider FROM activity_log WHERE project_id = $projectId AND session_id = $conversationId " +
            "AND provider IS NOT NULL ORDER BY created_at DESC LIMIT 1",
            ("$projectId", projectId),
            ("$conversationId", conversationId));

    /// <summary>Gives a conversation a user-chosen title, replacing any earlier one.</summary>
    /// <remarks>
    /// A conversation has no row of its own to hold a title, since it is a group over individual
    /// turns — hence a table keyed by the conversation id alone.
    /// </remarks>
    public static void RenameConversation(
        SqliteConnection connection, string projectId, string sessionId, string title) =>
        Sql.Execute(connection,
            "INSERT INTO conversation_titles (session_id, project_id, title, updated_at) " +
            "VALUES ($sessionId, $projectId, $title, $updatedAt) " +
            "ON CONFLICT(session_id) DO UPDATE SET title = excluded.title, updated_at = excluded.updated_at",
            ("$sessionId", sessionId),
            ("$projectId", projectId),
            ("$title", title),
            ("$updatedAt", Clock.Now()));

    /// <summary>Deletes a conversation's turns and its title.</summary>
    /// <remarks>A hard delete, like every delete in this schema — there is no soft-delete column anywhere.</remarks>
    public static void DeleteConversation(SqliteConnection connection, string projectId, string sessionId)
    {
        Sql.Execute(connection,
            "DELETE FROM activity_log WHERE project_id = $projectId AND session_id = $sessionId",
            ("$projectId", projectId),
            ("$sessionId", sessionId));

        Sql.Execute(connection,
            "DELETE FROM conversation_titles WHERE project_id = $projectId AND session_id = $sessionId",
            ("$projectId", projectId),
            ("$sessionId", sessionId));
    }

    /// <summary>Whether a turn matches a case-folded search needle, in either half of the exchange.</summary>
    /// <remarks>Search covers full past exchanges, not just the title the list shows.</remarks>
    private static bool Contains(ActivityLogEntry turn, string needle) =>
        turn.Question.ToLowerInvariant().Contains(needle, StringComparison.Ordinal)
        || turn.Answer.ToLowerInvariant().Contains(needle, StringComparison.Ordinal);

    private static Dictionary<string, string> Titles(SqliteConnection connection, string projectId) =>
        Sql.Query(connection,
            "SELECT session_id, title FROM conversation_titles WHERE project_id = $projectId",
            reader => (SessionId: reader.GetString(0), Title: reader.GetString(1)),
            ("$projectId", projectId))
            .ToDictionary(row => row.SessionId, row => row.Title, StringComparer.Ordinal);

    private static ActivityLogEntry Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.TextOrNull(2),
        reader.TextOrNull(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.TextOrNull(6),
        reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetInt64(8),
        !reader.IsDBNull(9) && reader.GetBoolean(9),
        reader.TextOrNull(10),
        reader.TextOrNull(11),
        reader.TextOrNull(12));
}
