using Microsoft.Data.SqlClient;

namespace CodeFlow.Dbml.Introspectors;

/// <summary>
/// Reads a SQL Server schema (<c>DBML-026</c>).
/// </summary>
/// <remarks>
/// <c>INFORMATION_SCHEMA</c> for columns, <c>sys</c> for everything whose **order** matters — the
/// standard views do not report a composite key's member positions reliably, and
/// <c>sys.index_columns.key_ordinal</c> does.
/// <para>
/// One SQL Server quirk shapes the connection: since Microsoft.Data.SqlClient 4.0 the default is
/// <c>Encrypt=true</c>, so a local server with a self-signed certificate refuses the handshake
/// unless the certificate is trusted. "Use TLS" off means exactly that — not encrypted — and on
/// means encrypted with the server's certificate validated.
/// </para>
/// </remarks>
internal sealed class SqlServerIntrospector : IDbmlIntrospector
{
    public string Driver => DbmlDrivers.SqlServer;

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
            SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME,
                   CASE
                     WHEN c.CHARACTER_MAXIMUM_LENGTH = -1 THEN c.DATA_TYPE + '(max)'
                     WHEN c.CHARACTER_MAXIMUM_LENGTH IS NOT NULL
                       THEN c.DATA_TYPE + '(' + CAST(c.CHARACTER_MAXIMUM_LENGTH AS varchar(12)) + ')'
                     WHEN c.DATA_TYPE IN ('decimal', 'numeric')
                       THEN c.DATA_TYPE + '(' + CAST(c.NUMERIC_PRECISION AS varchar(12)) + ','
                            + CAST(c.NUMERIC_SCALE AS varchar(12)) + ')'
                     ELSE c.DATA_TYPE
                   END AS type,
                   CASE WHEN c.IS_NULLABLE = 'NO' THEN 1 ELSE 0 END,
                   COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)),
                                  c.COLUMN_NAME, 'IsIdentity'),
                   c.COLUMN_DEFAULT,
                   c.ORDINAL_POSITION
            FROM INFORMATION_SCHEMA.COLUMNS c
            JOIN INFORMATION_SCHEMA.TABLES t
              ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            WHERE t.TABLE_TYPE = 'BASE TABLE'
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
            """,
            r => new ColumnRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetInt32(4) == 1,
                !r.IsDBNull(5) && r.GetInt32(5) == 1,
                Literal(r.TextOrNull(6)),
                r.GetInt32(7)),
            cancellationToken).ConfigureAwait(false);

        var keys = await Catalogue.QueryAsync(db,
            """
            SELECT s.name, t.name, i.name, c.name,
                   CAST(i.is_primary_key AS int), ic.key_ordinal
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE (i.is_primary_key = 1 OR i.is_unique_constraint = 1) AND ic.is_included_column = 0
            """,
            r => new KeyRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) == 1, r.GetByte(5)),
            cancellationToken).ConfigureAwait(false);

        var foreignKeys = await Catalogue.QueryAsync(db,
            """
            SELECT fk.name,
                   s.name, t.name, c.name,
                   rs.name, rt.name, rc.name,
                   fk.delete_referential_action_desc, fk.update_referential_action_desc,
                   fkc.constraint_column_id
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.tables t ON t.object_id = fkc.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
            JOIN sys.tables rt ON rt.object_id = fkc.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            """,
            r => new ForeignKeyRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetString(6),
                r.TextOrNull(7), r.TextOrNull(8), r.GetInt32(9)),
            cancellationToken).ConfigureAwait(false);

        var indexes = await Catalogue.QueryAsync(db,
            """
            SELECT s.name, t.name, i.name, c.name, CAST(i.is_unique AS int), ic.key_ordinal
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.is_primary_key = 0 AND i.is_unique_constraint = 0
              AND i.type <> 0 AND ic.is_included_column = 0 AND i.name IS NOT NULL
            """,
            r => new IndexRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) == 1, r.GetByte(5)),
            cancellationToken).ConfigureAwait(false);

        return DbmlSnapshotBuilder.Build(columns, keys, foreignKeys, indexes, []);
    }

    private static SqlConnection Open(DbmlConnection connection, string? password)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = connection.Port is { } port and > 0
                ? $"{connection.Host},{port}"
                : connection.Host ?? "localhost",
            InitialCatalog = connection.Database ?? string.Empty,
            Encrypt = connection.UseTls,
            // Only reachable with encryption off, where there is no certificate to trust anyway.
            TrustServerCertificate = !connection.UseTls,
            ConnectTimeout = Catalogue.TimeoutSeconds,
            Pooling = false,
        };

        // No username means Windows integrated authentication, which is how a developer reaches a
        // local instance and the one case where there is no password to hold.
        if (string.IsNullOrWhiteSpace(connection.Username))
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = connection.Username;
            builder.Password = password ?? string.Empty;
        }

        return new SqlConnection(builder.ToString());
    }

    /// <summary>A SQL Server default, which the catalogue wraps in one or two layers of parentheses.</summary>
    /// <remarks>
    /// It reports <c>((0))</c> for a numeric literal, <c>('pending')</c> for a string and
    /// <c>(getdate())</c> for a function — the outer parentheses are the catalogue's, not the user's.
    /// </remarks>
    private static string? Literal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim();
        while (value.Length >= 2 && value[0] == '(' && value[^1] == ')')
        {
            value = value[1..^1].Trim();
        }

        // `N'…'` is a Unicode literal, and it is how SSMS and every migration tool write a string
        // default for an `nvarchar` column. Left alone it reached the diagram as `N'pendiente'` and
        // was then quoted a second time as if the `N` were part of the value.
        if (value.Length >= 3 && (value[0] is 'N' or 'n') && value[1] == '\'' && value[^1] == '\'')
        {
            value = value[1..];
        }

        return value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
            ? value[1..^1].Replace("''", "'", StringComparison.Ordinal)
            : value.Length == 0 ? null : value;
    }
}
