using MySqlConnector;

namespace CodeFlow.Dbml.Introspectors;

/// <summary>
/// Reads a MySQL or MariaDB schema (<c>DBML-026</c>).
/// </summary>
/// <remarks>
/// The engine where "schema" and "database" are the same word. A connection names one database and
/// every query is scoped to it with <c>DATABASE()</c> — without that, <c>information_schema</c>
/// happily returns every table on the server, including the server's own.
/// <para>
/// Its catalogue is the friendliest of the four: <c>COLUMN_TYPE</c> already carries the arguments
/// (<c>varchar(120)</c>, <c>decimal(10,2)</c>, and an <c>enum('a','b')</c> inline), and
/// <c>EXTRA</c> says <c>auto_increment</c> outright.
/// </para>
/// </remarks>
internal sealed class MySqlIntrospector : IDbmlIntrospector
{
    public string Driver => DbmlDrivers.MySql;

    public async Task ProbeAsync(DbmlConnection connection, string? password, CancellationToken cancellationToken)
    {
        await using var db = Open(connection, password);
        await Catalogue.OpenOrRefuseAsync(db, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DbmlSchemaSnapshot> ReadAsync(
        DbmlConnection connection, string? password, CancellationToken cancellationToken)
    {
        await using var db = Open(connection, password);
        await Catalogue.OpenOrRefuseAsync(db, cancellationToken).ConfigureAwait(false);

        var columns = await Catalogue.QueryAsync(db,
            """
            SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.COLUMN_TYPE,
                   c.IS_NULLABLE = 'NO',
                   c.EXTRA LIKE '%auto_increment%',
                   c.COLUMN_DEFAULT,
                   c.ORDINAL_POSITION
            FROM information_schema.COLUMNS c
            JOIN information_schema.TABLES t
              ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            WHERE t.TABLE_TYPE = 'BASE TABLE' AND c.TABLE_SCHEMA = DATABASE()
            ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION
            """,
            r => new ColumnRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetBoolean(4), r.GetBoolean(5), Literal(r.TextOrNull(6)), r.GetInt32(7)),
            cancellationToken).ConfigureAwait(false);

        var keys = await Catalogue.QueryAsync(db,
            """
            SELECT s.TABLE_SCHEMA, s.TABLE_NAME, s.INDEX_NAME, s.COLUMN_NAME,
                   s.INDEX_NAME = 'PRIMARY', s.SEQ_IN_INDEX
            FROM information_schema.STATISTICS s
            WHERE s.TABLE_SCHEMA = DATABASE() AND s.NON_UNIQUE = 0
            """,
            r => new KeyRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetInt32(5)),
            cancellationToken).ConfigureAwait(false);

        var foreignKeys = await Catalogue.QueryAsync(db,
            """
            SELECT k.CONSTRAINT_NAME,
                   k.TABLE_SCHEMA, k.TABLE_NAME, k.COLUMN_NAME,
                   k.REFERENCED_TABLE_SCHEMA, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME,
                   r.DELETE_RULE, r.UPDATE_RULE, k.ORDINAL_POSITION
            FROM information_schema.KEY_COLUMN_USAGE k
            JOIN information_schema.REFERENTIAL_CONSTRAINTS r
              ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
            WHERE k.TABLE_SCHEMA = DATABASE() AND k.REFERENCED_TABLE_NAME IS NOT NULL
            """,
            r => new ForeignKeyRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetString(6),
                r.TextOrNull(7), r.TextOrNull(8), r.GetInt32(9)),
            cancellationToken).ConfigureAwait(false);

        var indexes = await Catalogue.QueryAsync(db,
            """
            SELECT s.TABLE_SCHEMA, s.TABLE_NAME, s.INDEX_NAME, s.COLUMN_NAME,
                   s.NON_UNIQUE = 0, s.SEQ_IN_INDEX
            FROM information_schema.STATISTICS s
            WHERE s.TABLE_SCHEMA = DATABASE() AND s.NON_UNIQUE = 1
            """,
            r => new IndexRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetInt32(5)),
            cancellationToken).ConfigureAwait(false);

        return DbmlSnapshotBuilder.Build(columns, keys, foreignKeys, indexes, []);
    }

    private static MySqlConnection Open(DbmlConnection connection, string? password) =>
        new(new MySqlConnectionStringBuilder
        {
            Server = connection.Host ?? "localhost",
            Port = (uint)(connection.Port ?? DbmlDrivers.DefaultPort(DbmlDrivers.MySql)),
            Database = connection.Database ?? string.Empty,
            UserID = connection.Username ?? string.Empty,
            Password = password ?? string.Empty,
            SslMode = connection.UseTls ? MySqlSslMode.Required : MySqlSslMode.Preferred,
            ConnectionTimeout = Catalogue.TimeoutSeconds,
            Pooling = false,
        }.ToString());

    /// <summary>
    /// A MySQL default, which the catalogue reports unquoted.
    /// </summary>
    /// <remarks>
    /// Unlike the other three it stores the literal as-is, so nothing needs unwrapping — except that
    /// MariaDB and MySQL 8 differ on function defaults: one reports <c>current_timestamp()</c> and
    /// the other <c>CURRENT_TIMESTAMP</c>. Both are passed through as written, since a diagram shows
    /// what the database says.
    /// </remarks>
    private static string? Literal(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw;
}
