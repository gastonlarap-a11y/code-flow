using System.Data.Common;
using CodeFlow.Dbml;
using CodeFlow.Dbml.Introspectors;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace CodeFlow.Tests.Dbml;

/// <summary>
/// The three server introspectors against real servers (DBML-026).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DbmlSnapshotBuilderTests"/> proves the assembly for all four engines with synthetic
/// rows; what it cannot prove is that each engine's catalogue SQL returns those rows. Only a server
/// can, so these run against one — and are skipped, saying why, when none is configured. Nothing in
/// a normal `dotnet test` needs a database.
/// </para>
/// <para>
/// Each test creates a throwaway database, builds the same schema in that engine's dialect, reads
/// it, and drops it. To run them, start the servers and point the variables at them, e.g.:
/// </para>
/// <code>
/// docker run -d --name cf-pg -e POSTGRES_PASSWORD=Codeflow_Pass1 -p 55432:5432 postgres:17
/// docker run -d --name cf-my -e MYSQL_ROOT_PASSWORD=Codeflow_Pass1 -p 53306:3306 mysql:8.4
/// docker run -d --name cf-ms --platform linux/amd64 -e ACCEPT_EULA=Y \
///   -e MSSQL_SA_PASSWORD=Codeflow_Pass1 -p 51433:1433 mcr.microsoft.com/mssql/server:2022-latest
///
/// CODEFLOW_TEST_POSTGRES=localhost:55432:postgres:Codeflow_Pass1 \
/// CODEFLOW_TEST_MYSQL=localhost:53306:root:Codeflow_Pass1 \
/// CODEFLOW_TEST_SQLSERVER=localhost:51433:sa:Codeflow_Pass1 \
///   dotnet test --filter "FullyQualifiedName~ServerIntrospector"
/// </code>
/// <para>
/// The schema is chosen for the places these catalogues go wrong silently: a composite primary key
/// (whose first member must not read as an increment), a composite foreign key whose child columns
/// are named differently from the parent's (so a mispaired read is visible, not coincidentally
/// right), a named index, cascading actions, and literal defaults in each engine's own wrapping.
/// </para>
/// </remarks>
public sealed class ServerIntrospectorTests
{
    [Fact]
    public async Task Postgres_reads_the_schema_it_was_given()
    {
        var server = Server("CODEFLOW_TEST_POSTGRES");
        var database = ThrowawayName();
        var ct = TestContext.Current.CancellationToken;

        await using (var admin = new NpgsqlConnection(PostgresString(server, "postgres")))
        {
            await admin.OpenAsync(ct);
            await ExecAsync(admin, ct, $"CREATE DATABASE {database}");
        }

        try
        {
            await using (var db = new NpgsqlConnection(PostgresString(server, database)))
            {
                await db.OpenAsync(ct);
                await ExecAsync(db, ct,
                    "CREATE TYPE estado_pedido AS ENUM ('pendiente', 'pagado')",
                    "CREATE TABLE clientes (id serial PRIMARY KEY, email varchar(255) NOT NULL UNIQUE, nombre varchar(120) NOT NULL)",
                    """
                    CREATE TABLE pedidos (
                      id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                      cliente_id integer NOT NULL REFERENCES clientes(id) ON DELETE CASCADE,
                      total numeric(10,2) NOT NULL DEFAULT 0,
                      estado estado_pedido NOT NULL DEFAULT 'pendiente')
                    """,
                    "CREATE INDEX ix_pedidos_cliente ON pedidos (cliente_id)",
                    """
                    CREATE TABLE lineas (
                      pedido_id integer NOT NULL REFERENCES pedidos(id) ON DELETE CASCADE,
                      producto varchar(80) NOT NULL,
                      cantidad integer NOT NULL,
                      PRIMARY KEY (pedido_id, producto))
                    """,
                    """
                    CREATE TABLE notas_linea (
                      id serial PRIMARY KEY,
                      n_pedido integer NOT NULL,
                      n_producto varchar(80) NOT NULL,
                      FOREIGN KEY (n_pedido, n_producto) REFERENCES lineas (pedido_id, producto))
                    """);
            }

            var snapshot = await new PostgresIntrospector().ReadAsync(
                Connection(DbmlDrivers.Postgres, server, database), server.Password, ct);

            AssertCommonShape(snapshot, schema: "public");

            // The one engine with real enumerated types.
            var estado = Assert.Single(snapshot.Enums);
            Assert.Equal("estado_pedido", estado.Name);
            Assert.Equal(["pendiente", "pagado"], estado.Values);
            Assert.Equal("estado_pedido", Column(snapshot, "pedidos", "estado").Type);

            // `serial` reads through `nextval(…)`, `GENERATED … AS IDENTITY` through `is_identity`.
            Assert.True(Column(snapshot, "clientes", "id").Increment);
            Assert.True(Column(snapshot, "pedidos", "id").Increment);
            // The `nextval(…)` default is dropped: `increment` already says it.
            Assert.Null(Column(snapshot, "clientes", "id").DefaultValue);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(PostgresString(server, "postgres"));
            await admin.OpenAsync(ct);
            await ExecAsync(admin, ct, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
    }

    [Fact]
    public async Task MySql_reads_the_schema_it_was_given()
    {
        var server = Server("CODEFLOW_TEST_MYSQL");
        var database = ThrowawayName();
        var ct = TestContext.Current.CancellationToken;

        await using (var admin = new MySqlConnection(MySqlString(server, null)))
        {
            await admin.OpenAsync(ct);
            await ExecAsync(admin, ct, $"CREATE DATABASE {database}");
        }

        try
        {
            await using (var db = new MySqlConnection(MySqlString(server, database)))
            {
                await db.OpenAsync(ct);
                await ExecAsync(db, ct,
                    "CREATE TABLE clientes (id int AUTO_INCREMENT PRIMARY KEY, email varchar(255) NOT NULL UNIQUE, nombre varchar(120) NOT NULL)",
                    """
                    CREATE TABLE pedidos (
                      id int AUTO_INCREMENT PRIMARY KEY,
                      cliente_id int NOT NULL,
                      total decimal(10,2) NOT NULL DEFAULT 0,
                      estado varchar(20) NOT NULL DEFAULT 'pendiente',
                      INDEX ix_pedidos_cliente (cliente_id),
                      CONSTRAINT fk_pedidos_cliente FOREIGN KEY (cliente_id) REFERENCES clientes(id) ON DELETE CASCADE)
                    """,
                    """
                    CREATE TABLE lineas (
                      pedido_id int NOT NULL,
                      producto varchar(80) NOT NULL,
                      cantidad int NOT NULL,
                      PRIMARY KEY (pedido_id, producto),
                      CONSTRAINT fk_lineas_pedido FOREIGN KEY (pedido_id) REFERENCES pedidos(id) ON DELETE CASCADE)
                    """,
                    """
                    CREATE TABLE notas_linea (
                      id int AUTO_INCREMENT PRIMARY KEY,
                      n_pedido int NOT NULL,
                      n_producto varchar(80) NOT NULL,
                      CONSTRAINT fk_notas FOREIGN KEY (n_pedido, n_producto) REFERENCES lineas (pedido_id, producto))
                    """);
            }

            var snapshot = await new MySqlIntrospector().ReadAsync(
                Connection(DbmlDrivers.MySql, server, database), server.Password, ct);

            // MySQL's "schema" is the database itself.
            AssertCommonShape(snapshot, schema: database);
            Assert.True(Column(snapshot, "clientes", "id").Increment);
            Assert.Empty(snapshot.Enums);
        }
        finally
        {
            await using var admin = new MySqlConnection(MySqlString(server, null));
            await admin.OpenAsync(ct);
            await ExecAsync(admin, ct, $"DROP DATABASE IF EXISTS {database}");
        }
    }

    [Fact]
    public async Task SqlServer_reads_the_schema_it_was_given()
    {
        var server = Server("CODEFLOW_TEST_SQLSERVER");
        var database = ThrowawayName();
        var ct = TestContext.Current.CancellationToken;

        await using (var admin = new SqlConnection(SqlServerString(server, "master")))
        {
            await admin.OpenAsync(ct);
            await ExecAsync(admin, ct, $"CREATE DATABASE {database}");
        }

        try
        {
            await using (var db = new SqlConnection(SqlServerString(server, database)))
            {
                await db.OpenAsync(ct);
                await ExecAsync(db, ct,
                    "CREATE TABLE clientes (id int IDENTITY(1,1) PRIMARY KEY, email nvarchar(255) NOT NULL UNIQUE, nombre nvarchar(120) NOT NULL)",
                    // `N'…'` on purpose: it is how SQL Server tooling writes a string default, and the
                    // catalogue stores it as `(N'pendiente')`.
                    """
                    CREATE TABLE pedidos (
                      id int IDENTITY(1,1) PRIMARY KEY,
                      cliente_id int NOT NULL,
                      total decimal(10,2) NOT NULL DEFAULT 0,
                      estado nvarchar(20) NOT NULL DEFAULT N'pendiente',
                      comentario nvarchar(max) NULL,
                      CONSTRAINT fk_pedidos_cliente FOREIGN KEY (cliente_id) REFERENCES clientes(id) ON DELETE CASCADE)
                    """,
                    "CREATE INDEX ix_pedidos_cliente ON pedidos (cliente_id)",
                    """
                    CREATE TABLE lineas (
                      pedido_id int NOT NULL,
                      producto nvarchar(80) NOT NULL,
                      cantidad int NOT NULL,
                      CONSTRAINT pk_lineas PRIMARY KEY (pedido_id, producto),
                      CONSTRAINT fk_lineas_pedido FOREIGN KEY (pedido_id) REFERENCES pedidos(id) ON DELETE CASCADE)
                    """,
                    """
                    CREATE TABLE notas_linea (
                      id int IDENTITY(1,1) PRIMARY KEY,
                      n_pedido int NOT NULL,
                      n_producto nvarchar(80) NOT NULL,
                      CONSTRAINT fk_notas FOREIGN KEY (n_pedido, n_producto) REFERENCES lineas (pedido_id, producto))
                    """);
            }

            var snapshot = await new SqlServerIntrospector().ReadAsync(
                Connection(DbmlDrivers.SqlServer, server, database), server.Password, ct);

            AssertCommonShape(snapshot, schema: "dbo");
            Assert.True(Column(snapshot, "clientes", "id").Increment);
            Assert.Equal("nvarchar(max)", Column(snapshot, "pedidos", "comentario").Type);
            Assert.Empty(snapshot.Enums);
        }
        finally
        {
            await using var admin = new SqlConnection(SqlServerString(server, "master"));
            await admin.OpenAsync(ct);
            await ExecAsync(admin, ct,
                $"IF DB_ID('{database}') IS NOT NULL BEGIN ALTER DATABASE {database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {database}; END");
        }
    }

    [Fact]
    public async Task A_wrong_password_is_a_refusal_that_does_not_repeat_the_password()
    {
        // DBML-024: several drivers put the connection string in their exception text.
        var server = Server("CODEFLOW_TEST_POSTGRES");
        const string wrong = "definitely-not-the-password-7f3a";

        var error = await Assert.ThrowsAsync<DbmlConnectionException>(async () =>
            await new PostgresIntrospector().ProbeAsync(
                Connection(DbmlDrivers.Postgres, server, "postgres"), wrong, TestContext.Current.CancellationToken));

        Assert.StartsWith(DbmlConnectionException.Marker, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(wrong, error.Message, StringComparison.Ordinal);
    }

    // ---------- the assertions every engine shares ----------

    private static void AssertCommonShape(DbmlSchemaSnapshot snapshot, string schema)
    {
        Assert.Equal(["clientes", "lineas", "notas_linea", "pedidos"], snapshot.Tables.Select(t => t.Name));
        Assert.All(snapshot.Tables, table => Assert.Equal(schema, table.Schema));

        var email = Column(snapshot, "clientes", "email");
        Assert.True(email.Unique);
        Assert.True(email.NotNull);
        Assert.Contains("255", email.Type, StringComparison.Ordinal);
        Assert.True(Column(snapshot, "clientes", "id").Pk);

        // A composite key: both members pk, neither an increment, and one pk index in declared order.
        var lineas = Table(snapshot, "lineas");
        Assert.All(lineas.Columns.Where(c => c.Name is "pedido_id" or "producto"), c =>
        {
            Assert.True(c.Pk);
            Assert.False(c.Increment);
        });
        var key = Assert.Single(lineas.Indexes, i => i.Pk);
        Assert.Equal(["pedido_id", "producto"], key.Columns);

        Assert.Contains(Table(snapshot, "pedidos").Indexes, i => i.Name == "ix_pedidos_cliente" && i.Columns.SequenceEqual(["cliente_id"]));

        // Defaults, unwrapped from each engine's own quoting and casts.
        Assert.Equal("pendiente", Column(snapshot, "pedidos", "estado").DefaultValue);
        Assert.StartsWith("0", Column(snapshot, "pedidos", "total").DefaultValue ?? "", StringComparison.Ordinal);

        var toClientes = Assert.Single(snapshot.Refs, r => r.FromTable == "pedidos");
        Assert.Equal(["cliente_id"], toClientes.FromColumns);
        Assert.Equal("clientes", toClientes.ToTable);
        Assert.Equal("cascade", toClientes.OnDelete);

        Assert.Equal("cascade", Assert.Single(snapshot.Refs, r => r.FromTable == "lineas").OnDelete);

        // The composite foreign key: child and parent columns named differently, so a mispaired read
        // cannot pass by coincidence.
        var notas = Assert.Single(snapshot.Refs, r => r.FromTable == "notas_linea");
        Assert.Equal(["n_pedido", "n_producto"], notas.FromColumns);
        Assert.Equal(["pedido_id", "producto"], notas.ToColumns);
        Assert.Null(notas.OnDelete);
    }

    private static DbmlSnapshotTable Table(DbmlSchemaSnapshot snapshot, string name) =>
        Assert.Single(snapshot.Tables, t => t.Name == name);

    private static DbmlSnapshotColumn Column(DbmlSchemaSnapshot snapshot, string table, string name) =>
        Assert.Single(Table(snapshot, table).Columns, c => c.Name == name);

    // ---------- plumbing ----------

    private sealed record ServerSpec(string Host, int Port, string User, string Password);

    /// <summary>`host:port:user:password` from the variable, or a skip that says how to set it.</summary>
    private static ServerSpec Server(string variable)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(raw),
            $"set {variable}=host:port:user:password to run this against a real server — see the class remarks");

        var parts = raw!.Split(':', 4);
        Assert.SkipWhen(parts.Length != 4, $"{variable} must be host:port:user:password");

        return new ServerSpec(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), parts[2], parts[3]);
    }

