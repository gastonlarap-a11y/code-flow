using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Dbml.Introspectors;

/// <summary>
/// Reads a SQLite file through its `PRAGMA` catalogue (<c>DBML-026</c>).
/// </summary>
/// <remarks>
/// The odd one out, three times over. It has no server, so `file_path` replaces host/port/database
/// and there is no password. It has no schemas, so everything is filed under `public` — the same
/// name `@dbml/core` uses for "no namespace", so the emitted document declares none. And its
/// catalogue is `PRAGMA` functions rather than an `information_schema`, which means one round trip
/// per table instead of one query for all of them; fine for a file on the local disk, and the reason
/// the other three do not work this way.
/// <para>
/// **Opened read-only** — `SqliteOpenMode.ReadOnlyOnly` refuses rather than creating a file, so
/// pointing this at a path that does not exist says so instead of leaving an empty database behind.
/// </para>
/// </remarks>
internal sealed class SqliteIntrospector : IDbmlIntrospector
{
    /// <summary>What SQLite calls the namespace it has none of.</summary>
    private const string Schema = "public";

    public string Driver => DbmlDrivers.Sqlite;

    public async Task ProbeAsync(DbmlConnection connection, string? password, CancellationToken cancellationToken)
    {
        await using var db = await OpenAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DbmlSchemaSnapshot> ReadAsync(
        DbmlConnection connection, string? password, CancellationToken cancellationToken)
    {
        await using var db = await OpenAsync(connection, cancellationToken).ConfigureAwait(false);

        var columns = new List<ColumnRow>();
        var keys = new List<KeyRow>();
        var foreignKeys = new List<ForeignKeyRow>();
        var indexes = new List<IndexRow>();

        foreach (var (table, ddl) in await TablesAsync(db, cancellationToken).ConfigureAwait(false))
        {
            await ReadColumnsAsync(db, table, ddl, columns, keys, cancellationToken).ConfigureAwait(false);
            await ReadForeignKeysAsync(db, table, foreignKeys, cancellationToken).ConfigureAwait(false);
            await ReadIndexesAsync(db, table, keys, indexes, cancellationToken).ConfigureAwait(false);
        }

        return DbmlSnapshotBuilder.Build(columns, keys, foreignKeys, indexes, []);
    }

    private static async Task<SqliteConnection> OpenAsync(
        DbmlConnection connection, CancellationToken cancellationToken)
    {
        var path = connection.FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new DbmlConnectionException("this connection has no database file");
        }

        if (!File.Exists(path))
        {
            throw new DbmlConnectionException($"there is no database file at {path}");
        }

        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            // Not `ReadOnly`: that one still creates the file when it is missing.
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());

