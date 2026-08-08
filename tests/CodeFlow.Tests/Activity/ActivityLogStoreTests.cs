using CodeFlow.Activity;
using CodeFlow.Storage;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeFlow.Tests.Activity;

/// <summary>
/// The chat transcript and the conversation list.
/// See <c>docs/business-rules/03-storage.md</c> <c>STORE-014</c>.
/// </summary>
public sealed class ActivityLogStoreTests
{
    [Fact]
    public void One_conversation_stays_one_activity_even_when_the_engine_changes_session_id()
    {
        // The reason the app mints its own conversation id instead of borrowing the engine's: the
        // Claude CLI can hand back a fresh token on every resumed turn.
        using var db = new TempDatabase();
        var project = Project(db);

        Turn(db, project, "conv-1", engineSession: "sess-a", question: "first");
        Turn(db, project, "conv-1", engineSession: "sess-b", question: "second");
        Turn(db, project, "conv-1", engineSession: "sess-c", question: "third");

        var conversation = Assert.Single(db.Use(c => ActivityLogStore.Conversations(c, project, null)));

        Assert.Equal("conv-1", conversation.SessionId);
        Assert.Equal(3, conversation.TurnCount);
        Assert.Equal("first", conversation.Title);
    }

    [Fact]
    public void Three_conversations_stay_three_even_when_the_engine_reports_one_sentinel()
    {
        // The mirror image: Gemini reports the same fixed token for every run, so grouping on the
        // engine's id would collapse every chat the user ever had into a single activity.
        using var db = new TempDatabase();
        var project = Project(db);

        Turn(db, project, "conv-1", engineSession: "agy-last", question: "about auth");
        Turn(db, project, "conv-2", engineSession: "agy-last", question: "about routing");
        Turn(db, project, "conv-3", engineSession: "agy-last", question: "about tests");

        var conversations = db.Use(c => ActivityLogStore.Conversations(c, project, null));

        Assert.Equal(3, conversations.Count);
        Assert.Equal(["conv-3", "conv-2", "conv-1"], conversations.Select(s => s.SessionId));
    }

    [Fact]
    public void A_turn_recorded_before_session_tracking_existed_is_invisible_forever()
    {
        // STORE-014, reproduced rather than corrected: no migration backfills a synthetic id, and
        // inventing one here would surface conversations 1.7.2 never showed.
        using var db = new TempDatabase();
        var project = Project(db);

        db.Do(c => Sql.Execute(c,
            "INSERT INTO activity_log (id, project_id, session_id, question, answer, created_at) " +
            "VALUES ('legacy', $projectId, NULL, 'orphan', 'answer', '2024-01-01T00:00:00.0000000+00:00')",
            ("$projectId", project)));

        Turn(db, project, "conv-1", engineSession: null, question: "tracked");

        var conversation = Assert.Single(db.Use(c => ActivityLogStore.Conversations(c, project, null)));
        Assert.Equal("tracked", conversation.Title);
    }

    [Fact]
    public void Search_covers_the_whole_exchange_and_not_just_the_title()
    {
        using var db = new TempDatabase();
        var project = Project(db);

        Turn(db, project, "conv-1", engineSession: null, question: "how do I log in", answer: "use the session cookie");
        Turn(db, project, "conv-2", engineSession: null, question: "what about caching", answer: "no cache yet");

        // Matches the answer of the first conversation only.
        var byAnswer = db.Use(c => ActivityLogStore.Conversations(c, project, "COOKIE"));
        Assert.Equal(["conv-1"], byAnswer.Select(s => s.SessionId));

        // Matches nothing at all, which is an empty list rather than everything.
        Assert.Empty(db.Use(c => ActivityLogStore.Conversations(c, project, "kubernetes")));
    }

    [Fact]
    public void A_renamed_conversation_keeps_its_turns_and_only_swaps_the_title()
    {
        using var db = new TempDatabase();
        var project = Project(db);

        Turn(db, project, "conv-1", engineSession: null, question: "the original first question");
        db.Do(c => ActivityLogStore.RenameConversation(c, project, "conv-1", "Auth work"));

        var conversation = Assert.Single(db.Use(c => ActivityLogStore.Conversations(c, project, null)));
        Assert.Equal("Auth work", conversation.Title);

        // The rename lives in its own table; the transcript is untouched.
        var turn = Assert.Single(db.Use(c => ActivityLogStore.Messages(c, project, "conv-1")));
        Assert.Equal("the original first question", turn.Question);

        // Renaming twice replaces rather than conflicts.
        db.Do(c => ActivityLogStore.RenameConversation(c, project, "conv-1", "Auth work, part two"));
        Assert.Equal(
            "Auth work, part two",
            Assert.Single(db.Use(c => ActivityLogStore.Conversations(c, project, null))).Title);
    }