    private static string ThrowawayName() => $"cf_intro_{Guid.NewGuid():N}"[..17];

    private static DbmlConnection Connection(string driver, ServerSpec server, string database) =>
        new("test", "test", driver, server.Host, server.Port, database, server.User, null, false, "now", "now");

    private static string PostgresString(ServerSpec s, string database) => new NpgsqlConnectionStringBuilder
    {
        Host = s.Host,
        Port = s.Port,
        Username = s.User,
        Password = s.Password,
        Database = database,
        Pooling = false,
    }.ToString();

    private static string MySqlString(ServerSpec s, string? database) => new MySqlConnectionStringBuilder
    {
        Server = s.Host,
        Port = (uint)s.Port,
        UserID = s.User,
        Password = s.Password,
        Database = database ?? string.Empty,
        Pooling = false,
        AllowPublicKeyRetrieval = true,
    }.ToString();

    private static string SqlServerString(ServerSpec s, string database) => new SqlConnectionStringBuilder
    {
        DataSource = $"{s.Host},{s.Port}",
        UserID = s.User,
        Password = s.Password,
        InitialCatalog = database,
        Encrypt = false,
        TrustServerCertificate = true,
        Pooling = false,
    }.ToString();

    private static async Task ExecAsync(DbConnection db, CancellationToken ct, params string[] statements)
    {
        foreach (var sql in statements)
        {
            await using var command = db.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }
    }
}
