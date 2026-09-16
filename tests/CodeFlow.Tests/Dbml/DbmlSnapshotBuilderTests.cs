using CodeFlow.Dbml;
using Xunit;

namespace CodeFlow.Tests.Dbml;

/// <summary>
/// Assembling an engine's catalogue rows into a schema (DBML-025).
/// </summary>
/// <remarks>
/// Synthetic rows on purpose. Every engine answers the same five questions in its own dialect and
/// this is what happens to the answers — so testing it here proves the assembly for all four at
/// once, without a PostgreSQL, a SQL Server and a MySQL to connect to.
/// </remarks>
public sealed class DbmlSnapshotBuilderTests
{
    private static ColumnRow Column(string table, string name, int position, bool notNull = false, bool increment = false) =>
        new("public", table, name, "integer", notNull, increment, null, position);

    [Fact]
    public void A_column_named_by_a_primary_key_constraint_is_marked_pk()
    {
        var snapshot = DbmlSnapshotBuilder.Build(
            [Column("usuarios", "id", 1), Column("usuarios", "email", 2)],
            [new KeyRow("public", "usuarios", "pk_usuarios", "id", Primary: true, 1)],
            [], [], []);

        Assert.True(snapshot.Tables[0].Columns[0].Pk);
        Assert.False(snapshot.Tables[0].Columns[1].Pk);
    }

    [Fact]
    public void Only_a_single_column_unique_constraint_marks_its_column_unique()
    {
        // A member of a two-column unique key is not unique on its own, and saying so would state
        // something false about the data.
        var snapshot = DbmlSnapshotBuilder.Build(
            [Column("t", "a", 1), Column("t", "b", 2), Column("t", "c", 3)],
            [
                new KeyRow("public", "t", "uq_one", "a", Primary: false, 1),
                new KeyRow("public", "t", "uq_pair", "b", Primary: false, 1),
                new KeyRow("public", "t", "uq_pair", "c", Primary: false, 2),
            ],
            [], [], []);

        var columns = snapshot.Tables[0].Columns;
        Assert.True(columns[0].Unique);
        Assert.False(columns[1].Unique);
        Assert.False(columns[2].Unique);
    }

    [Fact]
    public void A_single_column_key_becomes_no_index_because_the_column_already_says_it()
    {
        var snapshot = DbmlSnapshotBuilder.Build(
            [Column("t", "id", 1)],
            [new KeyRow("public", "t", "pk_t", "id", Primary: true, 1)],
            [], [], []);

        Assert.Empty(snapshot.Tables[0].Indexes);
    }

    [Fact]
    public void A_composite_key_becomes_an_index_because_it_has_nowhere_else_to_live()
    {
        var snapshot = DbmlSnapshotBuilder.Build(
            [Column("puente", "a_id", 1), Column("puente", "b_id", 2)],
            [
                new KeyRow("public", "puente", "pk_puente", "a_id", Primary: true, 1),
                new KeyRow("public", "puente", "pk_puente", "b_id", Primary: true, 2),
            ],
            [], [], []);

        var index = Assert.Single(snapshot.Tables[0].Indexes);
        Assert.True(index.Pk);
        Assert.Equal(["a_id", "b_id"], index.Columns);
        // A primary key has one name in every dialect and none worth showing.
        Assert.Null(index.Name);
    }

    [Fact]
    public void Key_and_index_members_are_ordered_by_position_not_by_arrival()
    {
        // The catalogues return them unordered, and a composite key with its columns swapped is a
        // different key.
        var snapshot = DbmlSnapshotBuilder.Build(
            [Column("t", "a", 1), Column("t", "b", 2)],
            [
                new KeyRow("public", "t", "pk_t", "b", Primary: true, 2),
                new KeyRow("public", "t", "pk_t", "a", Primary: true, 1),
            ],
            [], [], []);

        Assert.Equal(["a", "b"], snapshot.Tables[0].Indexes[0].Columns);
    }

