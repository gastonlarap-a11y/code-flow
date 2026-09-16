using Npgsql;

namespace CodeFlow.Dbml.Introspectors;

/// <summary>
/// Reads a PostgreSQL schema (<c>DBML-026</c>).
/// </summary>
/// <remarks>
/// Columns come from <c>information_schema</c>, which spells types the way the standard does; keys,
/// relations and indexes come from <c>pg_catalog</c>, which is the only place their **member order**
/// survives. A composite foreign key read through <c>information_schema.constraint_column_usage</c>
/// comes back with its columns in an unspecified order, which pairs the wrong ones together and is
/// silent about it — <c>unnest(conkey, confkey) WITH ORDINALITY</c> is what keeps them aligned.
/// <para>
/// It is also the engine with real enumerated types, which is why <see cref="DbmlSnapshotEnum"/>
/// exists at all.
/// </para>
/// </remarks>
internal sealed class PostgresIntrospector : IDbmlIntrospector
{
    /// <summary>The namespaces that belong to the server rather than to the user.</summary>
    private const string UserSchemas = "n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%'";

    public string Driver => DbmlDrivers.Postgres;

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
            SELECT c.table_schema, c.table_name, c.column_name,
                   CASE
                     WHEN c.data_type = 'USER-DEFINED' THEN c.udt_name
                     WHEN c.character_maximum_length IS NOT NULL
                       THEN c.data_type || '(' || c.character_maximum_length || ')'
                     WHEN c.data_type IN ('numeric', 'decimal') AND c.numeric_precision IS NOT NULL
                       THEN c.data_type || '(' || c.numeric_precision || ',' || COALESCE(c.numeric_scale, 0) || ')'
                     ELSE c.data_type
                   END AS type,
                   c.is_nullable = 'NO' AS not_null,
                   c.is_identity = 'YES' OR c.column_default LIKE 'nextval(%' AS increment,
                   c.column_default,
                   c.ordinal_position
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE t.table_type = 'BASE TABLE'
              AND c.table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """,
            r => new ColumnRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetBoolean(4), r.GetBoolean(5), Literal(r.TextOrNull(6)), r.GetInt32(7)),
            cancellationToken).ConfigureAwait(false);

        var keys = await Catalogue.QueryAsync(db,
            $"""
             SELECT n.nspname, cl.relname, con.conname, att.attname, con.contype = 'p', ord.n::int
             FROM pg_constraint con
             JOIN pg_class cl ON cl.oid = con.conrelid
             JOIN pg_namespace n ON n.oid = cl.relnamespace
             JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS ord(attnum, n) ON TRUE
             JOIN pg_attribute att ON att.attrelid = cl.oid AND att.attnum = ord.attnum
             WHERE con.contype IN ('p', 'u') AND {UserSchemas}
             """,
            r => new KeyRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetInt32(5)),
            cancellationToken).ConfigureAwait(false);

        var foreignKeys = await Catalogue.QueryAsync(db,
            $"""
             SELECT con.conname,
                    n.nspname, cl.relname, att.attname,
                    fn.nspname, fcl.relname, fatt.attname,
                    con.confdeltype, con.confupdtype, ord.n::int
             FROM pg_constraint con
             JOIN pg_class cl ON cl.oid = con.conrelid
             JOIN pg_namespace n ON n.oid = cl.relnamespace
             JOIN pg_class fcl ON fcl.oid = con.confrelid
             JOIN pg_namespace fn ON fn.oid = fcl.relnamespace
             JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS ord(conkey, confkey, n) ON TRUE
             JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = ord.conkey
             JOIN pg_attribute fatt ON fatt.attrelid = con.confrelid AND fatt.attnum = ord.confkey
             WHERE con.contype = 'f' AND {UserSchemas}
             """,
            r => new ForeignKeyRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetString(6),
                RuleOf(r.GetChar(7)), RuleOf(r.GetChar(8)), r.GetInt32(9)),
            cancellationToken).ConfigureAwait(false);

        var indexes = await Catalogue.QueryAsync(db,
            $"""
             SELECT n.nspname, cl.relname, ic.relname, att.attname, idx.indisunique, ord.n::int
             FROM pg_index idx
             JOIN pg_class cl ON cl.oid = idx.indrelid
             JOIN pg_class ic ON ic.oid = idx.indexrelid
             JOIN pg_namespace n ON n.oid = cl.relnamespace
             JOIN LATERAL unnest(idx.indkey) WITH ORDINALITY AS ord(attnum, n) ON TRUE
             JOIN pg_attribute att ON att.attrelid = cl.oid AND att.attnum = ord.attnum
             WHERE NOT idx.indisprimary AND {UserSchemas}
             """,
            r => new IndexRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetInt32(5)),
            cancellationToken).ConfigureAwait(false);

        var enumRows = await Catalogue.QueryAsync(db,
            $"""
             SELECT n.nspname, t.typname, e.enumlabel
             FROM pg_type t
             JOIN pg_enum e ON e.enumtypid = t.oid
             JOIN pg_namespace n ON n.oid = t.typnamespace
             WHERE {UserSchemas}
             ORDER BY n.nspname, t.typname, e.enumsortorder
             """,
            r => (Schema: r.GetString(0), Name: r.GetString(1), Value: r.GetString(2)),
            cancellationToken).ConfigureAwait(false);

        var enums = enumRows
            .GroupBy(row => (row.Schema, row.Name))
            .Select(g => new DbmlSnapshotEnumeration(g.Key.Schema, g.Key.Name, g.Select(v => v.Value).ToList()))
            .ToList();

        return DbmlSnapshotBuilder.Build(columns, keys, foreignKeys, indexes, enums);
    }

    private static NpgsqlConnection Open(DbmlConnection connection, string? password) =>
        new(new NpgsqlConnectionStringBuilder
        {
            Host = connection.Host ?? "localhost",
            Port = connection.Port ?? DbmlDrivers.DefaultPort(DbmlDrivers.Postgres),
            Database = connection.Database,
            Username = connection.Username,
            Password = password,
            SslMode = connection.UseTls ? SslMode.Require : SslMode.Prefer,
            Timeout = Catalogue.TimeoutSeconds,
            // Nothing here writes, and a pool would keep the socket to a server the user is only
            // reading once.
            Pooling = false,
        }.ToString());

    /// <summary>`pg_constraint`'s one-character referential actions.</summary>
    private static string? RuleOf(char code) => code switch
    {
        'c' => "cascade",
        'r' => "restrict",
        'n' => "set null",
        'd' => "set default",
        _ => null,
    };

    /// <summary>
    /// A PostgreSQL default, with the cast and quoting the catalogue reports it in removed.
    /// </summary>
    /// <remarks>
    /// It stores them as rendered expressions: <c>'pending'::character varying</c>,
    /// <c>now()</c>, <c>nextval('t_id_seq'::regclass)</c>. The cast is noise in a diagram, and a
    /// sequence default is already said by `increment`.
    /// </remarks>
    private static string? Literal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.StartsWith("nextval(", StringComparison.OrdinalIgnoreCase)) return null;

        var value = raw;
        var cast = value.LastIndexOf("::", StringComparison.Ordinal);
        if (cast > 0) value = value[..cast];

        value = value.Trim();
        return value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
            ? value[1..^1].Replace("''", "'", StringComparison.Ordinal)
            : value;
    }
}
