using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodeFlow.Ipc;
using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.ApiClient;

/// <summary>
/// The twenty-seven storage commands — the API tester's tree,
/// environments, history and cookie jar.
/// </summary>
/// <remarks>
/// Everything here is SQLite; nothing reaches the network. The transport commands
/// (<c>api_send_http</c> and the streaming protocols) are separate slices.
/// </remarks>
public static class ApiCommands
{
    public static CommandRegistry AddApiCommands(this CommandRegistry registry, Database database) =>
        registry

            // ---------- the tree ----------

            .Add("api_load_tree", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                return Read(database, c => ApiTreeStore.LoadTree(c, workspaceId),
                    ApiJsonContext.Default.ApiTree, ct);
            })
            .Add("api_create_collection", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                var name = Arg(p, "name");
                return Write(database, c => ApiTreeStore.CreateCollection(c, workspaceId, name),
                    ApiJsonContext.Default.ApiCollection, ct);
            })
            .Add("api_update_collection", (p, ct) =>
            {
                var row = Body(p, "collection", ApiJsonContext.Default.ApiCollection);
                return WriteUnit(database, c => ApiTreeStore.UpdateCollection(c, row), ct);
            })
            .Add("api_delete_collection", (p, ct) =>
            {
                var id = Arg(p, "id");
                return WriteUnit(database, c => ApiTreeStore.DeleteCollection(c, id), ct);
            })
            .Add("api_duplicate_collection", (p, ct) =>
            {
                var id = Arg(p, "id");
                return Write(database, c => ApiTreeStore.DuplicateCollection(c, id),
                    ApiJsonContext.Default.ApiCollection, ct);
            })
            .Add("api_reorder_collections", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                var ids = Strings(p, "ids");
                return WriteUnit(database, c => ApiTreeStore.ReorderCollections(c, workspaceId, ids), ct);
            })

            // ---------- folders ----------

            .Add("api_create_folder", (p, ct) =>
            {
                var collectionId = Arg(p, "collectionId");
                var parentId = OptionalArg(p, "parentId");
                var name = Arg(p, "name");
                return Write(database, c => ApiTreeStore.CreateFolder(c, collectionId, parentId, name),
                    ApiJsonContext.Default.ApiFolder, ct);
            })
            .Add("api_update_folder", (p, ct) =>
            {
                var row = Body(p, "folder", ApiJsonContext.Default.ApiFolder);
                return WriteUnit(database, c => ApiTreeStore.UpdateFolder(c, row), ct);
            })
            .Add("api_delete_folder", (p, ct) =>
            {
                var id = Arg(p, "id");
                return WriteUnit(database, c => ApiTreeStore.DeleteFolder(c, id), ct);
            })

            // ---------- requests ----------

            .Add("api_create_request", (p, ct) =>
            {
                var collectionId = Arg(p, "collectionId");
                var folderId = OptionalArg(p, "folderId");
                var name = Arg(p, "name");
                var protocol = Arg(p, "protocol");
                var spec = Arg(p, "spec");
                return Write(database,
                    c => ApiTreeStore.CreateRequest(c, collectionId, folderId, name, protocol, spec),
                    ApiJsonContext.Default.ApiRequestRow, ct);
            })
            .Add("api_update_request", (p, ct) =>
            {
                var row = Body(p, "request", ApiJsonContext.Default.ApiRequestRow);
                return WriteUnit(database, c => ApiTreeStore.UpdateRequest(c, row), ct);
            })
            .Add("api_delete_request", (p, ct) =>
            {
                var id = Arg(p, "id");
                return WriteUnit(database, c => ApiTreeStore.DeleteRequest(c, id), ct);
            })
            .Add("api_duplicate_request", (p, ct) =>
            {
                var id = Arg(p, "id");
                return Write(database, c => ApiTreeStore.DuplicateRequest(c, id),
                    ApiJsonContext.Default.ApiRequestRow, ct);
            })
            .Add("api_move_node", (p, ct) =>
            {
                var kind = Arg(p, "kind");
                var id = Arg(p, "id");
                var collectionId = Arg(p, "collectionId");
                var parentId = OptionalArg(p, "parentId");
                var index = Number(p, "index");
                return WriteUnit(database, c => ApiTreeStore.MoveNode(c, kind, id, collectionId, parentId, index), ct);
            })

            // ---------- environments ----------

            .Add("api_list_environments", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                return Read(database, c => ApiEnvironmentStore.List(c, workspaceId),
                    ApiJsonContext.Default.ListApiEnvironment, ct);
            })
            .Add("api_create_environment", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                var name = Arg(p, "name");
                return Write(database, c => ApiEnvironmentStore.Create(c, workspaceId, name),
                    ApiJsonContext.Default.ApiEnvironment, ct);
            })
            .Add("api_update_environment", (p, ct) =>
            {
                var row = Body(p, "environment", ApiJsonContext.Default.ApiEnvironment);
                return WriteUnit(database, c => ApiEnvironmentStore.Update(c, row), ct);
            })
            .Add("api_delete_environment", (p, ct) =>
            {
                var id = Arg(p, "id");
                return WriteUnit(database, c => ApiEnvironmentStore.Delete(c, id), ct);
            })
            .Add("api_duplicate_environment", (p, ct) =>
            {
                var id = Arg(p, "id");
                return Write(database, c => ApiEnvironmentStore.Duplicate(c, id),
                    ApiJsonContext.Default.ApiEnvironment, ct);
            })

            // ---------- history ----------

            .Add("api_list_history", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                var limit = Number(p, "limit");
                return Read(database, c => ApiHistoryStore.List(c, workspaceId, limit),
                    ApiJsonContext.Default.ListApiHistoryEntry, ct);
            })
            .Add("api_add_history", (p, ct) =>
            {
                var entry = Body(p, "entry", ApiJsonContext.Default.ApiHistoryEntry);
                return WriteUnit(database, c => ApiHistoryStore.Add(c, entry), ct);
            })
            .Add("api_delete_history", (p, ct) =>
            {
                var id = Arg(p, "id");
                return WriteUnit(database, c => ApiHistoryStore.Delete(c, id), ct);
            })
            .Add("api_clear_history", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                return WriteUnit(database, c => ApiHistoryStore.Clear(c, workspaceId), ct);
            })

            // ---------- cookies ----------

            .Add("api_list_cookies", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                return Read(database, c => ApiCookieStore.List(c, workspaceId),
                    ApiJsonContext.Default.ListApiCookie, ct);
            })
            .Add("api_upsert_cookie", (p, ct) =>
            {
                var cookie = Body(p, "cookie", ApiJsonContext.Default.ApiCookie);
                return WriteUnit(database, c => ApiCookieStore.Upsert(c, cookie), ct);
            })
            .Add("api_delete_cookie", (p, ct) =>
            {
                var id = Arg(p, "id");
                return WriteUnit(database, c => ApiCookieStore.Delete(c, id), ct);
            })
            .Add("api_clear_cookies", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                return WriteUnit(database, c => ApiCookieStore.Clear(c, workspaceId), ct);
            });

    // ---------- dispatch helpers ----------

    private static async ValueTask<ReadOnlyMemory<byte>> Read<T>(
        Database database, Func<SqliteConnection, T> work, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var result = await database.ReadAsync(work, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToUtf8Bytes(result, type);
    }

    private static async ValueTask<ReadOnlyMemory<byte>> Write<T>(
        Database database, Func<SqliteConnection, T> work, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var result = await database.WriteAsync(work, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToUtf8Bytes(result, type);
    }

    private static async ValueTask<ReadOnlyMemory<byte>> WriteUnit(
        Database database, Action<SqliteConnection> work, CancellationToken cancellationToken)
    {
        await database.WriteAsync(work, cancellationToken).ConfigureAwait(false);
        return "null"u8.ToArray();
    }

    // ---------- argument helpers ----------
    //
    // Scalars arrive camelCase, which is how the renderer sends argument names. The whole rows
    // below do not: they are objects the renderer round-trips straight back, so their keys are the
    // snake_case ones this feature returned — the same asymmetry `create_project`'s `input`
    // carries.

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");

    /// <summary>An argument the renderer sends as <c>null</c> to mean "directly under the parent".</summary>
    private static string? OptionalArg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Number(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
            ? number
            : throw new ArgumentException($"missing required parameter '{name}'");

    private static IReadOnlyList<string> Strings(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(e => e.GetString() ?? string.Empty)]
            : throw new ArgumentException($"missing required parameter '{name}'");

    /// <summary>Reads a whole row the renderer is sending back.</summary>
    private static T Body<T>(JsonElement parameters, string name, JsonTypeInfo<T> type) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.Deserialize(type) ?? throw new ArgumentException($"parameter '{name}' deserialised to null")
            : throw new ArgumentException($"missing required parameter '{name}'");
}

/// <summary>Everything this feature puts on the wire, in both directions.</summary>
/// <remarks>
/// snake_case, and here that matters more than usual: the tree's rows travel <em>out</em> to the
/// renderer, are edited there, and come back <em>in</em> to the update commands unchanged. One
/// naming policy has to describe both journeys, and it is 1.7.2's — <c>is_global</c>,
/// <c>http_only</c>, <c>duration_ms</c>, <c>sort_order</c>, all of which
/// <c>renderer/src/types/api.ts</c> declares verbatim.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ApiTree))]
[JsonSerializable(typeof(ApiCollection))]
[JsonSerializable(typeof(ApiFolder))]
[JsonSerializable(typeof(ApiRequestRow))]
[JsonSerializable(typeof(ApiEnvironment))]
[JsonSerializable(typeof(List<ApiEnvironment>))]
[JsonSerializable(typeof(ApiHistoryEntry))]
[JsonSerializable(typeof(List<ApiHistoryEntry>))]
[JsonSerializable(typeof(ApiCookie))]
[JsonSerializable(typeof(List<ApiCookie>))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;
