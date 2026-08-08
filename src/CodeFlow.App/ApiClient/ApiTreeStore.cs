using System.Text.Json;
using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.ApiClient;

/// <summary>
/// Collections, folders and requests — the API tester's sidebar tree, from
/// See <c>docs/business-rules/03-storage.md</c>.
/// </summary>
/// <remarks>
/// The specification for these lives with storage, not with
/// <c>docs/business-rules/08-api-client.md</c>: that document owns the transport and says outright
/// that these commands are thin forwarders it does not re-specify.
/// </remarks>
internal static class ApiTreeStore
{
    private const string CollectionColumns =
        "id, workspace_id, name, description, auth, pre_script, post_script, variables, sort_order, created_at, updated_at";

    private const string FolderColumns =
        "id, collection_id, parent_id, name, description, auth, pre_script, post_script, sort_order, created_at";

    private const string RequestColumns =
        "id, collection_id, folder_id, name, protocol, method, url, spec, sort_order, created_at, updated_at";

    /// <summary>
    /// How deep the ancestor walk in <see cref="IsWithinSubtree"/> will go before giving up.
    /// </summary>
    /// <remarks>
    /// Running out is treated as "yes, it is inside itself" — a tree that deep is already a cycle,
    /// and refusing the move is the safe answer.
    /// </remarks>
    private const int MaxFolderDepth = 256;

    /// <summary>Only the roots carry <c>workspace_id</c>; everything else reaches it through them.</summary>
    private const string InWorkspace =
        "collection_id IN (SELECT id FROM api_collections WHERE workspace_id = $workspaceId)";

    // ---------- the tree ----------

    public static ApiTree LoadTree(SqliteConnection connection, string workspaceId) => new(
        Sql.Query(connection,
            $"SELECT {CollectionColumns} FROM api_collections WHERE workspace_id = $workspaceId ORDER BY sort_order, created_at",
            ReadCollection,
            ("$workspaceId", workspaceId)),
        Sql.Query(connection,
            $"SELECT {FolderColumns} FROM api_folders WHERE {InWorkspace} ORDER BY collection_id, sort_order, created_at",
            ReadFolder,
            ("$workspaceId", workspaceId)),
        Sql.Query(connection,
            $"SELECT {RequestColumns} FROM api_requests WHERE {InWorkspace} ORDER BY collection_id, sort_order, created_at",
            ReadRequest,
            ("$workspaceId", workspaceId)));

    // ---------- collections ----------

    public static ApiCollection CreateCollection(SqliteConnection connection, string workspaceId, string name)
    {
        var now = Clock.Now();
        var row = new ApiCollection(
            Guid.NewGuid().ToString(),
            workspaceId,
            name,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "[]",
            NextCollectionOrder(connection, workspaceId),
            now,
            now);

        InsertCollection(connection, row);

        return row;
    }

    /// <summary>Saves the editable fields only.</summary>
    /// <remarks>
    /// <c>sort_order</c> is owned by <see cref="ReorderCollections"/>. Writing it back from a client
    /// that has not seen a concurrent reorder would silently scramble the sidebar.
    /// </remarks>
    public static void UpdateCollection(SqliteConnection connection, ApiCollection row) =>
        Sql.Execute(connection,
            """
            UPDATE api_collections
               SET name = $name, description = $description, auth = $auth, pre_script = $preScript,
                   post_script = $postScript, variables = $variables, updated_at = $updatedAt
             WHERE id = $id
            """,
            ("$id", row.Id),
            ("$name", row.Name),
            ("$description", row.Description),
            ("$auth", row.Auth),
            ("$preScript", row.PreScript),
            ("$postScript", row.PostScript),
            ("$variables", row.Variables),
            ("$updatedAt", Clock.Now()));

    /// <summary>Folders and requests go with it through <c>ON DELETE CASCADE</c>.</summary>
    public static void DeleteCollection(SqliteConnection connection, string id) =>
        Sql.Execute(connection, "DELETE FROM api_collections WHERE id = $id", ("$id", id));