    [Fact]
    public void The_index_backing_a_constraint_is_not_reported_twice()
    {
        // Every engine lists it in both places; emitting both draws the same thing under two names.
        var snapshot = DbmlSnapshotBuilder.Build(
            [Column("t", "a", 1), Column("t", "b", 2)],
            [
                new KeyRow("public", "t", "pk_t", "a", Primary: true, 1),
                new KeyRow("public", "t", "pk_t", "b", Primary: true, 2),
            ],
            [],
            [
                new IndexRow("public", "t", "pk_t", "a", Unique: true, 1),
                new IndexRow("public", "t", "pk_t", "b", Unique: true, 2),
            ],
            []);

        Assert.Single(snapshot.Tables[0].Indexes);
    }

    [Fact]
    public void A_composite_foreign_key_keeps_its_columns_paired()
    {
        // Read through a view that does not preserve member order, the pairs cross silently.
        var snapshot = DbmlSnapshotBuilder.Build(
            [Column("hijo", "a", 1), Column("hijo", "b", 2)],
            [],
            [
                new ForeignKeyRow("fk", "public", "hijo", "b", "public", "padre", "y", null, null, 2),
                new ForeignKeyRow("fk", "public", "hijo", "a", "public", "padre", "x", null, null, 1),
            ],
            [], []);

        var relation = Assert.Single(snapshot.Refs);
        Assert.Equal(["a", "b"], relation.FromColumns);
        Assert.Equal(["x", "y"], relation.ToColumns);
    }

    [Theory]
    [InlineData("CASCADE", "cascade")]
    [InlineData("Cascade", "cascade")]
    [InlineData("SET_NULL", "set null")]
    [InlineData("SET NULL", "set null")]
    [InlineData("RESTRICT", "restrict")]
    [InlineData("SET DEFAULT", "set default")]
    public void Referential_actions_are_normalised_across_the_four_spellings(string raw, string expected)
    {
        var snapshot = DbmlSnapshotBuilder.Build(
            [Column("hijo", "a", 1)],
            [],
            [new ForeignKeyRow("fk", "public", "hijo", "a", "public", "padre", "id", raw, null, 1)],
            [], []);

        Assert.Equal(expected, snapshot.Refs[0].OnDelete);
    }

    [Theory]
    [InlineData("NO_ACTION")]
    [InlineData("NO ACTION")]
    [InlineData("")]
    [InlineData(null)]
    public void No_action_is_dropped_because_it_is_what_every_key_that_says_nothing_reports(string? raw)
    {
        var snapshot = DbmlSnapshotBuilder.Build(
            [Column("hijo", "a", 1)],
            [],
            [new ForeignKeyRow("fk", "public", "hijo", "a", "public", "padre", "id", raw, raw, 1)],
            [], []);

        Assert.Null(snapshot.Refs[0].OnDelete);
        Assert.Null(snapshot.Refs[0].OnUpdate);
    }

    [Fact]
    public void Tables_and_relations_come_back_in_a_stable_order()
    {
        // The catalogues do not promise one, and an import that reshuffles itself between runs
        // produces a diff on every read.
        var snapshot = DbmlSnapshotBuilder.Build(
            [Column("zeta", "id", 1), Column("alpha", "id", 1)],
            [], [], [], []);

        Assert.Equal(["alpha", "zeta"], snapshot.Tables.Select(t => t.Name));
    }

    [Fact]
    public void Each_schema_keeps_its_own_tables_of_the_same_name()
    {
        var snapshot = DbmlSnapshotBuilder.Build(
            [
                new ColumnRow("ventas", "pedidos", "id", "integer", true, false, null, 1),
                new ColumnRow("compras", "pedidos", "id", "integer", true, false, null, 1),
            ],
            [], [], [], []);

        Assert.Equal(2, snapshot.Tables.Count);
        Assert.Equal(["compras", "ventas"], snapshot.Tables.Select(t => t.Schema));
    }
}
