using CodeFlow.Ai;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Storage;

/// <summary>
/// The startup migration procedure: 20 steps, run to completion before anything reads the schema.
/// </summary>
/// <remarks>
/// <para>
/// There is <b>no version table</b>. Every step decides for itself whether it already ran, by
/// checking for a table or a column. That is unusual, and deliberate: it is upgrade-safe by
/// construction, works on a database of any age, and is what 1.7.2 already ships and has
/// tests for. Recorded as <c>DIVERGENCE-STORE-*</c>; do not "modernise" it into a version
/// counter, because a counter cannot tell what an out-of-band edit did to the file.
/// </para>
/// <para>
/// Three ordering constraints are load-bearing. The rest of the steps are independent.
/// </para>
/// </remarks>
internal static class Migrations
{
    /// <summary>Runs every migration in order. Not idempotent by transaction — by inspection.</summary>
    public static void Run(SqliteConnection connection)
    {
        // ORDERING 1 of 3: before the schema batch. It moves the pre-workspace api_* tables aside
        // so the batch recreates them in their current shape, and the matching Finish step copies
        // the rows across.
        MigrateApiTablesBegin(connection);

        Execute(connection, Schema.Sql);

        MigrateApiTablesFinish(connection);
        EnsureGlobalsEnvironment(connection);

        MigrateReviewContextsToWorkspace(connection);
        MigrateMdFilesIntoContexts(connection);

        // ORDERING 2 of 3: MigrateReviewStandardsIntoPrompts must run before the backfill. Both
        // write workspace_prompts keyed (workspace_id, 'review_standard'), and the migration drops
        // its source table in the same step — so if the backfill seeded the default first, the
        // INSERT OR IGNORE below would discard the user's own edited standard, permanently.
        MigrateReviewStandardsIntoPrompts(connection);
        BackfillWorkspacePrompts(connection);

        DropLegacyInstalledSkills(connection);
        AddSessionIdToActivityLog(connection);
        AddResponseTimeToActivityLog(connection);
        AddIsErrorToActivityLog(connection);
        AddEngineSessionIdToActivityLog(connection);
        AddTraceToActivityLog(connection);
        AddEngineMetaToActivityLog(connection);
        AddCustomLabelToJobHistory(connection);

        // ORDERING 3 of 3: host after owner/repo. A row with owner and repo set but a null host
        // defaults to github.com, which only reads correctly if the pair exists first.
        AddGithubColumnsToProjects(connection);
        AddGithubHostToProjects(connection);

        AddEnabledToWorkspaceSkills(connection);
        AddProviderToWorkspaceAgents(connection);
        RealignReviewRunWorkspaces(connection);
        AddGitIdentityToWorkspaces(connection);
    }

    /// <summary>
    /// Seeds each workspace's "Globals" pseudo-environment.
    /// </summary>
    /// <remarks>
    /// Keyed on <c>is_global</c> rather than a fixed id, so a user renaming it does not cause a
    /// duplicate to be seeded on the next launch.
    /// </remarks>
    private static void EnsureGlobalsEnvironment(SqliteConnection connection)
    {
        Execute(connection,
            """
            INSERT INTO api_environments (id, workspace_id, name, variables, is_global, sort_order, created_at)
            SELECT lower(hex(randomblob(16))), w.id, 'Globals', '[]', 1, -1, $now
            FROM workspaces w
            WHERE NOT EXISTS (
                SELECT 1 FROM api_environments e WHERE e.workspace_id = w.id AND e.is_global = 1
            )
            """,
            ("$now", Clock.Now()));
    }

    /// <summary>
    /// Moves the pre-workspace <c>api_*</c> tables aside so the schema batch can recreate them.
    /// </summary>
    /// <remarks>
    /// SQLite cannot add the column in place: <c>ALTER TABLE … ADD COLUMN</c> rejects a
    /// <c>REFERENCES</c> clause while foreign keys are on, so an in-place add would leave a
    /// migrated database without the cascade a fresh one has — deleting a workspace would
    /// silently orphan its API rows instead of removing them.
    ///
    /// <c>legacy_alter_table</c> is essential rather than cosmetic: without it SQLite helpfully
    /// rewrites the foreign keys in <c>api_folders</c>/<c>api_requests</c> to point at
    /// <c>api_collections_legacy</c>, and the rename silently takes the children with it.
    /// </remarks>
    private static void MigrateApiTablesBegin(SqliteConnection connection)
    {
        if (!TableExists(connection, "api_collections") || HasColumn(connection, "api_collections", "workspace_id"))
        {
            return;
        }

        Execute(connection,
            """
            PRAGMA foreign_keys = OFF;
            PRAGMA legacy_alter_table = ON;
            -- Dropped rather than carried along: a renamed table keeps its indexes under their
            -- old names, which would collide when the schema batch recreates them.
            DROP INDEX IF EXISTS idx_api_history_time;
            DROP INDEX IF EXISTS idx_api_cookies_key;
            ALTER TABLE api_collections  RENAME TO api_collections_legacy;
            ALTER TABLE api_environments RENAME TO api_environments_legacy;
            ALTER TABLE api_history      RENAME TO api_history_legacy;
            ALTER TABLE api_cookies      RENAME TO api_cookies_legacy;
            PRAGMA legacy_alter_table = OFF;
            """);
    }

