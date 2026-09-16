namespace CodeFlow.Dbml;

/// <summary>The flat rows an engine's catalogue queries return, before they are a schema.</summary>
/// <remarks>
/// Every engine answers the same five questions in its own dialect, and every engine answers them
/// as rows: one per column, one per key member, one per foreign-key member, one per index member.
/// Assembling those into a <see cref="DbmlSchemaSnapshot"/> is identical work, so it is done once
/// here — which is also what makes it testable, since a synthetic row set proves the assembly for
/// all four engines without a server to connect to (<c>DBML-025</c>).
/// </remarks>
/// <param name="Position">Ordinal within its table, key or index. Composite keys depend on it.</param>
internal readonly record struct ColumnRow(
    string Schema,
    string Table,
    string Name,
    string Type,
    bool NotNull,
    bool Increment,
    string? DefaultValue,
    int Position);

/// <param name="Primary">Whether this constraint is the primary key rather than a unique one.</param>
internal readonly record struct KeyRow(
    string Schema,
    string Table,
    string Constraint,
    string Column,
    bool Primary,
    int Position);

internal readonly record struct ForeignKeyRow(
    string Constraint,
    string FromSchema,
    string FromTable,
    string FromColumn,
    string ToSchema,
    string ToTable,
    string ToColumn,
    string? OnDelete,
    string? OnUpdate,
    int Position);

internal readonly record struct IndexRow(
    string Schema,
    string Table,
    string Name,
    string Column,
    bool Unique,
    int Position);

/// <summary>Turns an engine's catalogue rows into the schema the renderer draws.</summary>
internal static class DbmlSnapshotBuilder
{
    public static DbmlSchemaSnapshot Build(
        IEnumerable<ColumnRow> columns,
        IEnumerable<KeyRow> keys,
        IEnumerable<ForeignKeyRow> foreignKeys,
        IEnumerable<IndexRow> indexes,
        IEnumerable<DbmlSnapshotEnumeration> enums)
    {
        var keyList = keys.ToList();
        var indexList = indexes.ToList();

        // A column is `pk` when a primary-key constraint names it, and `unique` only when a
        // single-column unique constraint does: a member of a two-column unique key is not unique on
        // its own, and marking it so would state something false about the data.
        var primaryColumns = new HashSet<(string, string, string)>();
        var uniqueColumns = new HashSet<(string, string, string)>();
        foreach (var group in keyList.GroupBy(k => (k.Schema, k.Table, k.Constraint)))
        {
            var members = group.OrderBy(k => k.Position).ToList();
            foreach (var member in members)
            {
                var key = (member.Schema, member.Table, member.Column);
                if (member.Primary) primaryColumns.Add(key);
                else if (members.Count == 1) uniqueColumns.Add(key);
            }
        }

        var tables = new List<DbmlSnapshotTable>();
        foreach (var group in columns.GroupBy(c => (c.Schema, c.Table)))
        {
            var (schema, table) = group.Key;

            var built = group
                .OrderBy(c => c.Position)
                .Select(c => new DbmlSnapshotColumn(
                    c.Name,
                    c.Type,
                    Pk: primaryColumns.Contains((schema, table, c.Name)),
                    NotNull: c.NotNull,
                    Unique: uniqueColumns.Contains((schema, table, c.Name)),
                    Increment: c.Increment,
                    DefaultValue: c.DefaultValue))
                .ToList();

            tables.Add(new DbmlSnapshotTable(schema, table, built, IndexesOf(schema, table, keyList, indexList)));
        }

        return new DbmlSchemaSnapshot(
            tables.OrderBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToList(),
            Relations(foreignKeys),
            enums.OrderBy(e => e.Schema, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// The indexes worth drawing: composite keys, and whatever the engine reported as an index.
    /// </summary>
    /// <remarks>
    /// A single-column key is left out on purpose — it is already said by the column's own `pk` or
    /// `unique` setting, and repeating it as an index block is noise in every table. A **composite**
    /// key has nowhere else to live, so it becomes an index, which is also the only way DBML can
    /// express one (`DBML-015` documents the same asymmetry from the export side).
    /// </remarks>
    private static List<DbmlSnapshotIndex> IndexesOf(
        string schema,
        string table,
        IEnumerable<KeyRow> keys,
        IEnumerable<IndexRow> indexes)
    {
        var built = new List<DbmlSnapshotIndex>();

        foreach (var group in keys.Where(k => k.Schema == schema && k.Table == table)
                     .GroupBy(k => k.Constraint))
        {
            var members = group.OrderBy(k => k.Position).ToList();
            if (members.Count < 2) continue;

            built.Add(new DbmlSnapshotIndex(
                Name: members[0].Primary ? null : group.Key,
                Columns: members.Select(m => m.Column).ToList(),
                Unique: !members[0].Primary,
                Pk: members[0].Primary));
        }

        foreach (var group in indexes.Where(i => i.Schema == schema && i.Table == table)
                     .GroupBy(i => i.Name))
        {
            var members = group.OrderBy(i => i.Position).ToList();
            var columns = members.Select(m => m.Column).ToList();

            // The engines report the index backing a constraint alongside the constraint itself.
            // Emitting both would draw the same thing twice under two names.
            if (built.Any(existing => existing.Columns.SequenceEqual(columns))) continue;

            built.Add(new DbmlSnapshotIndex(group.Key, columns, members[0].Unique, Pk: false));
        }

        return built;
    }

    /// <summary>One relation per foreign-key constraint, its members in declared order.</summary>
    private static List<DbmlSnapshotRef> Relations(IEnumerable<ForeignKeyRow> rows)
    {
        var relations = new List<DbmlSnapshotRef>();

        foreach (var group in rows.GroupBy(r => (r.Constraint, r.FromSchema, r.FromTable)))
        {
            var members = group.OrderBy(r => r.Position).ToList();
            var first = members[0];

            relations.Add(new DbmlSnapshotRef(
                first.FromSchema,
                first.FromTable,
                members.Select(m => m.FromColumn).ToList(),
                first.ToSchema,
                first.ToTable,
                members.Select(m => m.ToColumn).ToList(),
                Action(first.OnDelete),
                Action(first.OnUpdate)));
        }

        return relations
            .OrderBy(r => r.FromSchema, StringComparer.Ordinal)
            .ThenBy(r => r.FromTable, StringComparer.Ordinal)
            .ThenBy(r => string.Join(",", r.FromColumns), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A referential action as DBML writes it, or null for the one that means "do nothing".
    /// </summary>
    /// <remarks>
    /// The engines spell these four ways — <c>CASCADE</c>, <c>Cascade</c>, <c>cascade</c>,
    /// <c>SET NULL</c>, <c>SET_NULL</c> — and `NO ACTION` is the default every one of them reports
    /// for a key that declares nothing. Writing it out would put `[delete: no action]` on most of
    /// the relations in a schema, saying nothing.
    /// </remarks>
    private static string? Action(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var normalised = raw.Trim().Replace('_', ' ').ToLowerInvariant();
        return normalised switch
        {
            "cascade" => "cascade",
            "restrict" => "restrict",
            "set null" => "set null",
            "set default" => "set default",
            _ => null,
        };
    }
}
