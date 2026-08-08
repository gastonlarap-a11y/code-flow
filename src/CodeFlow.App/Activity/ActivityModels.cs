namespace CodeFlow.Activity;

/// <summary>
/// One recorded chat turn: the question, the answer, and how it was produced.
/// </summary>
/// <remarks>
/// <para>
/// A row of <c>activity_log</c>. The chat itself only lives in memory for the session, so without
/// this table a restart silently loses everything that was ever asked.
/// </para>
/// <para>
/// The two ids are deliberately separate. <paramref name="SessionId"/> is the <em>app's</em>
/// conversation id, minted by the frontend and stable for the conversation's whole life — it is what
/// turns group under in the history list. <paramref name="EngineSessionId"/> is the CLI's own resume
/// token, which is not a usable grouping key: Gemini reports one fixed sentinel for every run, and
/// the Claude CLI can mint a fresh id on each resumed turn.
/// </para>
/// </remarks>
/// <param name="Trace">
/// The lines the engine printed while producing this answer, as a JSON array, so a finished turn can
/// still show <em>how</em> it got there long after the live log is gone.
/// </param>
/// <param name="IsError">
/// This turn failed and <paramref name="Answer"/> is the raw engine error — still carrying its quota
/// marker, so a reopened conversation can re-derive the billing notice.
/// </param>
public sealed record ActivityLogEntry(
    string Id,
    string ProjectId,
    string? SessionId,
    string? EngineSessionId,
    string Question,
    string Answer,
    string? Trace,
    string CreatedAt,
    long? ResponseTimeMs,
    bool IsError,
    string? Provider,
    string? Model,
    string? EngineVersion);

/// <summary>
/// One conversation, summarised for the history list.
/// </summary>
/// <remarks>
/// Derived, not stored: a group over <c>activity_log</c> plus any <c>conversation_titles</c>
/// override. There is no conversation table, which is exactly why renaming needs one of its own.
/// </remarks>
/// <param name="Title">The first question asked, unless the user renamed the conversation.</param>
/// <param name="CreatedAt">When the first turn was recorded.</param>
/// <param name="UpdatedAt">When the most recent turn was recorded; the list sorts on this.</param>
public sealed record ChatConversationSummary(
    string SessionId,
    string ProjectId,
    string Title,
    string CreatedAt,
    string UpdatedAt,
    long TurnCount);

/// <summary>
/// One finished PR review or pre-commit analysis.
/// </summary>
/// <remarks>
/// Only completed runs are recorded — successful or errored. A run still in flight when the app
/// closed has nothing to reopen, and a run the user stopped is not history: filing it would leave a
/// permanent red row for something they did on purpose.
/// </remarks>
/// <param name="Id">
/// Supplied by the caller, not minted here: it is the id the frontend's in-memory job already has,
/// so a job running this session and the same job reloaded after a restart share one identity and
/// renaming or deleting either always hits the right row.
/// </param>
/// <param name="Label">The generated label. <paramref name="CustomLabel"/> overrides it in the UI.</param>
/// <param name="Meta">Opaque JSON the frontend round-trips; the backend never reads inside it.</param>
public sealed record JobHistoryEntry(
    string Id,
    string ProjectId,
    string Kind,
    string Label,
    string? CustomLabel,
    string Status,
    string? Result,
    string? Error,
    string Meta,
    string CreatedAt);

/// <summary>Who answered a chat turn, on what, and how long it took.</summary>
/// <remarks>
/// Grouped into one argument so <see cref="ActivityLogStore.Add"/> keeps a readable signature as
/// this grows. Every field is nullable because a turn that failed before the CLI started has no
/// model to report, and an HTTP engine has no version.
/// </remarks>
public sealed record TurnMeta(
    string? Provider = null,
    string? Model = null,
    string? EngineVersion = null,
    long? ResponseTimeMs = null);
