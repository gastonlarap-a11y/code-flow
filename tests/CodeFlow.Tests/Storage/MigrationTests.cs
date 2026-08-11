using CodeFlow.Storage;
using CodeFlow.Tests.TestVectors;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeFlow.Tests.Storage;

/// <summary>
/// The migration procedure, driven by the scenarios The extraction pass extracted from the extracted cases.
/// </summary>
/// <remarks>
/// These matter more than their size suggests: this codebase opens an existing user's
/// <c>codeflow.db</c>, so a migration that drops a row destroys work the app itself cannot get
/// back. Each scenario seeds a real legacy schema from
/// <c>docs/business-rules/test-vectors/sql/</c> and asserts what 1.7.2 asserts.
/// </remarks>
public sealed class MigrationTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in _files)
        {
            foreach (var path in new[] { file, $"{file}-wal", $"{file}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public void A_fresh_database_gets_every_table_and_index()
    {
        using var connection = OpenSeeded(seed: null);

        var tables = Names(connection, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'");
        var indexes = Names(connection, "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'idx_%'");

        Assert.Equal(21, tables.Count);
        Assert.Equal(9, indexes.Count);

        // Spot-check the ones a later slice depends on being spelled exactly this way.
        Assert.Contains("workspace_prompts", tables);
        Assert.Contains("api_cookies", tables);
        Assert.Contains("idx_api_cookies_key", indexes);

        // The work-item tables. `ticket_review_runs` is named here because the temptation is to
        // fold it into review_runs, and doing so would break that table's pr_id contract.
        Assert.Contains("tickets", tables);
        Assert.Contains("ticket_links", tables);
        Assert.Contains("ticket_review_runs", tables);
        Assert.Contains("idx_tickets_identity", indexes);

        // The two activity tables are append-only and never purged, so an unindexed read is a
        // full scan that grows with the install. Named here so dropping one is not silent.
        Assert.Contains("idx_activity_log_project", indexes);
        Assert.Contains("idx_job_history_project", indexes);
    }

    [Fact]
    public void A_fresh_database_needs_no_api_table_migration()
    {
        using var connection = OpenSeeded(seed: null);

        // The workspace_id column comes from the schema batch, not from the rename-and-copy pair,
        // so a fresh database never enters that path at all.
        Assert.True(HasColumn(connection, "api_collections", "workspace_id"));
        Assert.False(TableExists(connection, "api_collections_legacy"));
    }

    /// <summary>
    /// Fixture: <c>migrations.vectors.json#reparents-legacy-rows-to-oldest-workspace</c>.
    /// </summary>
    [Fact]
    public void Migrating_a_pre_workspace_database_keeps_every_row_and_reparents_it()
    {
        using var connection = OpenSeeded("sql/migrations-legacy-pre-workspace.sql");

        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM api_collections"));
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM api_folders"));
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM api_requests"));
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM api_history"));
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM api_cookies"));

        // Rows land in the oldest workspace — the one a single-workspace user has been working in.
        Assert.Equal("w-old", Scalar(connection, "SELECT workspace_id FROM api_collections WHERE id = 'c1'"));
        Assert.Equal("w-old", Scalar(connection, "SELECT workspace_id FROM api_history WHERE id = 'h1'"));
        Assert.Equal("w-old", Scalar(connection, "SELECT workspace_id FROM api_cookies WHERE id = 'k1'"));
        Assert.Equal("w-old", Scalar(connection, "SELECT workspace_id FROM api_environments WHERE id = 'e1'"));

        // The legacy unscoped Globals row is dropped rather than carried over, and a fresh one is
        // seeded per workspace — carrying it would leave two in whichever workspace inherited it.
        Assert.Equal(2, Count(connection, "SELECT COUNT(*) FROM api_environments WHERE is_global = 1"));
        Assert.Equal(2, Count(connection, "SELECT COUNT(DISTINCT workspace_id) FROM api_environments WHERE is_global = 1"));

        foreach (var legacy in new[]
                 {
                     "api_collections_legacy", "api_environments_legacy",
                     "api_history_legacy", "api_cookies_legacy",
                 })
        {
            Assert.False(TableExists(connection, legacy), legacy);
        }
    }

    /// <summary>
    /// Fixture: <c>migrations.vectors.json#migration-is-idempotent-on-a-legacy-database</c>.
    /// </summary>
    /// <remarks>
    /// Idempotency is the whole safety mechanism, since there is no version table: the procedure
    /// runs on every single launch, so "runs twice" is the normal case, not an edge case.
    /// </remarks>
    [Fact]
    public void Running_the_migration_twice_changes_nothing()
    {
        using var connection = OpenSeeded("sql/migrations-legacy-pre-workspace.sql");

        Migrations.Run(connection);

        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM api_collections"));
        Assert.Equal(2, Count(connection, "SELECT COUNT(*) FROM api_environments WHERE is_global = 1"));
    }

    /// <summary>
    /// Fixture: <c>migrations.vectors.json#no-workspace-keeps-legacy-rows-until-one-exists</c>.
    /// </summary>
    /// <remarks>
    /// With nowhere to put the rows, the legacy tables are left standing rather than dropped.
    /// Destroying a user's collections to tidy up a half-finished migration is not a trade worth
    /// making, and the next launch after a workspace exists finishes the job.
    /// </remarks>
    [Fact]
    public void A_database_with_no_workspace_keeps_its_legacy_rows_until_one_exists()
    {
        using var connection = OpenSeeded("sql/migrations-legacy-no-workspace.sql");

        Assert.True(TableExists(connection, "api_collections_legacy"));
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM api_collections_legacy"));

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO workspaces (id, name, icon, color, sort_order, created_at) " +
                "VALUES ('w1', 'First', 'folder', '#6366f1', 0, '2026-01-01T00:00:00.0000000+00:00')";
            insert.ExecuteNonQuery();
        }

        Migrations.Run(connection);

        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM api_collections WHERE workspace_id = 'w1'"));
        Assert.False(TableExists(connection, "api_collections_legacy"));
    }

    /// <summary>
    /// <c>BUG-STORE-a</c>, fixed after parity: a copy that dies partway leaves nothing behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CodeFlow 1.7.2 ran the four copies unwrapped, so a crash between them left the new tables
    /// half-filled <em>and</em> the legacy tables still standing — and because the procedure runs on
    /// every launch with no version table, the next launch re-copied the rows it had already copied
    /// and died on a primary-key collision. Every launch after that did the same. The app stopped
    /// starting, and the thing blocking it was the user's own data.
    /// </para>
    /// <para>
    /// The state is rebuilt rather than crashed into: a legacy table standing next to rows that were
    /// already copied out of it is precisely what a half-applied run leaves, and it is the state the
    /// user's database is stuck in by the time they notice. What matters is that the next launch
    /// finishes the job instead of dying on the rows it already moved.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_launch_after_a_half_applied_copy_finishes_the_job_instead_of_colliding()
    {
        // A normal, completed migration first: `api_collections` now holds c1.
        using var connection = OpenSeeded("sql/migrations-legacy-pre-workspace.sql");
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM api_collections WHERE id = 'c1'"));

        // Now put back what a crash would have left behind. All four sources are still standing —
        // they are dropped together, at the end — and the first one still holds the row that already
        // made it across. That row is the collision.
        Execute(connection,
            """
            CREATE TABLE api_collections_legacy (
                id TEXT PRIMARY KEY, name TEXT, description TEXT, auth TEXT,
                pre_script TEXT, post_script TEXT, variables TEXT,
                sort_order INTEGER, created_at TEXT, updated_at TEXT
            );
            CREATE TABLE api_environments_legacy (
                id TEXT PRIMARY KEY, name TEXT, variables TEXT,
                is_global INTEGER, sort_order INTEGER, created_at TEXT
            );
            CREATE TABLE api_history_legacy (
                id TEXT PRIMARY KEY, request_id TEXT, name TEXT, protocol TEXT, method TEXT,
                url TEXT, status INTEGER, duration_ms INTEGER, size_bytes INTEGER,
                snapshot TEXT, created_at TEXT
            );
            CREATE TABLE api_cookies_legacy (
                id TEXT PRIMARY KEY, domain TEXT, path TEXT, name TEXT, value TEXT,
                secure INTEGER, http_only INTEGER, expires TEXT, updated_at TEXT
            );
            INSERT INTO api_collections_legacy
                (id, name, description, auth, pre_script, post_script, variables, sort_order, created_at, updated_at)
            VALUES ('c1', 'Flow', '', '', '', '', '[]', 0, '2020-01-01', '2020-01-01');
            """);

        // This is the launch that used to fail — and then every launch after it, forever.
        Migrations.Run(connection);

        // Finished: the duplicate was skipped rather than fatal, and the source is gone.
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM api_collections WHERE id = 'c1'"));
        Assert.False(TableExists(connection, "api_collections_legacy"));

        // And the pragma the copy turns off is back on. This is the connection the process holds for
        // its whole life, so leaving it off would quietly disable every foreign key in the app —
        // including the one AMBIGUOUS-WS-a depends on.
        Assert.Equal(1, Count(connection, "PRAGMA foreign_keys"));
    }

    [Fact]
    public void Every_workspace_is_seeded_with_both_built_in_prompts()
    {
        using var connection = OpenSeeded(seed: null);

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO workspaces (id, name, icon, color, sort_order, created_at) " +
                "VALUES ('w1', 'First', 'folder', '#6366f1', 0, '2026-01-01T00:00:00.0000000+00:00')";
            insert.ExecuteNonQuery();
        }

        Migrations.Run(connection);

        // The real default text, not a blank: a user sees and can edit the actual methodology
        // rather than an empty box, and a blank save is what resets it.
        var standard = Scalar(connection,
            "SELECT content FROM workspace_prompts WHERE workspace_id = 'w1' AND kind = 'review_standard'");
        var description = Scalar(connection,
            "SELECT content FROM workspace_prompts WHERE workspace_id = 'w1' AND kind = 'pr_description'");

        Assert.NotNull(standard);
        Assert.NotNull(description);
        Assert.Equal(CodeFlow.Ai.Prompts.DefaultPrReviewStandard, standard);
        Assert.Equal(CodeFlow.Ai.Prompts.DefaultPrDescriptionTemplate, description);
    }

    [Fact]
    public void An_existing_workspaces_table_gains_the_git_identity_pair()
    {
        // The legacy seed's workspaces table predates the pair, so this exercises the AddColumn
        // path rather than the schema batch (WS-008).
        using var connection = OpenSeeded("sql/migrations-legacy-pre-workspace.sql");

        Assert.True(HasColumn(connection, "workspaces", "git_name"));
        Assert.True(HasColumn(connection, "workspaces", "git_email"));

        // Pre-existing rows read as "no override", not as an empty-string identity.
        Assert.Null(Scalar(connection, "SELECT git_name FROM workspaces LIMIT 1"));
        Assert.Null(Scalar(connection, "SELECT git_email FROM workspaces LIMIT 1"));

        Migrations.Run(connection);
        Assert.True(HasColumn(connection, "workspaces", "git_name"));
    }

    [Fact]
    public void A_review_run_stranded_by_a_pre_fix_move_is_realigned_with_its_project()
    {
        // The backfill half of BUG-STORE-b's fix: moves made before move_project_to_workspace
        // kept review_runs.workspace_id in step left rows stamped with the old workspace.
        using var connection = OpenSeeded(seed: null);

        Execute(connection,
            """
            INSERT INTO workspaces (id, name, icon, color, sort_order, created_at)
            VALUES ('ws-old', 'Old', 'folder', '#111111', 0, '2026-01-01'),
                   ('ws-new', 'New', 'folder', '#222222', 1, '2026-01-01');
            INSERT INTO projects (id, workspace_id, name, local_path, sort_order, created_at)
            VALUES ('proj-1', 'ws-new', 'moved', '/tmp/moved', 0, '2026-01-01');
            INSERT INTO review_runs (id, project_id, workspace_id, pr_id, iter, level, meta, review_md, diff, findings, created_at)
            VALUES ('run-1', 'proj-1', 'ws-old', 7, 1, 'completo', '{}', '', '', '[]', '2026-01-01');
            """);

        Migrations.Run(connection);

        Assert.Equal(["ws-new"], Names(connection, "SELECT workspace_id FROM review_runs WHERE id = 'run-1'"));

        // And once aligned, another run matches nothing.
        Migrations.Run(connection);
        Assert.Equal(["ws-new"], Names(connection, "SELECT workspace_id FROM review_runs WHERE id = 'run-1'"));
    }

    [Fact]
    public void Foreign_keys_cascade_a_workspace_deletion()
    {
        // Nine tables reach a workspace through ON DELETE CASCADE. The pragma is per-connection,
        // so forgetting it does not fail — it silently orphans rows.
        using var connection = OpenSeeded(seed: null);

        using (var seed = connection.CreateCommand())
        {
            seed.CommandText =
                """
                INSERT INTO workspaces (id, name, icon, color, sort_order, created_at)
                    VALUES ('w1', 'First', 'folder', '#6366f1', 0, '2026-01-01T00:00:00.0000000+00:00');
                INSERT INTO projects (id, workspace_id, name, local_path, sort_order, created_at)
                    VALUES ('p1', 'w1', 'Repo', '/tmp/repo', 0, '2026-01-01T00:00:00.0000000+00:00');
                """;
            seed.ExecuteNonQuery();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM workspaces WHERE id = 'w1'";
            delete.ExecuteNonQuery();
        }

        Assert.Equal(0, Count(connection, "SELECT COUNT(*) FROM projects"));
    }

    // -----------------------------------------------------------------------

    private SqliteConnection OpenSeeded(string? seed)
    {
        var connection = OpenRaw(seed);
        Migrations.Run(connection);
        return connection;
    }

    /// <summary>Seeded and open, but not migrated — for tests that need to act before the run.</summary>
    private SqliteConnection OpenRaw(string? seed)
    {
        // A file rather than :memory: — the migration renames tables and toggles pragmas, and a
        // shared in-memory database behaves differently enough to be worth avoiding here.
        var path = Path.Combine(Path.GetTempPath(), $"codeflow-mig-{Guid.NewGuid():N}.db");
        _files.Add(path);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();

        if (seed is not null)
        {
            using var command = connection.CreateCommand();
            command.CommandText = File.ReadAllText(Path.Combine(FixtureCatalog.Directory, seed));
            command.ExecuteNonQuery();
        }

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }

    private static List<string> Names(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1) == column)
            {
                return true;
            }
        }

        return false;
    }
}