        try
        {
            await db.OpenAsync(cancellationToken).ConfigureAwait(false);
            return db;
        }
        catch (SqliteException e)
        {
            await db.DisposeAsync().ConfigureAwait(false);
            throw new DbmlConnectionException(e.Message, e);
        }
    }

    /// <summary>Every user table, with the DDL it was created by.</summary>
    /// <remarks>
    /// The DDL is read for one thing only: `AUTOINCREMENT`, which no `PRAGMA` reports. Without it an
    /// `INTEGER PRIMARY KEY` and an `INTEGER PRIMARY KEY AUTOINCREMENT` are indistinguishable, and
    /// the second is the one that means "the engine fills this in".
    /// </remarks>
    private static async Task<List<(string Name, string Ddl)>> TablesAsync(
        SqliteConnection db, CancellationToken cancellationToken)
    {
        var tables = new List<(string, string)>();

        await using var command = db.CreateCommand();
        command.CommandText =
            """
            SELECT name, COALESCE(sql, '') FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add((reader.GetString(0), reader.GetString(1)));
        }

        return tables;
    }

    private static async Task ReadColumnsAsync(
        SqliteConnection db,
        string table,
        string ddl,
        List<ColumnRow> columns,
        List<KeyRow> keys,
        CancellationToken cancellationToken)
    {
        var autoincrement = ddl.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase);
        var rows = new List<(string Name, string Type, bool NotNull, string? Default, int KeyPosition, int Position)>();

        await using (var command = db.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({Quote(table)})";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3) != 0,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    (int)reader.GetInt64(5),
                    (int)reader.GetInt64(0)));
            }
        }

        // The whole key has to be known before any column can claim to be the rowid: the alias rule
        // is about a primary key of **exactly one** INTEGER column, and reading row by row made the
        // first member of a composite key look like one.
        var keyWidth = rows.Count(r => r.KeyPosition > 0);

        foreach (var row in rows)
        {
            // A lone `INTEGER PRIMARY KEY` is an alias for the rowid, which SQLite assigns whether or
            // not `AUTOINCREMENT` was written. Both fill themselves in, so both are `increment`.
            var rowIdAlias = keyWidth == 1
                && row.KeyPosition == 1
                && row.Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase);

            columns.Add(new ColumnRow(
                Schema, table, row.Name, row.Type,
                NotNull: row.NotNull || row.KeyPosition > 0,
                Increment: rowIdAlias || (autoincrement && keyWidth == 1 && row.KeyPosition == 1),
                DefaultValue: Literal(row.Default),
                Position: row.Position));

            if (row.KeyPosition > 0)
            {
                keys.Add(new KeyRow(Schema, table, $"pk_{table}", row.Name, Primary: true, Position: row.KeyPosition));
            }
        }
    }

    private static async Task ReadForeignKeysAsync(
        SqliteConnection db, string table, List<ForeignKeyRow> rows, CancellationToken cancellationToken)
    {
        await using var command = db.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list({Quote(table)})";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // `to` is null when the key references the target's primary key implicitly. SQLite does
            // not resolve it for us; `rowid` is the honest stand-in, and the emitter reads it as the
            // key column it is.
            var target = reader.GetString(2);
            var toColumn = reader.IsDBNull(4) ? "rowid" : reader.GetString(4);

            rows.Add(new ForeignKeyRow(
                Constraint: $"fk_{table}_{reader.GetInt64(0).ToString(CultureInfo.InvariantCulture)}",
                Schema, table, reader.GetString(3),
                Schema, target, toColumn,
                OnDelete: reader.IsDBNull(6) ? null : reader.GetString(6),
                OnUpdate: reader.IsDBNull(5) ? null : reader.GetString(5),
                Position: (int)reader.GetInt64(1)));
        }
    }

    private static async Task ReadIndexesAsync(
        SqliteConnection db,
        string table,
        List<KeyRow> keys,
        List<IndexRow> indexes,
        CancellationToken cancellationToken)
    {
        var listed = new List<(string Name, bool Unique, string Origin)>();

        await using (var command = db.CreateCommand())
        {
            command.CommandText = $"PRAGMA index_list({Quote(table)})";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                listed.Add((reader.GetString(1), reader.GetInt64(2) != 0, reader.GetString(3)));
            }
        }

        foreach (var (name, unique, origin) in listed)
        {
            // `pk` is the index behind a table-level PRIMARY KEY, already read from `table_info`.
            if (origin == "pk") continue;

            await using var command = db.CreateCommand();
            command.CommandText = $"PRAGMA index_info({Quote(name)})";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // A null name is an expression index, which DBML cannot name a column for.
                if (reader.IsDBNull(2)) continue;
                var column = reader.GetString(2);
                var position = (int)reader.GetInt64(0) + 1;

                // `u` is the index behind a UNIQUE constraint. Reported as a key so a single-column
                // one becomes the column's own `unique` setting rather than an index block.
                if (origin == "u") keys.Add(new KeyRow(Schema, table, name, column, Primary: false, position));
                else indexes.Add(new IndexRow(Schema, table, name, column, unique, position));
            }
        }
    }

    /// <summary>A SQLite default, with the quoting it is reported in removed.</summary>
    private static string? Literal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim();
        return value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
            ? value[1..^1].Replace("''", "'", StringComparison.Ordinal)
            : value;
    }

    /// <summary>
    /// An identifier for a `PRAGMA`, which takes no parameters.
    /// </summary>
    /// <remarks>
    /// `PRAGMA table_info(?)` is not a thing SQLite accepts, so the name is interpolated — and
    /// therefore quoted here, doubling any embedded quote, exactly as a parameter would have. The
    /// names come from `sqlite_master` rather than from a user, but a table called `a"); DROP` is a
    /// legal SQLite table and this is the one place it would matter.
    /// </remarks>
    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