    /// <summary>
    /// Copies the pre-workspace rows into the recreated tables, assigning them to the oldest
    /// workspace — the one a single-workspace user has been working in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With no workspace at all the legacy tables are left untouched rather than dropped: there
    /// is nowhere to put the rows, and destroying a user's collections to tidy up a migration is
    /// not a trade worth making. The next launch after a workspace exists finishes the job.
    /// </para>
    /// <para>
    /// <b><c>BUG-STORE-a</c> is fixed here, deliberately and after parity.</b> CodeFlow 1.7.2 runs the
    /// four copies unwrapped and non-idempotent, so a crash between them left the migration
    /// half-applied and <em>every subsequent launch</em> failed on a primary-key collision instead of
    /// resuming — an app that will not start, with the user's own data as the thing blocking it. The
    /// port reproduced that faithfully while reaching parity (<c>.claude/rules/dotnet.md</c>); the operator
    /// then decided to close it, which is what §4 asks for once parity is reached rather than a
    /// silent correction.
    /// </para>
    /// <para>
    /// Both halves of the fix are here, because they answer different questions. The transaction
    /// stops the broken state from being <em>created</em>: the copy either happens or it does not, and
    /// the <c>DROP</c>s are inside it so the sources cannot vanish without the rows arriving.
    /// <c>INSERT OR IGNORE</c> lets an <em>already</em> broken database recover: a user whose app
    /// stopped starting has rows that were copied before the crash, and re-copying them is exactly
    /// what used to collide. Skipping the duplicates finishes the migration instead of failing it.
    /// It is safe precisely because ids are carried across unchanged, so a row that is already there
    /// is the same row.
    /// </para>
    /// <para>
    /// <b><c>PRAGMA foreign_keys</c> cannot move inside the transaction.</b> SQLite ignores it while
    /// one is open — silently, with no error — so it is set before <c>BEGIN</c> and restored in a
    /// <c>finally</c>. Restoring it matters beyond this method: the connection is the one the process
    /// holds for its whole life, so leaving it off would disable every foreign key in the app.
    /// </para>
    /// </remarks>
    private static void MigrateApiTablesFinish(SqliteConnection connection)
    {
        if (!TableExists(connection, "api_collections_legacy"))
        {
            return;
        }

        var workspaceId = ScalarOrNull(connection,
            "SELECT id FROM workspaces ORDER BY sort_order, created_at LIMIT 1");
        if (workspaceId is null)
        {
            return;
        }

        Execute(connection, "PRAGMA foreign_keys = OFF;");

        try
        {
            Execute(connection, "BEGIN;");
            CopyLegacyApiTables(connection, workspaceId);
            Execute(connection, "COMMIT;");
        }
        catch
        {
            RollBackQuietly(connection);
            throw;
        }
        finally
        {
            Execute(connection, "PRAGMA foreign_keys = ON;");
        }
    }

    /// <summary>The body of the API-table migration, run inside one transaction by its caller.</summary>
    private static void CopyLegacyApiTables(SqliteConnection connection, string workspaceId)
    {
        Execute(connection,
            """
            INSERT OR IGNORE INTO api_collections
                (id, workspace_id, name, description, auth, pre_script, post_script, variables,
                 sort_order, created_at, updated_at)
            SELECT id, $ws, name, description, auth, pre_script, post_script, variables,
                   sort_order, created_at, updated_at
            FROM api_collections_legacy
            """,
            ("$ws", workspaceId));

        // The Globals row is per-workspace now, and EnsureGlobalsEnvironment seeds one for every
        // workspace right after this — carrying the old one over would leave two in the workspace
        // that inherits it.
        Execute(connection,
            """
            INSERT OR IGNORE INTO api_environments
                (id, workspace_id, name, variables, is_global, sort_order, created_at)
            SELECT id, $ws, name, variables, is_global, sort_order, created_at
            FROM api_environments_legacy WHERE is_global = 0
            """,
            ("$ws", workspaceId));

        Execute(connection,
            """
            INSERT OR IGNORE INTO api_history
                (id, workspace_id, request_id, name, protocol, method, url, status, duration_ms,
                 size_bytes, snapshot, created_at)
            SELECT id, $ws, request_id, name, protocol, method, url, status, duration_ms,
                   size_bytes, snapshot, created_at
            FROM api_history_legacy
            """,
            ("$ws", workspaceId));

        Execute(connection,
            """
            INSERT OR IGNORE INTO api_cookies
                (id, workspace_id, domain, path, name, value, secure, http_only, expires, updated_at)
            SELECT id, $ws, domain, path, name, value, secure, http_only, expires, updated_at
            FROM api_cookies_legacy
            """,
            ("$ws", workspaceId));

        // Inside the transaction with the copies, which is the point: dropping the sources is what
        // makes the migration irreversible, so it must not be able to happen without them.
        Execute(connection,
            """
            DROP TABLE api_collections_legacy;
            DROP TABLE api_environments_legacy;
            DROP TABLE api_history_legacy;
            DROP TABLE api_cookies_legacy;
            """);
    }

