using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeFlow.Activity;
using CodeFlow.Ipc;
using CodeFlow.Tests.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Activity;

/// <summary>
/// The history command surface.
/// See <c>docs/business-rules/01-ipc-surface.md</c>.
/// </summary>
public sealed class ActivityCommandsTests
{
    /// <summary>
    /// The names <c>renderer/src/lib/ipc/commands.ts</c> invokes.
    /// </summary>
    /// <remarks>
    /// Asserted as an exact set, not a subset: a typo here is a command the renderer calls and nothing
    /// answers, which surfaces as a blank history panel rather than as an error anyone can trace.
    /// </remarks>
    private static readonly string[] Expected =
    [
        "list_chat_conversations", "get_chat_conversation", "delete_chat_conversation",
        "rename_chat_conversation", "list_job_history", "rename_job_history_entry",
        "delete_job_history_entry",
    ];

    [Fact]
    public void The_commands_this_slice_owns_are_registered_under_their_contract_names()
    {
        using var db = new TempDatabase();

        var registry = new CommandRegistry().AddActivityCommands(db.Handle);

        Assert.Equal(
            Expected.OrderBy(n => n, StringComparer.Ordinal),
            registry.Names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_shape_is_spelled_the_way_the_renderer_reads_it()
    {
        // snake_case, matching renderer/src/types/domain.ts:241-288. 1.7.2's models derive
        // Serialize with no rename_all, so the field names crossing the boundary are the stored ones.
        // Getting this wrong compiles, serialises, and then reads as undefined in every component —
        // blank rows rather than an error, which is why it is asserted field by field.
        Assert.Equal(
            [
                "id", "project_id", "session_id", "engine_session_id", "question", "answer", "trace",
                "created_at", "response_time_ms", "is_error", "provider", "model", "engine_version",
            ],
            Names(
                new ActivityLogEntry("i", "p", "s", "e", "q", "a", "t", "c", 1, true, "pr", "m", "v"),
                ActivityJsonContext.Default.ActivityLogEntry));

        Assert.Equal(
            ["session_id", "project_id", "title", "created_at", "updated_at", "turn_count"],
            Names(
                new ChatConversationSummary("s", "p", "t", "c", "u", 1),
                ActivityJsonContext.Default.ChatConversationSummary));

        Assert.Equal(
            [
                "id", "project_id", "kind", "label", "custom_label", "status", "result", "error",
                "meta", "created_at",
            ],
            Names(
                new JobHistoryEntry("i", "p", "k", "l", "cl", "s", "r", "e", "{}", "c"),
                ActivityJsonContext.Default.JobHistoryEntry));
    }

    private static IEnumerable<string> Names<T>(T value, JsonTypeInfo<T> type) =>
        JsonSerializer.SerializeToDocument(value, type).RootElement.EnumerateObject().Select(p => p.Name);
}
