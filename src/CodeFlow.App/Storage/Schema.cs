namespace CodeFlow.Storage;

/// <summary>
/// The canonical schema: 18 tables and 7 indexes, applied as one batch on every startup.
/// </summary>
/// <remarks>
/// <para>
/// Every column type, <c>NOT NULL</c>, <c>DEFAULT</c> and <c>REFERENCES</c> clause matches what a
/// CodeFlow 1.7.2 install already wrote. This opens an existing user's <c>codeflow.db</c>, so a
/// difference here is not a style choice, it is a schema mismatch against real data.
/// </para>
/// <para>
/// Everything is <c>IF NOT EXISTS</c>. There is deliberately no version table: idempotency is
/// structural, which is upgrade-safe by construction and is what 1.7.2 already ships.
/// </para>
/// <para>
/// Note the boolean columns are <c>INTEGER</c>. SQLite has no boolean type and the port must map
/// them explicitly rather than relying on a provider convention.
/// </para>
/// </remarks>
internal static class Schema
{
    public const string Sql = """
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS workspaces (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            icon        TEXT NOT NULL DEFAULT 'folder',
            color       TEXT NOT NULL DEFAULT '#6366f1',
            sort_order  INTEGER NOT NULL DEFAULT 0,
            created_at  TEXT NOT NULL,
            git_name    TEXT,
            git_email   TEXT
        );

        CREATE TABLE IF NOT EXISTS projects (
            id          TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
            name        TEXT NOT NULL,
            local_path  TEXT NOT NULL,
            remote_url  TEXT,
            color       TEXT NOT NULL DEFAULT '#6366f1',
            icon        TEXT NOT NULL DEFAULT 'git-branch',
            ado_org      TEXT,
            ado_project  TEXT,
            ado_repo_id  TEXT,
            github_owner TEXT,
            github_repo  TEXT,
            github_host  TEXT,
            sort_order   INTEGER NOT NULL DEFAULT 0,
            created_at   TEXT NOT NULL
        );

        -- Review context is scoped per WORKSPACE (see MigrateReviewContextsToWorkspace for the
        -- project_id -> workspace_id column migration for pre-existing rows).
        CREATE TABLE IF NOT EXISTS review_contexts (
            id           TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
            name         TEXT NOT NULL,
            content      TEXT NOT NULL DEFAULT '',
            enabled      INTEGER NOT NULL DEFAULT 1,
            created_at   TEXT NOT NULL
        );

        -- Per-workspace, provider-independent prompt overrides keyed by `kind`. Empty content
        -- means "use the built-in default", so resetting is just a blank save.
        CREATE TABLE IF NOT EXISTS workspace_prompts (
            workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
            kind         TEXT NOT NULL,
            content      TEXT NOT NULL DEFAULT '',
            updated_at   TEXT NOT NULL,
            PRIMARY KEY (workspace_id, kind)
        );

        -- Durable memory of every completed PR review. Timestamped rows, never overwritten, so
        -- the code a finding referred to stays recoverable after the branch is gone.
        CREATE TABLE IF NOT EXISTS review_runs (
            id           TEXT PRIMARY KEY,
            project_id   TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            workspace_id TEXT NOT NULL,
            pr_id        INTEGER NOT NULL,
            iter         INTEGER NOT NULL,
            level        TEXT NOT NULL,
            meta         TEXT NOT NULL DEFAULT '{}',
            review_md    TEXT NOT NULL,
            diff         TEXT NOT NULL DEFAULT '',
            findings     TEXT NOT NULL DEFAULT '[]',
            created_at   TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_review_runs_pr ON review_runs (project_id, pr_id, created_at);

        CREATE TABLE IF NOT EXISTS workspace_skills (
            id           TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
            skill_name   TEXT NOT NULL,
            source_repo  TEXT NOT NULL,
            enabled      INTEGER NOT NULL DEFAULT 1,
            installed_at TEXT NOT NULL
        );

        -- User-defined SDD/Harness agents per workspace. Deliberately empty by default.
        CREATE TABLE IF NOT EXISTS workspace_agents (
            id           TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
            name         TEXT NOT NULL,
            role         TEXT NOT NULL DEFAULT '',
            provider     TEXT NOT NULL DEFAULT '',
            model        TEXT NOT NULL DEFAULT '',
            prompt       TEXT NOT NULL DEFAULT '',
            enabled      INTEGER NOT NULL DEFAULT 1,
            sort_order   INTEGER NOT NULL DEFAULT 0,
            created_at   TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS workspace_mcps (
            id           TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
            name         TEXT NOT NULL,
            command      TEXT NOT NULL,
            args         TEXT NOT NULL DEFAULT '',
            env          TEXT NOT NULL DEFAULT '',
            enabled      INTEGER NOT NULL DEFAULT 1,
            created_at   TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS app_settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS activity_log (
            id          TEXT PRIMARY KEY,
            project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            session_id  TEXT,
            question    TEXT NOT NULL,
            answer      TEXT NOT NULL,
            created_at  TEXT NOT NULL
        );

        -- Every read of this table filters by project_id and orders by created_at, and nothing
        -- ever deletes from it: without an index the chat history is a full scan that grows for
        -- the life of the install.
        CREATE INDEX IF NOT EXISTS idx_activity_log_project ON activity_log (project_id, created_at);

        CREATE TABLE IF NOT EXISTS job_history (
            id           TEXT PRIMARY KEY,
            project_id   TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            kind         TEXT NOT NULL,
            label        TEXT NOT NULL,
            custom_label TEXT,
            status       TEXT NOT NULL,
            result       TEXT,
            error        TEXT,
            meta         TEXT NOT NULL DEFAULT '{}',
            created_at   TEXT NOT NULL
        );

        -- Same shape as activity_log: append-only, never purged, always read per project.
        CREATE INDEX IF NOT EXISTS idx_job_history_project ON job_history (project_id, created_at);

        CREATE TABLE IF NOT EXISTS conversation_titles (
            session_id  TEXT PRIMARY KEY,
            project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            title       TEXT NOT NULL,
            updated_at  TEXT NOT NULL
        );

        -- ===================== API client (per workspace) =====================
        -- Only the roots carry `workspace_id`: folders and requests reach it through their
        -- collection, so there is exactly one place a row's workspace can be wrong.

        CREATE TABLE IF NOT EXISTS api_collections (
            id           TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL DEFAULT '' REFERENCES workspaces(id) ON DELETE CASCADE,
            name        TEXT NOT NULL,
            description TEXT NOT NULL DEFAULT '',
            -- JSON AuthConfig; '' = nothing configured (children fall through to "none").
            auth        TEXT NOT NULL DEFAULT '',
            pre_script  TEXT NOT NULL DEFAULT '',
            post_script TEXT NOT NULL DEFAULT '',
            variables   TEXT NOT NULL DEFAULT '[]',
            sort_order  INTEGER NOT NULL DEFAULT 0,
            created_at  TEXT NOT NULL,
            updated_at  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS api_folders (
            id            TEXT PRIMARY KEY,
            collection_id TEXT NOT NULL REFERENCES api_collections(id) ON DELETE CASCADE,
            parent_id     TEXT REFERENCES api_folders(id) ON DELETE CASCADE,
            name          TEXT NOT NULL,
            description   TEXT NOT NULL DEFAULT '',
            auth          TEXT NOT NULL DEFAULT '',
            pre_script    TEXT NOT NULL DEFAULT '',
            post_script   TEXT NOT NULL DEFAULT '',
            sort_order    INTEGER NOT NULL DEFAULT 0,
            created_at    TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_api_folders_parent
            ON api_folders (collection_id, parent_id, sort_order);

        CREATE TABLE IF NOT EXISTS api_requests (
            id            TEXT PRIMARY KEY,
            collection_id TEXT NOT NULL REFERENCES api_collections(id) ON DELETE CASCADE,
            folder_id     TEXT REFERENCES api_folders(id) ON DELETE CASCADE,
            name          TEXT NOT NULL,
            -- http | graphql | websocket | socketio | grpc | mqtt
            protocol      TEXT NOT NULL DEFAULT 'http',
            -- Denormalized out of `spec` purely so the tree can render method+URL without
            -- parsing every blob.
            method        TEXT NOT NULL DEFAULT 'GET',
            url           TEXT NOT NULL DEFAULT '',
            spec          TEXT NOT NULL DEFAULT '{}',
            sort_order    INTEGER NOT NULL DEFAULT 0,
            created_at    TEXT NOT NULL,
            updated_at    TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_api_requests_parent
            ON api_requests (collection_id, folder_id, sort_order);

        -- Exactly one row per workspace has `is_global = 1`: the "Globals" pseudo-environment,
        -- always in scope and not deletable (see EnsureGlobalsEnvironment).
        CREATE TABLE IF NOT EXISTS api_environments (
            id           TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL DEFAULT '' REFERENCES workspaces(id) ON DELETE CASCADE,
            name        TEXT NOT NULL,
            variables   TEXT NOT NULL DEFAULT '[]',
            is_global   INTEGER NOT NULL DEFAULT 0,
            sort_order  INTEGER NOT NULL DEFAULT 0,
            created_at  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS api_history (
            id           TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL DEFAULT '' REFERENCES workspaces(id) ON DELETE CASCADE,
            request_id  TEXT,
            name        TEXT NOT NULL DEFAULT '',
            protocol    TEXT NOT NULL DEFAULT 'http',
            method      TEXT NOT NULL DEFAULT '',
            url         TEXT NOT NULL DEFAULT '',
            status      INTEGER,
            duration_ms INTEGER,
            size_bytes  INTEGER,
            snapshot    TEXT NOT NULL DEFAULT '{}',
            created_at  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_api_history_time ON api_history (workspace_id, created_at DESC);

        -- The cookie jar. Persisted rather than held in an HTTP client, because the client is
        -- rebuilt per request (per-request SSL/proxy/redirect overrides make sharing impossible).
        CREATE TABLE IF NOT EXISTS api_cookies (
            id         TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL DEFAULT '' REFERENCES workspaces(id) ON DELETE CASCADE,
            domain     TEXT NOT NULL,
            path       TEXT NOT NULL DEFAULT '/',
            name       TEXT NOT NULL,
            value      TEXT NOT NULL DEFAULT '',
            secure     INTEGER NOT NULL DEFAULT 0,
            http_only  INTEGER NOT NULL DEFAULT 0,
            expires    TEXT,
            updated_at TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_api_cookies_key
            ON api_cookies (workspace_id, domain, path, name);
        """;
}