    /// <summary>
    /// Rolls back, swallowing a failure to do so.
    /// </summary>
    /// <remarks>
    /// The caller is already unwinding an exception that says what actually went wrong. A
    /// <c>ROLLBACK</c> with no open transaction throws on its own, and letting that replace the real
    /// failure would hide the cause behind a symptom.
    /// </remarks>
    private static void RollBackQuietly(SqliteConnection connection)
    {
        try
        {
            Execute(connection, "ROLLBACK;");
        }
        catch (SqliteException)
        {
        }
    }

    /// <summary>
    /// Re-scopes <c>review_contexts</c> from per-project to per-workspace.
    /// </summary>
    /// <remarks>
    /// Rows are re-pointed at their project's workspace rather than the column simply being
    /// dropped, which would silently discard content the user wrote. A row whose project no
    /// longer exists has no workspace to move to and is deleted.
    /// </remarks>
    private static void MigrateReviewContextsToWorkspace(SqliteConnection connection)
    {
        if (!HasColumn(connection, "review_contexts", "project_id"))
        {
            return;
        }

        Execute(connection,
            """
            ALTER TABLE review_contexts ADD COLUMN workspace_id TEXT;
            UPDATE review_contexts
                SET workspace_id = (SELECT workspace_id FROM projects WHERE projects.id = review_contexts.project_id);
            DELETE FROM review_contexts WHERE workspace_id IS NULL;
            ALTER TABLE review_contexts DROP COLUMN project_id;
            """);
    }

    /// <summary>Folds the old "Instructions / .md" table into review contexts.</summary>
    private static void MigrateMdFilesIntoContexts(SqliteConnection connection)
    {
        if (!TableExists(connection, "workspace_md_files"))
        {
            return;
        }

        Execute(connection,
            """
            INSERT OR IGNORE INTO review_contexts (id, workspace_id, name, content, enabled, created_at)
                SELECT id, workspace_id, filename, content, enabled, created_at FROM workspace_md_files;
            DROP TABLE workspace_md_files;
            """);
    }

    /// <summary>Moves the original review-standard table into the generalised prompt table.</summary>
    private static void MigrateReviewStandardsIntoPrompts(SqliteConnection connection)
    {
        if (!TableExists(connection, "workspace_review_standards"))
        {
            return;
        }

        Execute(connection,
            """
            INSERT OR IGNORE INTO workspace_prompts (workspace_id, kind, content, updated_at)
                SELECT workspace_id, 'review_standard', content, updated_at FROM workspace_review_standards;
            DROP TABLE workspace_review_standards;
            """);
    }

    /// <summary>
    /// Seeds the built-in default of each prompt kind into every workspace that lacks it.
    /// </summary>
    /// <remarks>
    /// Seeds the real default text rather than a blank, so a user sees and can edit the actual
    /// methodology instead of an empty box. Must run after
    /// <see cref="MigrateReviewStandardsIntoPrompts"/> — see the ordering note in <c>Run</c>.
    /// </remarks>
    private static void BackfillWorkspacePrompts(SqliteConnection connection)
    {
        var now = Clock.Now();

        foreach (var (kind, text) in new[]
                 {
                     ("review_standard", Prompts.DefaultPrReviewStandard),
                     ("pr_description", Prompts.DefaultPrDescriptionTemplate),
                 })
        {
            Execute(connection,
                """
                INSERT INTO workspace_prompts (workspace_id, kind, content, updated_at)
                SELECT w.id, $kind, $content, $now FROM workspaces w
                WHERE NOT EXISTS (
                    SELECT 1 FROM workspace_prompts p WHERE p.workspace_id = w.id AND p.kind = $kind
                )
                """,
                ("$kind", kind), ("$content", text), ("$now", now));
        }
    }

    /// <summary>Drops a table superseded by <c>workspace_skills</c> before it ever held data.</summary>
    private static void DropLegacyInstalledSkills(SqliteConnection connection) =>
        Execute(connection, "DROP TABLE IF EXISTS installed_skills;");