    [Fact]
    public void Turns_come_back_oldest_first_so_the_transcript_reads_in_order()
    {
        using var db = new TempDatabase();
        var project = Project(db);

        Turn(db, project, "conv-1", engineSession: null, question: "one");
        Turn(db, project, "conv-1", engineSession: null, question: "two");
        Turn(db, project, "conv-1", engineSession: null, question: "three");

        Assert.Equal(
            ["one", "two", "three"],
            db.Use(c => ActivityLogStore.Messages(c, project, "conv-1")).Select(t => t.Question));
    }

    [Fact]
    public void Every_field_of_a_turn_survives_a_round_trip()
    {
        using var db = new TempDatabase();
        var project = Project(db);

        var written = db.Use(c => ActivityLogStore.Add(
            c, project, "conv-1", "sess-a", "why is this slow?", "QUOTA_EXCEEDED::out of credit",
            """[{"stream":"stderr","line":"probing"}]""",
            new TurnMeta("opencode", "grok-code", "0.4.2", 1234),
            isError: true));

        Assert.Equal(written, Assert.Single(db.Use(c => ActivityLogStore.Messages(c, project, "conv-1"))));
        Assert.True(written.IsError);
        Assert.Equal(1234, written.ResponseTimeMs);
    }

    [Fact]
    public void The_last_turn_provider_is_what_guards_a_cross_engine_resume()
    {
        using var db = new TempDatabase();
        var project = Project(db);

        Assert.Null(db.Use(c => ActivityLogStore.LastTurnProvider(c, project, "conv-1")));

        Turn(db, project, "conv-1", engineSession: null, question: "first", provider: "claude");
        Assert.Equal("claude", db.Use(c => ActivityLogStore.LastTurnProvider(c, project, "conv-1")));

        Turn(db, project, "conv-1", engineSession: null, question: "second", provider: "codex");
        Assert.Equal("codex", db.Use(c => ActivityLogStore.LastTurnProvider(c, project, "conv-1")));

        // A turn recorded before provider tracking existed does not overwrite the answer with null.
        Turn(db, project, "conv-1", engineSession: null, question: "third", provider: null);
        Assert.Equal("codex", db.Use(c => ActivityLogStore.LastTurnProvider(c, project, "conv-1")));

        Assert.Null(db.Use(c => ActivityLogStore.LastTurnProvider(c, project, "conv-unknown")));
    }

    [Fact]
    public void Deleting_a_conversation_takes_its_title_with_it_and_leaves_the_others_alone()
    {
        using var db = new TempDatabase();
        var project = Project(db);

        Turn(db, project, "conv-1", engineSession: null, question: "doomed");
        Turn(db, project, "conv-2", engineSession: null, question: "spared");
        db.Do(c => ActivityLogStore.RenameConversation(c, project, "conv-1", "Doomed"));

        db.Do(c => ActivityLogStore.DeleteConversation(c, project, "conv-1"));

        Assert.Equal(["conv-2"], db.Use(c => ActivityLogStore.Conversations(c, project, null)).Select(s => s.SessionId));
        Assert.Equal(0, db.Use(c => Count(c, "conversation_titles")));
    }

    [Fact]
    public void Deleting_the_project_cascades_to_its_whole_transcript()
    {
        using var db = new TempDatabase();
        var project = Project(db);

        Turn(db, project, "conv-1", engineSession: null, question: "asked");
        db.Do(c => ActivityLogStore.RenameConversation(c, project, "conv-1", "Named"));

        db.Do(c => ProjectStore.Delete(c, project));

        Assert.Equal(0, db.Use(c => Count(c, "activity_log")));
        Assert.Equal(0, db.Use(c => Count(c, "conversation_titles")));
    }

    private static string Project(TempDatabase db)
    {
        var workspace = db.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#6366f1"));
        return db.Use(c => ProjectStore.Create(c, WorkspaceStoreTests.NewProjectIn(workspace.Id))).Id;
    }

    /// <summary>
    /// Records one turn, guaranteeing it sorts after the previous one.
    /// </summary>
    /// <remarks>
    /// The pause is the point: <c>created_at</c> is the only ordering key in this schema, and two
    /// inserts inside the clock's resolution would leave "the first question" and "the latest turn"
    /// down to SQLite's tie-breaking rather than to anything asserted here.
    /// </remarks>
    private static void Turn(
        TempDatabase db,
        string projectId,
        string conversationId,
        string? engineSession,
        string question,
        string answer = "an answer",
        string? provider = "claude")
    {
        db.Use(c => ActivityLogStore.Add(
            c, projectId, conversationId, engineSession, question, answer, trace: null,
            new TurnMeta(provider, "a-model", "1.2.3", 100),
            isError: false));

        Thread.Sleep(2);
    }

    private static long Count(SqliteConnection connection, string table) =>
        Sql.Query(connection, $"SELECT COUNT(*) FROM {table}", reader => reader.GetInt64(0)).Single();
}