    /// <summary>Deep copy: every folder and request gets a fresh id and the parent links are remapped.</summary>
    /// <remarks>
    /// The copy shares no row with the original and can diverge freely, and it stays in the
    /// source's workspace.
    /// </remarks>
    public static ApiCollection DuplicateCollection(SqliteConnection connection, string id)
    {
        var source = GetCollection(connection, id) ?? throw new InvalidOperationException($"Unknown collection {id}");

        var now = Clock.Now();
        var copy = source with
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{source.Name} copy",
            SortOrder = NextCollectionOrder(connection, source.WorkspaceId),
            CreatedAt = now,
            UpdatedAt = now,
        };

        InsertCollection(connection, copy);

        var folders = Sql.Query(connection,
            $"SELECT {FolderColumns} FROM api_folders WHERE collection_id = $id ORDER BY sort_order, created_at",
            ReadFolder,
            ("$id", id));

        var remap = folders.ToDictionary(f => f.Id, _ => Guid.NewGuid().ToString(), StringComparer.Ordinal);

        // Two passes: a child folder can be listed before its parent, and inserting it with the
        // parent link already set would fail the self-referencing foreign key.
        foreach (var folder in folders)
        {
            InsertFolder(connection, folder with
            {
                Id = remap[folder.Id],
                CollectionId = copy.Id,
                ParentId = null,
                CreatedAt = now,
            });
        }

        foreach (var folder in folders.Where(f => f.ParentId is not null && remap.ContainsKey(f.ParentId)))
        {
            Sql.Execute(connection,
                "UPDATE api_folders SET parent_id = $parentId WHERE id = $id",
                ("$id", remap[folder.Id]),
                ("$parentId", remap[folder.ParentId!]));
        }

        var requests = Sql.Query(connection,
            $"SELECT {RequestColumns} FROM api_requests WHERE collection_id = $id ORDER BY sort_order, created_at",
            ReadRequest,
            ("$id", id));