    /// <summary>
    /// Repoints <c>review_runs.workspace_id</c> at the owning project's current workspace.
    /// </summary>
    /// <remarks>
    /// The backfill half of <c>BUG-STORE-b</c>'s fix: before <c>move_project_to_workspace</c>
    /// started moving these rows along, any move left them stale — invisible to the new
    /// workspace's list, purgeable by the old one. Naturally idempotent: a row that already
    /// agrees with its project matches nothing.
    /// </remarks>
    private static void RealignReviewRunWorkspaces(SqliteConnection connection) =>
        Execute(connection,
            """
            UPDATE review_runs SET workspace_id = (
                SELECT p.workspace_id FROM projects p WHERE p.id = review_runs.project_id
            )
            WHERE EXISTS (
                SELECT 1 FROM projects p
                WHERE p.id = review_runs.project_id AND p.workspace_id <> review_runs.workspace_id
            );
            """);

    private static void AddSessionIdToActivityLog(SqliteConnection connection) =>
        AddColumn(connection, "activity_log", "session_id", "TEXT");

    private static void AddResponseTimeToActivityLog(SqliteConnection connection) =>
        AddColumn(connection, "activity_log", "response_time_ms", "INTEGER");

    /// <summary>Failed turns used not to be recorded; every pre-existing row is a success.</summary>
    private static void AddIsErrorToActivityLog(SqliteConnection connection) =>
        AddColumn(connection, "activity_log", "is_error", "INTEGER NOT NULL DEFAULT 0");

    /// <summary>
    /// Splits the two meanings <c>session_id</c> used to carry.
    /// </summary>
    /// <remarks>
    /// <c>session_id</c> is now the conversation id, minted by the app and stable for its life.
    /// The engine's own resume token moves to its own column because it is not an identity:
    /// agy reports one fixed sentinel for every run, so every chat collapsed into a single
    /// activity, while the Claude CLI can mint a new id on each resumed turn, so one conversation
    /// scattered across several.
    /// </remarks>
    private static void AddEngineSessionIdToActivityLog(SqliteConnection connection) =>
        AddColumn(connection, "activity_log", "engine_session_id", "TEXT");

    private static void AddTraceToActivityLog(SqliteConnection connection) =>
        AddColumn(connection, "activity_log", "trace", "TEXT");

    /// <summary>
    /// Records which engine answered a turn.
    /// </summary>
    /// <remarks>
    /// Per turn rather than derived from today's settings, which would credit an old answer to
    /// whatever engine happens to be configured now. The three columns are added as a set because
    /// they are always written as one.
    /// </remarks>
    private static void AddEngineMetaToActivityLog(SqliteConnection connection)
    {
        foreach (var column in new[] { "provider", "model", "engine_version" })
        {
            AddColumn(connection, "activity_log", column, "TEXT");
        }
    }

    private static void AddCustomLabelToJobHistory(SqliteConnection connection) =>
        AddColumn(connection, "job_history", "custom_label", "TEXT");

    /// <summary>Added as a pair, because they are always set or cleared together.</summary>
    private static void AddGithubColumnsToProjects(SqliteConnection connection)
    {
        AddColumn(connection, "projects", "github_owner", "TEXT");
        AddColumn(connection, "projects", "github_repo", "TEXT");
    }

    private static void AddGithubHostToProjects(SqliteConnection connection) =>
        AddColumn(connection, "projects", "github_host", "TEXT");

    /// <summary>Existing rows default to enabled, which is the pre-toggle behaviour.</summary>
    private static void AddEnabledToWorkspaceSkills(SqliteConnection connection) =>
        AddColumn(connection, "workspace_skills", "enabled", "INTEGER NOT NULL DEFAULT 1");

    /// <summary>Existing rows default to empty, falling back to the active provider.</summary>
    private static void AddProviderToWorkspaceAgents(SqliteConnection connection) =>
        AddColumn(connection, "workspace_agents", "provider", "TEXT NOT NULL DEFAULT ''");

    /// <summary>Added as a pair: an identity override is only usable with both halves (WS-008).</summary>
    private static void AddGitIdentityToWorkspaces(SqliteConnection connection)
    {
        AddColumn(connection, "workspaces", "git_name", "TEXT");
        AddColumn(connection, "workspaces", "git_email", "TEXT");
    }

    // -----------------------------------------------------------------------
    // Primitives
    // -----------------------------------------------------------------------

    /// <summary>Adds a column unless it already exists. This is the idempotency mechanism.</summary>
    private static void AddColumn(SqliteConnection connection, string table, string column, string definition)
    {
        if (HasColumn(connection, table, column))
        {
            return;
        }

        // Table and column names are compile-time constants from this file, never user input.
        Execute(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
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
        // PRAGMA does not accept a bound parameter for the table name.
        command.CommandText = $"PRAGMA table_info({table})";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ScalarOrNull(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}
