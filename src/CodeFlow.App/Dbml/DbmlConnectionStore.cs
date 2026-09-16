using System.Globalization;
using CodeFlow.Security;
using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Dbml;

/// <summary>
/// The saved database connections (<c>DBML-023</c>), and the half of each one that is a secret.
/// </summary>
/// <remarks>
/// The split is the point. Everything a person typed goes in <c>db_connections</c>; the password
/// goes to the OS credential store and is never read back out to the renderer. Saving a connection
/// with a blank password leaves whatever was stored alone, which is what makes "edit the port"
/// possible without retyping the secret (<c>DBML-024</c>).
/// </remarks>
internal static class DbmlConnectionStore
{
    public static IReadOnlyList<DbmlConnection> List(SqliteConnection connection) =>
        Sql.Query(connection,
            """
            SELECT id, name, driver, host, port, database, username, file_path, use_tls, created_at, updated_at
            FROM db_connections
            ORDER BY name COLLATE NOCASE, id
            """,
            Read);

    public static DbmlConnection? Get(SqliteConnection connection, string id) =>
        Sql.QuerySingle(connection,
            """
            SELECT id, name, driver, host, port, database, username, file_path, use_tls, created_at, updated_at
            FROM db_connections WHERE id = $id
            """,
            Read,
            ("$id", id));

    /// <summary>Creates or updates one, and files its password if a new one was sent.</summary>
    /// <remarks>
    /// The credential is written <b>after</b> the row, so a failed insert cannot leave a secret
    /// filed under an id that does not exist. The reverse order would leak one every time a
    /// constraint fired.
    /// </remarks>
    public static DbmlConnection Save(SqliteConnection connection, NewDbmlConnection input)
    {
        var id = string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString() : input.Id;
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        Sql.Execute(connection,
            """
            INSERT INTO db_connections
                (id, name, driver, host, port, database, username, file_path, use_tls, created_at, updated_at)
            VALUES
                ($id, $name, $driver, $host, $port, $database, $username, $filePath, $useTls, $now, $now)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                driver = excluded.driver,
                host = excluded.host,
                port = excluded.port,
                database = excluded.database,
                username = excluded.username,
                file_path = excluded.file_path,
                use_tls = excluded.use_tls,
                updated_at = excluded.updated_at
            """,
            ("$id", id),
            ("$name", input.Name),
            ("$driver", input.Driver),
            ("$host", Blank(input.Host)),
            ("$port", input.Port),
            ("$database", Blank(input.Database)),
            ("$username", Blank(input.Username)),
            ("$filePath", Blank(input.FilePath)),
            ("$useTls", input.UseTls ? 1 : 0),
            ("$now", now));

        // Blank means "leave it": the renderer cannot show what is stored, so an untouched password
        // field arrives empty on every edit, and treating that as "clear it" would wipe the secret
        // every time somebody fixed a typo in the host.
        if (!string.IsNullOrEmpty(input.Password))
        {
            CredentialStore.Set(CredentialStore.DbPasswordKey(id), input.Password);
        }

        return Get(connection, id) ?? throw new InvalidOperationException($"connection '{id}' vanished after being saved");
    }

    /// <summary>Removes one, and the password with it.</summary>
    /// <remarks>
    /// The credential first: a row that outlives its secret is a connection that asks for the
    /// password again, while a secret that outlives its row is one nothing will ever read or clean
    /// up. Of the two half-failures, the recoverable one is better.
    /// </remarks>
    public static void Delete(SqliteConnection connection, string id)
    {
        CredentialStore.Delete(CredentialStore.DbPasswordKey(id));
        Sql.Execute(connection, "DELETE FROM db_connections WHERE id = $id", ("$id", id));
    }

    /// <summary>The stored password, for building a connection string inside this process.</summary>
    public static string? PasswordFor(string id) => CredentialStore.Get(CredentialStore.DbPasswordKey(id));

    /// <summary>An optional field, stored as NULL rather than as an empty string.</summary>
    /// <remarks>
    /// One spelling for "absent" in the column, so `host = ''` and `host IS NULL` cannot both mean
    /// the same thing to two different readers.
    /// </remarks>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static DbmlConnection Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.TextOrNull(3),
        reader.IsDBNull(4) ? null : reader.GetInt32(4),
        reader.TextOrNull(5),
        reader.TextOrNull(6),
        reader.TextOrNull(7),
        reader.GetInt32(8) != 0,
        reader.GetString(9),
        reader.GetString(10));
}
