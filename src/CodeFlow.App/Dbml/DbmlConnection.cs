namespace CodeFlow.Dbml;

/// <summary>
/// A database the schema designer can read a schema out of.
/// See <c>docs/business-rules/15-dbml.md</c>, <c>DBML-023</c>.
/// </summary>
/// <remarks>
/// <b>There is no password on this record, and that is structural.</b> It goes to the OS credential
/// store under <c>db-password:{id}</c> and is read only inside this process, when a connection
/// string is built. This type crosses the IPC boundary in both directions, so a field here is a
/// field the renderer can see (<c>DBML-024</c>).
/// </remarks>
/// <param name="FilePath">SQLite's alternative to host/port/database. Null for the other three.</param>
public sealed record DbmlConnection(
    string Id,
    string Name,
    string Driver,
    string? Host,
    int? Port,
    string? Database,
    string? Username,
    string? FilePath,
    bool UseTls,
    string CreatedAt,
    string UpdatedAt);

/// <summary>What the renderer sends to create or update one.</summary>
/// <remarks>
/// Separate from <see cref="DbmlConnection"/> because the two differ in what they carry: no
/// timestamps, and an optional <paramref name="Password"/> that is split off into the credential
/// store on the way in and never comes back out.
/// </remarks>
public sealed record NewDbmlConnection(
    string? Id,
    string Name,
    string Driver,
    string? Host,
    int? Port,
    string? Database,
    string? Username,
    string? FilePath,
    bool UseTls,
    string? Password);

/// <summary>The engines that can be introspected, verbatim as the renderer sends them.</summary>
public static class DbmlDrivers
{
    public const string Postgres = "postgres";

    public const string SqlServer = "sqlserver";

    public const string MySql = "mysql";

    public const string Sqlite = "sqlite";

    /// <summary>The four, in the order the settings UI lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Postgres, SqlServer, MySql, Sqlite];

    /// <summary>The default port, for a connection that names none.</summary>
    public static int DefaultPort(string driver) => driver switch
    {
        Postgres => 5432,
        SqlServer => 1433,
        MySql => 3306,
        _ => 0,
    };
}
