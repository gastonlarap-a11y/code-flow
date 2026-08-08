using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodeFlow.Ipc;
using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Activity;

/// <summary>
/// The chat history and job list commands.
/// See <c>docs/business-rules/03-storage.md</c>.
/// </summary>
/// <remarks>
/// Its own feature folder rather than a corner of <c>Ai/</c>: these read what the AI features write,
/// but the history list, the rename and the delete are a surface of their own, reached from the
/// activity panel with no engine involved.
/// </remarks>
public static class ActivityCommands
{
    public static CommandRegistry AddActivityCommands(this CommandRegistry registry, Database database) =>
        registry
            // ---------- chat conversations ----------
            .Add("list_chat_conversations", (p, ct) =>
            {
                var projectId = Arg(p, "projectId");
                var search = OptionalArg(p, "search");
                return Read(database, c => ActivityLogStore.Conversations(c, projectId, search),
                    ActivityJsonContext.Default.ListChatConversationSummary, ct);
            })
            .Add("get_chat_conversation", (p, ct) =>
            {
                var projectId = Arg(p, "projectId");
                var sessionId = Arg(p, "sessionId");
                return Read(database, c => ActivityLogStore.Messages(c, projectId, sessionId),
                    ActivityJsonContext.Default.ListActivityLogEntry, ct);
            })
            .Add("delete_chat_conversation", (p, ct) =>
            {
                var projectId = Arg(p, "projectId");
                var sessionId = Arg(p, "sessionId");
                return WriteUnit(database, c => ActivityLogStore.DeleteConversation(c, projectId, sessionId), ct);
            })
            .Add("rename_chat_conversation", (p, ct) =>
            {
                var projectId = Arg(p, "projectId");
                var sessionId = Arg(p, "sessionId");
                var title = Arg(p, "title");
                return WriteUnit(database,
                    c => ActivityLogStore.RenameConversation(c, projectId, sessionId, title), ct);
            })
            // ---------- job history ----------
            .Add("list_job_history", (p, ct) =>
            {
                var projectId = Arg(p, "projectId");
                return Read(database, c => JobHistoryStore.List(c, projectId),
                    ActivityJsonContext.Default.ListJobHistoryEntry, ct);
            })
            .Add("rename_job_history_entry", (p, ct) =>
            {
                var id = Arg(p, "id");
                var label = Arg(p, "label");
                return WriteUnit(database, c => JobHistoryStore.Rename(c, id, label), ct);
            })
            .Add("delete_job_history_entry", (p, ct) =>
            {
                var id = Arg(p, "id");
                return WriteUnit(database, c => JobHistoryStore.Delete(c, id), ct);
            });

    private static async ValueTask<ReadOnlyMemory<byte>> Read<T>(
        Database database, Func<SqliteConnection, T> work, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var result = await database.ReadAsync(work, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToUtf8Bytes(result, type);
    }

    private static async ValueTask<ReadOnlyMemory<byte>> WriteUnit(
        Database database, Action<SqliteConnection> work, CancellationToken cancellationToken)
    {
        await database.WriteAsync(work, cancellationToken).ConfigureAwait(false);
        return "null"u8.ToArray();
    }

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");

    /// <summary>An argument the renderer sends as <c>null</c> when it has no value to send.</summary>
    /// <remarks>
    /// Only <c>search</c> uses this: absent and null both mean "no filter", which is not the same as
    /// an empty string — 1.7.2 treats <c>Some("")</c> as a needle every turn contains.
    /// </remarks>
    private static string? OptionalArg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Serialisable types this feature puts on the wire.</summary>
/// <remarks>
/// snake_case, because <c>renderer/src/types/domain.ts</c> declares these field names verbatim.
/// A camelCase policy here would compile and then read as <c>undefined</c> in every
/// component — blank rows rather than an error.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ActivityLogEntry))]
[JsonSerializable(typeof(List<ActivityLogEntry>))]
[JsonSerializable(typeof(ChatConversationSummary))]
[JsonSerializable(typeof(List<ChatConversationSummary>))]
[JsonSerializable(typeof(JobHistoryEntry))]
[JsonSerializable(typeof(List<JobHistoryEntry>))]
internal sealed partial class ActivityJsonContext : JsonSerializerContext;
