using CodeFlow.Dbml;
using CodeFlow.Dbml.Introspectors;
using CodeFlow.Tests.Files;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeFlow.Tests.Dbml;

/// <summary>
/// Reading a real SQLite file (DBML-026).
/// </summary>
/// <remarks>
/// The one engine that can be tested end to end without a server, so it is: a real file, real
/// `PRAGMA` calls, real results. What it proves beyond itself is that
/// <see cref="DbmlSnapshotBuilder"/> is fed the rows it expects — the other three introspectors
/// differ only in the SQL that produces them.
/// </remarks>
public sealed class SqliteIntrospectorTests
{
    private static async Task<DbmlSchemaSnapshot> ReadAsync(TempDirectory folder, string ddl)
    {
        var path = Path.Combine(folder.Path, "test.db");
        await using (var db = new SqliteConnection($"Data Source={path}"))
        {
            await db.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = db.CreateCommand();
            command.CommandText = ddl;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var connection = new DbmlConnection(
            "c1", "test", DbmlDrivers.Sqlite, null, null, null, null, path, false, "now", "now");

        return await new SqliteIntrospector().ReadAsync(connection, null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reads_tables_columns_types_and_nullability()
    {
        using var folder = new TempDirectory();

        var snapshot = await ReadAsync(folder,
            """
            CREATE TABLE usuarios (
              id INTEGER PRIMARY KEY,
              email TEXT NOT NULL UNIQUE,
              apodo TEXT
            );
            """);

        var table = Assert.Single(snapshot.Tables);
        Assert.Equal("usuarios", table.Name);
        // No schemas in SQLite, so everything lands in the namespace `@dbml/core` calls "none".
        Assert.Equal("public", table.Schema);

        Assert.Equal(["id", "email", "apodo"], table.Columns.Select(c => c.Name));
        Assert.Equal(["INTEGER", "TEXT", "TEXT"], table.Columns.Select(c => c.Type));
        Assert.Equal([true, true, false], table.Columns.Select(c => c.NotNull));
    }

    [Fact]
    public async Task An_integer_primary_key_is_an_increment_because_sqlite_fills_it_in()
    {
        // The rowid alias assigns itself whether or not AUTOINCREMENT was written.
        using var folder = new TempDirectory();

        var snapshot = await ReadAsync(folder, "CREATE TABLE a (id INTEGER PRIMARY KEY, nombre TEXT);");

        Assert.True(snapshot.Tables[0].Columns[0].Pk);
        Assert.True(snapshot.Tables[0].Columns[0].Increment);
        Assert.False(snapshot.Tables[0].Columns[1].Increment);
    }

    [Fact]
    public async Task A_text_primary_key_is_not_an_increment()
    {
        using var folder = new TempDirectory();

        var snapshot = await ReadAsync(folder, "CREATE TABLE a (codigo TEXT PRIMARY KEY);");

        Assert.True(snapshot.Tables[0].Columns[0].Pk);
        Assert.False(snapshot.Tables[0].Columns[0].Increment);
    }

    [Fact]
    public async Task A_single_column_unique_constraint_marks_the_column_rather_than_making_an_index()
    {
        using var folder = new TempDirectory();

        var snapshot = await ReadAsync(folder, "CREATE TABLE a (id INTEGER PRIMARY KEY, email TEXT UNIQUE);");

        Assert.True(snapshot.Tables[0].Columns[1].Unique);
        Assert.Empty(snapshot.Tables[0].Indexes);
    }

    [Fact]
    public async Task A_composite_primary_key_becomes_a_pk_index_in_declared_order()
    {
        using var folder = new TempDirectory();

        var snapshot = await ReadAsync(folder,
            """
            CREATE TABLE animal_vacunas (
              animal_id INTEGER NOT NULL,
              vacuna_id INTEGER NOT NULL,
              PRIMARY KEY (animal_id, vacuna_id)
            );
            """);

        var index = Assert.Single(snapshot.Tables[0].Indexes);
        Assert.True(index.Pk);
        Assert.Equal(["animal_id", "vacuna_id"], index.Columns);
    }

    [Fact]
    public async Task No_member_of_a_composite_key_is_an_increment()
    {
        // The rowid alias is a primary key of *exactly one* INTEGER column. Read row by row, the
        // first member of a composite key looked like one — found by importing a real database and
        // reading `pedido_id INTEGER [pk, increment]` in a join table.
        using var folder = new TempDirectory();

        var snapshot = await ReadAsync(folder,
            """
            CREATE TABLE lineas (
              pedido_id INTEGER NOT NULL,
              producto TEXT NOT NULL,
              PRIMARY KEY (pedido_id, producto)
            );
            """);

        Assert.All(snapshot.Tables[0].Columns, column => Assert.False(column.Increment));
        Assert.All(snapshot.Tables[0].Columns, column => Assert.True(column.Pk));
    }

    [Fact]
    public async Task Reads_a_foreign_key_with_its_referential_action()
    {
        using var folder = new TempDirectory();

        var snapshot = await ReadAsync(folder,
            """
            CREATE TABLE usuarios (id INTEGER PRIMARY KEY);
            CREATE TABLE animales (
              id INTEGER PRIMARY KEY,
              usuario_id INTEGER NOT NULL REFERENCES usuarios(id) ON DELETE CASCADE
            );
            """);

        var relation = Assert.Single(snapshot.Refs);
        Assert.Equal("animales", relation.FromTable);
        Assert.Equal(["usuario_id"], relation.FromColumns);
        Assert.Equal("usuarios", relation.ToTable);
        Assert.Equal(["id"], relation.ToColumns);
        Assert.Equal("cascade", relation.OnDelete);
    }

    [Fact]
    public async Task A_named_index_comes_back_with_its_name_and_columns()
    {
        using var folder = new TempDirectory();

        var snapshot = await ReadAsync(folder,
            """
            CREATE TABLE a (id INTEGER PRIMARY KEY, x INTEGER, y INTEGER);
            CREATE INDEX ix_a_xy ON a (x, y);
            """);

        var index = Assert.Single(snapshot.Tables[0].Indexes);
        Assert.Equal("ix_a_xy", index.Name);
        Assert.Equal(["x", "y"], index.Columns);
        Assert.False(index.Unique);
    }

    [Fact]
    public async Task Defaults_lose_the_quoting_sqlite_reports_them_in()
    {
        using var folder = new TempDirectory();

        var snapshot = await ReadAsync(folder,
            "CREATE TABLE a (id INTEGER PRIMARY KEY, estado TEXT DEFAULT 'activo', cuota INTEGER DEFAULT 10);");

        Assert.Equal("activo", snapshot.Tables[0].Columns[1].DefaultValue);
        Assert.Equal("10", snapshot.Tables[0].Columns[2].DefaultValue);
    }

    [Fact]
    public async Task Sqlites_own_tables_are_not_part_of_the_users_schema()
    {
        using var folder = new TempDirectory();

        var snapshot = await ReadAsync(folder,
            "CREATE TABLE a (id INTEGER PRIMARY KEY AUTOINCREMENT);");

        // AUTOINCREMENT creates `sqlite_sequence`, which belongs to the engine.
        Assert.Equal(["a"], snapshot.Tables.Select(t => t.Name));
    }

    [Fact]
    public async Task A_missing_file_is_refused_rather_than_created()
    {
        // `SqliteOpenMode.ReadOnly` is the difference between saying so and silently leaving an
        // empty database behind at whatever path was typed.
        using var folder = new TempDirectory();
        var missing = Path.Combine(folder.Path, "absent.db");

        var connection = new DbmlConnection(
            "c1", "test", DbmlDrivers.Sqlite, null, null, null, null, missing, false, "now", "now");

        var error = await Assert.ThrowsAsync<DbmlConnectionException>(async () =>
            await new SqliteIntrospector().ReadAsync(connection, null, TestContext.Current.CancellationToken));

        Assert.StartsWith(DbmlConnectionException.Marker, error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task A_connection_with_no_file_says_so_instead_of_opening_something()
    {
        var connection = new DbmlConnection(
            "c1", "test", DbmlDrivers.Sqlite, null, null, null, null, null, false, "now", "now");

        var error = await Assert.ThrowsAsync<DbmlConnectionException>(async () =>
            await new SqliteIntrospector().ReadAsync(connection, null, TestContext.Current.CancellationToken));

        Assert.Contains("no database file", error.Message, StringComparison.Ordinal);
    }
}