        foreach (var request in requests)
        {
            InsertRequest(connection, request with
            {
                Id = Guid.NewGuid().ToString(),
                CollectionId = copy.Id,
                FolderId = request.FolderId is not null && remap.TryGetValue(request.FolderId, out var mapped)
                    ? mapped
                    : null,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        return copy;
    }

    /// <summary>Renumbers one workspace's collections to the order the sidebar shows, top to bottom.</summary>
    /// <remarks>
    /// The <c>workspace_id</c> guard is belt-and-braces: a stale list from a workspace the user has
    /// just switched away from renumbers nothing instead of scrambling the one it does belong to.
    /// </remarks>
    public static void ReorderCollections(SqliteConnection connection, string workspaceId, IReadOnlyList<string> ids)
    {
        for (var index = 0; index < ids.Count; index++)
        {
            Sql.Execute(connection,
                "UPDATE api_collections SET sort_order = $order WHERE id = $id AND workspace_id = $workspaceId",
                ("$id", ids[index]),
                ("$workspaceId", workspaceId),
                ("$order", (long)index));
        }
    }

    // ---------- folders ----------

    public static ApiFolder CreateFolder(
        SqliteConnection connection, string collectionId, string? parentId, string name)
    {
        var row = new ApiFolder(
            Guid.NewGuid().ToString(),
            collectionId,
            parentId,
            name,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            NextChildOrder(connection, "api_folders", "parent_id", collectionId, parentId),
            Clock.Now());

        InsertFolder(connection, row);

        return row;
    }

    /// <summary>Editable fields only: the structural columns belong to <see cref="MoveNode"/>.</summary>
    public static void UpdateFolder(SqliteConnection connection, ApiFolder row) =>
        Sql.Execute(connection,
            """
            UPDATE api_folders
               SET name = $name, description = $description, auth = $auth,
                   pre_script = $preScript, post_script = $postScript
             WHERE id = $id
            """,
            ("$id", row.Id),
            ("$name", row.Name),
            ("$description", row.Description),
            ("$auth", row.Auth),
            ("$preScript", row.PreScript),
            ("$postScript", row.PostScript));

    public static void DeleteFolder(SqliteConnection connection, string id) =>
        Sql.Execute(connection, "DELETE FROM api_folders WHERE id = $id", ("$id", id));

    // ---------- requests ----------

    public static ApiRequestRow CreateRequest(
        SqliteConnection connection,
        string collectionId,
        string? folderId,
        string name,
        string protocol,
        string spec)
    {
        var now = Clock.Now();
        var (method, url) = Denormalize(spec);

        var row = new ApiRequestRow(
            Guid.NewGuid().ToString(),
            collectionId,
            folderId,
            name,
            protocol,
            method,
            url,
            spec,
            NextChildOrder(connection, "api_requests", "folder_id", collectionId, folderId),
            now,
            now);

        InsertRequest(connection, row);

        return row;
    }

    public static void UpdateRequest(SqliteConnection connection, ApiRequestRow row) =>
        Sql.Execute(connection,
            """
            UPDATE api_requests
               SET name = $name, protocol = $protocol, method = $method, url = $url,
                   spec = $spec, updated_at = $updatedAt
             WHERE id = $id
            """,
            ("$id", row.Id),
            ("$name", row.Name),
            ("$protocol", row.Protocol),
            ("$method", row.Method),
            ("$url", row.Url),
            ("$spec", row.Spec),
            ("$updatedAt", Clock.Now()));

    public static void DeleteRequest(SqliteConnection connection, string id) =>
        Sql.Execute(connection, "DELETE FROM api_requests WHERE id = $id", ("$id", id));

    public static ApiRequestRow DuplicateRequest(SqliteConnection connection, string id)
    {
        var source = Sql.QuerySingle(connection,
            $"SELECT {RequestColumns} FROM api_requests WHERE id = $id",
            ReadRequest,
            ("$id", id)) ?? throw new InvalidOperationException($"Unknown request {id}");

        var now = Clock.Now();
        var copy = source with
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{source.Name} copy",
            SortOrder = NextChildOrder(connection, "api_requests", "folder_id", source.CollectionId, source.FolderId),
            CreatedAt = now,
            UpdatedAt = now,
        };

        InsertRequest(connection, copy);

        return copy;
    }

    // ---------- moving ----------

    /// <summary>
    /// Reparents one node and renumbers the destination so <c>sort_order</c> stays dense
    /// <c>0..n</c> with the moved node at <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Folders and requests are renumbered against their own kind: they live in separate tables
    /// with independent <c>sort_order</c> columns, and the tree renders folders above requests, so
    /// an index the UI computed is an index within one of the two lists.
    /// </para>
    /// <para>
    /// The two guards catch caller mistakes rather than database failures. The UI guards them too,
    /// but a bug there would corrupt the tree irrecoverably — and a node dragged into another
    /// workspace's collection would vanish from the tree the user is looking at and reappear in one
    /// they cannot see from here.
    /// </para>
    /// </remarks>
    public static void MoveNode(
        SqliteConnection connection,
        string kind,
        string id,
        string collectionId,
        string? parentId,
        long index)
    {
        var (table, parentColumn) = kind switch
        {
            "folder" => ("api_folders", "parent_id"),
            "request" => ("api_requests", "folder_id"),
            _ => throw new InvalidOperationException($"Unknown node kind {kind}"),
        };

        if (kind == "folder" && IsWithinSubtree(connection, id, parentId))
        {
            throw new InvalidOperationException("A folder cannot be moved inside itself");
        }

        var sourceWorkspace = NodeWorkspace(connection, table, id)
            ?? throw new InvalidOperationException($"Unknown {kind} {id}");

        var destinationWorkspace = Sql.QueryText(connection,
            "SELECT workspace_id FROM api_collections WHERE id = $id",
            ("$id", collectionId)) ?? throw new InvalidOperationException($"Unknown collection {collectionId}");

        if (!string.Equals(sourceWorkspace, destinationWorkspace, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A node cannot be moved to a collection in another workspace");
        }

        Sql.Execute(connection,
            $"UPDATE {table} SET collection_id = $collectionId, {parentColumn} = $parentId WHERE id = $id",
            ("$id", id),
            ("$collectionId", collectionId),
            ("$parentId", parentId));

        if (kind == "folder")
        {
            CarrySubtreeToCollection(connection, id, collectionId);
        }

        var siblings = Sql.Query(connection,
            $"""
             SELECT id FROM {table}
              WHERE collection_id = $collectionId AND {parentColumn} IS $parentId AND id <> $id
              ORDER BY sort_order, created_at
             """,
            reader => reader.GetString(0),
            ("$collectionId", collectionId),
            ("$parentId", parentId),
            ("$id", id));

        siblings.Insert((int)Math.Clamp(index, 0, siblings.Count), id);

        for (var order = 0; order < siblings.Count; order++)
        {
            Sql.Execute(connection,
                $"UPDATE {table} SET sort_order = $order WHERE id = $id",
                ("$id", siblings[order]),
                ("$order", (long)order));
        }
    }

    /// <summary>Rewrites <c>collection_id</c> on everything under a folder, itself included.</summary>
    /// <remarks>
    /// Dropping a folder into another collection only moves the folder's own row; its descendants
    /// still name the collection they came from, and <see cref="LoadTree"/> would then render them
    /// under a collection they are no longer reachable from.
    /// </remarks>
    private static void CarrySubtreeToCollection(SqliteConnection connection, string folderId, string collectionId)
    {
        const string Subtree =
            """
            WITH RECURSIVE subtree(id) AS (
                SELECT $folderId
                UNION ALL
                SELECT f.id FROM api_folders f JOIN subtree ON f.parent_id = subtree.id
            )
            """;

        Sql.Execute(connection,
            $"{Subtree} UPDATE api_folders SET collection_id = $collectionId WHERE id IN (SELECT id FROM subtree)",
            ("$folderId", folderId),
            ("$collectionId", collectionId));

        Sql.Execute(connection,
            $"{Subtree} UPDATE api_requests SET collection_id = $collectionId WHERE folder_id IN (SELECT id FROM subtree)",
            ("$folderId", folderId),
            ("$collectionId", collectionId));
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is the folder itself or sits anywhere beneath it.
    /// </summary>
    /// <remarks>
    /// Dropping a folder there would detach the whole subtree from the tree. Walking up from the
    /// candidate is cheaper than walking down from the folder, and the depth cap makes an already
    /// corrupt tree answer "yes" rather than loop.
    /// </remarks>
    private static bool IsWithinSubtree(SqliteConnection connection, string folderId, string? candidate)
    {
        var cursor = candidate;

        for (var depth = 0; depth < MaxFolderDepth; depth++)
        {
            if (cursor is null)
            {
                return false;
            }

            if (string.Equals(cursor, folderId, StringComparison.Ordinal))
            {
                return true;
            }

            cursor = Sql.QueryText(connection,
                "SELECT parent_id FROM api_folders WHERE id = $id",
                ("$id", cursor));
        }

        return true;
    }

    /// <summary>The workspace a node sits in, read through its collection — the only row that records it.</summary>
    private static string? NodeWorkspace(SqliteConnection connection, string table, string id) =>
        Sql.QueryText(connection,
            $"""
             SELECT c.workspace_id FROM {table} n
               JOIN api_collections c ON c.id = n.collection_id
              WHERE n.id = $id
             """,
            ("$id", id));

    // ---------- helpers ----------

    /// <summary>
    /// Next free slot among the children of one parent.
    /// </summary>
    /// <remarks>
    /// <c>IS</c> rather than <c>=</c>, so a null parent — meaning directly under the collection —
    /// matches. The column name differs per table but means the same thing.
    /// </remarks>
    private static long NextChildOrder(
        SqliteConnection connection, string table, string parentColumn, string collectionId, string? parentId)
    {
        var value = Sql.QueryText(connection,
            $"""
             SELECT CAST(COALESCE(MAX(sort_order) + 1, 0) AS TEXT) FROM {table}
              WHERE collection_id = $collectionId AND {parentColumn} IS $parentId
             """,
            ("$collectionId", collectionId),
            ("$parentId", parentId));

        return long.TryParse(value, out var order) ? order : 0;
    }

    private static long NextCollectionOrder(SqliteConnection connection, string workspaceId)
    {
        var value = Sql.QueryText(connection,
            "SELECT CAST(COALESCE(MAX(sort_order) + 1, 0) AS TEXT) FROM api_collections WHERE workspace_id = $workspaceId",
            ("$workspaceId", workspaceId));

        return long.TryParse(value, out var order) ? order : 0;
    }

    /// <summary>Seeds the two denormalised columns from the incoming spec.</summary>
    /// <remarks>
    /// <c>method</c> and <c>url</c> are columns only so the tree can render a row without opening
    /// its blob. Leaving them at the schema default would list a request saved from a filled-in tab
    /// as <c>GET</c> with no URL until its first update.
    /// </remarks>
    private static (string Method, string Url) Denormalize(string spec)
    {
        try
        {
            using var parsed = JsonDocument.Parse(spec);
            var method = Text(parsed.RootElement, "method");

            return (method.Length == 0 ? "GET" : method, Text(parsed.RootElement, "url"));
        }
        catch (JsonException)
        {
            // An unparseable spec is still saved, and
            // its row simply carries the defaults.
            return ("GET", string.Empty);
        }

        static string Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()!
                : string.Empty;
    }

    private static ApiCollection? GetCollection(SqliteConnection connection, string id) =>
        Sql.QuerySingle(connection,
            $"SELECT {CollectionColumns} FROM api_collections WHERE id = $id", ReadCollection, ("$id", id));

    private static void InsertCollection(SqliteConnection connection, ApiCollection row) =>
        Sql.Execute(connection,
            """
            INSERT INTO api_collections
                (id, workspace_id, name, description, auth, pre_script, post_script, variables,
                 sort_order, created_at, updated_at)
            VALUES ($id, $workspaceId, $name, $description, $auth, $preScript, $postScript, $variables,
                    $sortOrder, $createdAt, $updatedAt)
            """,
            ("$id", row.Id),
            ("$workspaceId", row.WorkspaceId),
            ("$name", row.Name),
            ("$description", row.Description),
            ("$auth", row.Auth),
            ("$preScript", row.PreScript),
            ("$postScript", row.PostScript),
            ("$variables", row.Variables),
            ("$sortOrder", row.SortOrder),
            ("$createdAt", row.CreatedAt),
            ("$updatedAt", row.UpdatedAt));

    private static void InsertFolder(SqliteConnection connection, ApiFolder row) =>
        Sql.Execute(connection,
            """
            INSERT INTO api_folders
                (id, collection_id, parent_id, name, description, auth, pre_script, post_script,
                 sort_order, created_at)
            VALUES ($id, $collectionId, $parentId, $name, $description, $auth, $preScript, $postScript,
                    $sortOrder, $createdAt)
            """,
            ("$id", row.Id),
            ("$collectionId", row.CollectionId),
            ("$parentId", row.ParentId),
            ("$name", row.Name),
            ("$description", row.Description),
            ("$auth", row.Auth),
            ("$preScript", row.PreScript),
            ("$postScript", row.PostScript),
            ("$sortOrder", row.SortOrder),
            ("$createdAt", row.CreatedAt));

    private static void InsertRequest(SqliteConnection connection, ApiRequestRow row) =>
        Sql.Execute(connection,
            """
            INSERT INTO api_requests
                (id, collection_id, folder_id, name, protocol, method, url, spec, sort_order,
                 created_at, updated_at)
            VALUES ($id, $collectionId, $folderId, $name, $protocol, $method, $url, $spec, $sortOrder,
                    $createdAt, $updatedAt)
            """,
            ("$id", row.Id),
            ("$collectionId", row.CollectionId),
            ("$folderId", row.FolderId),
            ("$name", row.Name),
            ("$protocol", row.Protocol),
            ("$method", row.Method),
            ("$url", row.Url),
            ("$spec", row.Spec),
            ("$sortOrder", row.SortOrder),
            ("$createdAt", row.CreatedAt),
            ("$updatedAt", row.UpdatedAt));

    private static ApiCollection ReadCollection(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetInt64(8),
        reader.GetString(9),
        reader.GetString(10));

    private static ApiFolder ReadFolder(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.TextOrNull(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetInt64(8),
        reader.GetString(9));

    private static ApiRequestRow ReadRequest(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.TextOrNull(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetInt64(8),
        reader.GetString(9),
        reader.GetString(10));
}
