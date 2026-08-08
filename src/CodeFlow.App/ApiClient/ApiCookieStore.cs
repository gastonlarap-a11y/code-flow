using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.ApiClient;

/// <summary>A workspace's cookie jar.</summary>
internal static class ApiCookieStore
{
    private const string Columns =
        "id, workspace_id, domain, path, name, value, secure, http_only, expires, updated_at";

    public static List<ApiCookie> List(SqliteConnection connection, string workspaceId) =>
        Sql.Query(connection,
            $"SELECT {Columns} FROM api_cookies WHERE workspace_id = $workspaceId ORDER BY domain, path, name",
            Read,
            ("$workspaceId", workspaceId));

    /// <summary>
    /// Stores a cookie, keyed on <c>(workspace_id, domain, path, name)</c> rather than on its id
    /// (<c>STORE-020</c>).
    /// </summary>
    /// <remarks>
    /// That triple is the cookie's identity on the wire, so a <c>Set-Cookie</c> for one the jar
    /// already holds has to replace it rather than accumulate — but only within the jar it was set
    /// in, so a staging session in one workspace never overwrites the same host's session in
    /// another. Keying on the row id instead would let the jar fill with stale duplicates of the
    /// same cookie, and the request builder would then have to guess which one is current.
    /// </remarks>
    public static void Upsert(SqliteConnection connection, ApiCookie cookie) =>
        Sql.Execute(connection,
            """
            INSERT INTO api_cookies
                (id, workspace_id, domain, path, name, value, secure, http_only, expires, updated_at)
            VALUES ($id, $workspaceId, $domain, $path, $name, $value, $secure, $httpOnly, $expires, $updatedAt)
            ON CONFLICT(workspace_id, domain, path, name) DO UPDATE SET
                value = excluded.value,
                secure = excluded.secure,
                http_only = excluded.http_only,
                expires = excluded.expires,
                updated_at = excluded.updated_at
            """,
            ("$id", cookie.Id),
            ("$workspaceId", cookie.WorkspaceId),
            ("$domain", cookie.Domain),
            ("$path", cookie.Path),
            ("$name", cookie.Name),
            ("$value", cookie.Value),
            ("$secure", cookie.Secure ? 1 : 0),
            ("$httpOnly", cookie.HttpOnly ? 1 : 0),
            ("$expires", cookie.Expires),
            ("$updatedAt", Clock.Now()));

    public static void Delete(SqliteConnection connection, string id) =>
        Sql.Execute(connection, "DELETE FROM api_cookies WHERE id = $id", ("$id", id));

    public static void Clear(SqliteConnection connection, string workspaceId) =>
        Sql.Execute(connection,
            "DELETE FROM api_cookies WHERE workspace_id = $workspaceId", ("$workspaceId", workspaceId));

    private static ApiCookie Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetBoolean(6),
        reader.GetBoolean(7),
        reader.TextOrNull(8),
        reader.GetString(9));
}
