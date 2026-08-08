# 03 — Storage

Everything about the local SQLite database: the bootstrap sequence, the schema, the
startup-time migration procedure, the the sidecar models, and every query function's semantics —
ordering, cascades, defaults, idempotency. This is the storage layer beneath every other
domain document; `01-ipc-surface.md` owns the commands that call into it.

## Scope

- `src/CodeFlow.App/Storage/` — `Database.cs`, `Schema.cs`, `Migrations.cs`, `Sql.cs`, `Clock.cs`
- `src/CodeFlow.App/Workspaces/WorkspaceModels.cs` — the row shapes
- `src/CodeFlow.App/Activity/ActivityLogStore.cs`, `src/CodeFlow.App/Activity/JobHistoryStore.cs`
- `src/CodeFlow.App/ApiClient/ApiTreeStore.cs` and its sibling stores

`AppPaths.cs` is read only for the single fact storage depends on — where the SQLite file lives —
and is otherwise owned by `02-bootstrap-platform.md`.

## Bootstrap

`src/CodeFlow.App/Storage/Database.cs` — `init()`:

1. `AppPaths`().expect(...)` — creates the CodeFlow config directory tree. This is
   an `.expect()`, not a propagated `Result`: **failure here panics the whole process**
   before a single connection is opened.
2. `Connection.open(`AppPaths`())` — opens (creating if absent) the one SQLite file the
   app ever uses. Immediately after open, one pragma batch runs: `journal_mode = WAL`
   (persistent, a no-op on an existing WAL file), `foreign_keys = ON` and
   `synchronous = NORMAL` (both per-connection; NORMAL is SQLite's recommended pairing
   with WAL — durability differs from the FULL default only on power loss, not app crash).
3. `Migrations.Run`(&conn)` — the full migration procedure (below), executed synchronously,
   every launch, before the connection is handed to anything else.
4. `Db(Mutex.new(conn))` — the connection is wrapped in a `Mutex` and that's the entirety of
   the app's storage state: **one physical `Microsoft.Data.Sqlite` for the lifetime of the
   process**, serialized behind the mutex. There is no connection pool anywhere in this
   codebase; every `db/*.rs` function takes `&Connection` and is called with the mutex held
   for the duration of one command.

This ordering — directories, then open, then migrate, then publish as the shell state — is fixed
and has no fallback path: if step 1 or 2 fails the app does not start.

## Schema

18 tables (`CREATE TABLE IF NOT EXISTS` count in `src/CodeFlow.App/Storage/Migrations.cs`), 7 indexes (6
plain + 1 unique). Every table is created by one `conn.execute_batch(...)` call
(`src/CodeFlow.App/Storage/Migrations.cs`) run on every startup — `CREATE TABLE IF NOT EXISTS` makes each
statement individually idempotent, which is the entire mechanism that makes re-running the
batch safe. `PRAGMA foreign_keys = ON;` is the first statement in the batch and stays in
effect for the rest of the connection's life except for two deliberate, narrow windows (see
Migrations, `migrate_api_tables_begin`/`finish`) where it is turned off and explicitly turned
back on before `run()` returns.

DDL below is transcribed byte-for-byte from `src/CodeFlow.App/Storage/Migrations.cs`, including its inline SQL
comments, in the order the batch declares them.

### `workspaces`

`sql
CREATE TABLE IF NOT EXISTS workspaces (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    icon        TEXT NOT NULL DEFAULT 'folder',
    color       TEXT NOT NULL DEFAULT '#6366f1',
    sort_order  INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL,
    -- Commit-identity override, both null = use the global identity (WS-008). Added by
    -- AddGitIdentityToWorkspaces for pre-existing databases.
    git_name    TEXT,
    git_email   TEXT
);
`

### `projects`

`sql
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
`

`github_owner`/`github_repo` and `github_host` were added by later migrations (see
Migrations) — they appear here already merged into the batch, which is the current shape a
fresh database gets directly.

### `review_contexts`

`sql
-- Review context is scoped per WORKSPACE (see migrate_review_contexts_to_workspace
-- below for the project_id -> workspace_id column migration for pre-existing rows).
CREATE TABLE IF NOT EXISTS review_contexts (
    id           TEXT PRIMARY KEY,
    workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
    name         TEXT NOT NULL,
    content      TEXT NOT NULL DEFAULT '',
    enabled      INTEGER NOT NULL DEFAULT 1,
    created_at   TEXT NOT NULL
);
`

### `workspace_prompts`

`sql
-- Per-workspace, provider-independent prompt overrides keyed by `kind`
-- (`review_standard` = the PR review methodology, `pr_description` = the PR-description
-- generator). One row per (workspace, kind), seeded with the built-in default on creation
-- and backfilled for pre-existing workspaces (see backfill_workspace_prompts). Empty
-- content means "use the built-in default", so resetting is just a blank save. These are
-- deliberately NOT per-provider — the same text applies to whatever engine a task routes to.
CREATE TABLE IF NOT EXISTS workspace_prompts (
    workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
    kind         TEXT NOT NULL,
    content      TEXT NOT NULL DEFAULT '',
    updated_at   TEXT NOT NULL,
    PRIMARY KEY (workspace_id, kind)
);
`

No surrogate `id`; the natural key is the pair itself.

### `review_runs`

`sql
-- Durable memory of every completed PR review — one row per run, kept in the DB (not on
-- disk) so it moves/backs up with codeflow.db. Holds the rendered review, the exact diff
-- reviewed, run metadata and the parsed findings (JSON), which is what a re-review reads
-- back to reconcile new/still-present/resolved. Timestamped rows, never overwritten, so the
-- code a finding referred to stays recoverable even after the branch is gone.
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
`

`workspace_id` here has **no `REFERENCES` clause** — see `STORE-010`.

### `workspace_skills`

`sql
-- Skills installed via `npx skills add`, scoped per workspace; synced into whichever
-- project is actually being reviewed at review time (Claude Code only discovers
-- skills from a project's own .claude/skills, there's no cross-directory flag for it).
CREATE TABLE IF NOT EXISTS workspace_skills (
    id           TEXT PRIMARY KEY,
    workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
    skill_name   TEXT NOT NULL,
    source_repo  TEXT NOT NULL,
    enabled      INTEGER NOT NULL DEFAULT 1,
    installed_at TEXT NOT NULL
);
`

### `workspace_agents`

`sql
-- User-defined SDD/Harness agents (roles) per workspace — name + role + model + prompt.
-- Deliberately empty by default (no preset roster); the user creates their own.
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
`

### `workspace_mcps`

`sql
-- MCP servers configured per workspace; written out as a --mcp-config JSON file for
-- headless `claude -p` invocations against any project in the workspace.
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
`

### `app_settings`

`sql
CREATE TABLE IF NOT EXISTS app_settings (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
`

Plain key/value; no record models a row (see Models).

### `activity_log`

`sql
-- Persisted record of every AI chat question/answer turn, scoped per project — the
-- chat itself (chatStore) only lives in memory for the session, so without this a
-- restart silently loses everything that was ever asked. `session_id` is the Claude
-- Code session these turns can be `--resume`d under; rows sharing one `session_id`
-- reconstruct a full conversation, letting the UI list/reopen/continue past chats
-- instead of only ever having one ongoing conversation per project.
CREATE TABLE IF NOT EXISTS activity_log (
    id          TEXT PRIMARY KEY,
    project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    session_id  TEXT,
    question    TEXT NOT NULL,
    answer      TEXT NOT NULL,
    created_at  TEXT NOT NULL
);
`

Shown here in its original shape; `session_id`, `response_time_ms`, `is_error`,
`engine_session_id`, `trace`, `provider`, `model` and `engine_version` are all later
`ALTER TABLE ADD COLUMN` additions layered on afterward (see Migrations) — a fresh database
gets this base batch first and then every `add_*_to_activity_log` step immediately after,
in the same `run()` call, so in practice a fresh install ends up with all of them too.

### `job_history`

`sql
-- Persisted record of every finished PR review / pre-commit analysis run — like
-- `activity_log` above, `jobsStore` on the frontend only lives in memory for the
-- session, so without this a restart silently loses every past review/analysis
-- result. Only successful/errored *completed* runs are recorded (there's nothing
-- meaningful to reopen from a run that was still in flight when the app closed).
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
`

### `conversation_titles`

`sql
-- A user-given rename for a chat conversation (`activity_log` rows grouped by
-- `session_id`) — conversations don't otherwise have a row of their own to attach a
-- title to, since they're just a GROUP BY over individual question/answer turns.
CREATE TABLE IF NOT EXISTS conversation_titles (
    session_id  TEXT PRIMARY KEY,
    project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    title       TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);
`

No `created_at` — only `updated_at`. No record models a row (see Models).

### API client tables

`sql
-- ===================== API client (per workspace) =====================
-- Scoped to a WORKSPACE, not to a project: a collection describes a *service*, and the
-- several repos of one workspace (frontend, backend, infra) normally talk to the same
-- one — scoping per repo would mean re-creating the same collection in each. Scoping per
-- workspace also keeps environments and the cookie jar from leaking a staging session
-- from one client's workspace into another's.
--
-- Only the roots carry `workspace_id`: folders and requests reach it through their
-- collection, so there is exactly one place a row's workspace can be wrong.
--
-- The editable content of a request lives in one `spec` JSON blob rather than in
-- columns, so adding a protocol, an auth scheme or a body mode never needs a migration.

CREATE TABLE IF NOT EXISTS api_collections (
    id           TEXT PRIMARY KEY,
    workspace_id TEXT NOT NULL DEFAULT '' REFERENCES workspaces(id) ON DELETE CASCADE,
    name        TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    -- JSON AuthConfig; '' = nothing configured (children fall through to "none").
    auth        TEXT NOT NULL DEFAULT '',
    pre_script  TEXT NOT NULL DEFAULT '',
    post_script TEXT NOT NULL DEFAULT '',
    -- JSON ApiVariable[] — collection-scoped variables.
    variables   TEXT NOT NULL DEFAULT '[]',
    sort_order  INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);

-- Folders nest arbitrarily (`parent_id` self-references); NULL means "directly under the
-- collection". Kept as a separate table from requests so a folder can carry its own
-- auth/scripts, which requests inherit.
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
    -- JSON ApiRequestSpec: params, headers, body, auth, scripts, protocol settings.
    spec          TEXT NOT NULL DEFAULT '{}',
    sort_order    INTEGER NOT NULL DEFAULT 0,
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_api_requests_parent
    ON api_requests (collection_id, folder_id, sort_order);

-- Environments are global too. Exactly one row has `is_global = 1`: the "Globals"
-- pseudo-environment, which is always in scope and can't be deleted or switched away
-- from (see `ensure_globals_environment`).
CREATE TABLE IF NOT EXISTS api_environments (
    id           TEXT PRIMARY KEY,
    workspace_id TEXT NOT NULL DEFAULT '' REFERENCES workspaces(id) ON DELETE CASCADE,
    name        TEXT NOT NULL,
    -- JSON ApiVariable[] — initial vs current value, secret flag, enabled flag.
    variables   TEXT NOT NULL DEFAULT '[]',
    is_global   INTEGER NOT NULL DEFAULT 0,
    sort_order  INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL
);

-- Every send, whether or not it came from a saved request (`request_id` is NULL for
-- ad-hoc sends). `snapshot` holds the full request spec + response so an old entry can
-- be replayed or restored into the builder exactly as it was.
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

-- The cookie jar. Persisted rather than kept in the reqwest client because the client is
-- rebuilt per request (per-request SSL/proxy/redirect overrides make a shared client
-- impossible), so nothing in the transport layer can hold jar state across sends.
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
`

That is 18 tables total (`workspaces`, `projects`, `review_contexts`, `workspace_prompts`,
`review_runs`, `workspace_skills`, `workspace_agents`, `workspace_mcps`, `app_settings`,
`activity_log`, `job_history`, `conversation_titles`, `api_collections`, `api_folders`,
`api_requests`, `api_environments`, `api_history`, `api_cookies`) and 7 indexes
(`idx_review_runs_pr`, `idx_activity_log_project`, `idx_job_history_project`,
`idx_api_folders_parent`, `idx_api_requests_parent`,
`idx_api_history_time`, `idx_api_cookies_key` — the last is `UNIQUE`).

`idx_activity_log_project` and `idx_job_history_project` are **not** 1.7.2's. Both tables are
append-only and never purged, and every read of either filters by `project_id` and orders by
`created_at`, so without them the chat history and the job list are full scans that grow for the
life of the install. Added as `DIVERGENCE-STORE-e`; `CREATE INDEX IF NOT EXISTS` in the same
idempotent batch means an existing database picks them up on its next launch with no migration
step.

Every boolean-shaped column (`enabled`, `is_global`, `secure`, `http_only`, `is_error`) is
declared `INTEGER`; `rusqlite` maps the sidecar `bool` to/from SQLite `0`/`1` on these columns
without any explicit conversion code in this codebase — the port's ORM/mapper needs the
equivalent implicit `bool <-> INTEGER` conversion.

## Migrations

`Migrations.Run`(&Connection)` (`src/CodeFlow.App/Storage/Migrations.cs`) is the **entire** migration system.
There is no version table anywhere — no `schema_migrations`, no `PRAGMA user_version` usage.
Every step decides for itself, from the live schema, whether it already ran:

- **Table-level guard**: `table_exists(conn, name)` (`:439-448`) — `SELECT 1 FROM
  sqlite_master WHERE type = 'table' AND name = ?1`.
- **Column-level guard**: `has_column(conn, table, column)` (`:468-478`) — `PRAGMA
  table_info(table)`, scans for the column name.
- **Statement-level guard**: `CREATE TABLE IF NOT EXISTS` / `CREATE INDEX IF NOT EXISTS` are
  self-guarding; no separate check is needed.

Because every guard re-derives its answer from the schema itself on each call, the procedure
is safe to run on every process launch (it does, unconditionally) and safe to re-run after a
crash **for every step except one** — `migrate_api_tables_finish`, flagged `BUG-STORE-a`
below.

### Call order (`run()`, `:3-313`)

1. `migrate_api_tables_begin(conn)` — **must run before** the schema batch. It renames the
   four pre-workspace `api_*` root tables aside so the batch recreates them in their current
   (workspace-scoped) shape. Running it after the batch would find the tables already in
   their current shape and do nothing, defeating the whole migration.
2. The schema batch (`conn.execute_batch(...)`, all 18 `CREATE TABLE IF NOT EXISTS` plus the
   7 indexes) — see Schema.
3. `migrate_api_tables_finish(conn)` — second half of step 1; copies the pre-workspace rows
   into the freshly recreated tables.
4. `ensure_globals_environment(conn)` — seeds a `Globals` `api_environments` row for any
   workspace that lacks one.
5. `migrate_review_contexts_to_workspace(conn)` — re-points pre-existing `review_contexts`
   rows from `project_id` to `workspace_id` and drops the old column.
6. `migrate_md_files_into_contexts(conn)` — folds the old `workspace_md_files` table into
   `review_contexts`.
7. `migrate_review_standards_into_prompts(conn)` — folds the old
   `workspace_review_standards` table into `workspace_prompts` under `kind =
   'review_standard'`.
8. `backfill_workspace_prompts(conn)` — seeds the built-in default text for
   `review_standard` and `pr_description` into every workspace that doesn't yet have that
   `(workspace_id, kind)` row. **Must run after step 7** — see `STORE-003`.
9. `drop_legacy_installed_skills(conn)` — unconditionally drops the old, never-used
   `installed_skills` table.
10. `add_session_id_to_activity_log`
11. `add_response_time_to_activity_log`
12. `add_is_error_to_activity_log`
13. `add_engine_session_id_to_activity_log`
14. `add_trace_to_activity_log`
15. `add_engine_meta_to_activity_log` — adds `provider`, `model`, `engine_version` together
    (loops the three column names; each is individually `has_column`-guarded).
16. `add_custom_label_to_job_history`
17. `add_github_columns_to_projects` — adds `github_owner` and `github_repo` together (each
    individually guarded).
18. `add_github_host_to_projects` — **must run after** step 17; a row with
    `github_owner`/`github_repo` set but a `NULL` `github_host` is read elsewhere as
    defaulting to `github.com`.
19. `add_enabled_to_workspace_skills`
20. `add_provider_to_workspace_agents`

Steps 10–20 are all the same shape: `if has_column(...) { return success } else { ALTER TABLE
... ADD COLUMN ... }`. Each is independently idempotent and order-independent from every
other step in that group — the two ordering constraints that matter are the ones called out
above (step 1 before the batch; step 8 after step 7; step 18 after step 17, by convention
though not enforced).

### The two-phase `api_*` FK-column-add (steps 1 + 3)

SQLite's `ALTER TABLE ... ADD COLUMN` rejects a column definition carrying a `REFERENCES`
clause while foreign keys are enabled, so a pre-workspace database cannot gain a
`workspace_id REFERENCES workspaces(id)` column on `api_collections` (etc.) via a plain
`ADD COLUMN`. The workaround, split across `migrate_api_tables_begin` and
`migrate_api_tables_finish`:

**`migrate_api_tables_begin` (`src/CodeFlow.App/Storage/Migrations.cs`)** — guarded by `needs_migration =
table_exists(api_collections) && !has_column(api_collections, workspace_id)`. When true:

`sql
PRAGMA foreign_keys = OFF;
PRAGMA legacy_alter_table = ON;
DROP INDEX IF EXISTS idx_api_history_time;
DROP INDEX IF EXISTS idx_api_cookies_key;
ALTER TABLE api_collections  RENAME TO api_collections_legacy;
ALTER TABLE api_environments RENAME TO api_environments_legacy;
ALTER TABLE api_history      RENAME TO api_history_legacy;
ALTER TABLE api_cookies      RENAME TO api_cookies_legacy;
PRAGMA legacy_alter_table = OFF;
`

`PRAGMA legacy_alter_table = ON` reverts `ALTER TABLE ... RENAME TO` to its pre-3.25 SQLite
behaviour: it does **not** rewrite `REFERENCES` clauses in other tables that point at the
table being renamed. Without it, SQLite would automatically rewrite `api_folders`' and
`api_requests`' `REFERENCES api_collections(id)` clauses to `REFERENCES
api_collections_legacy(id)` as a side effect of the rename — and once `api_collections_legacy`
is dropped in the finish step, those two tables would be left referencing a table that no
longer exists. With the pragma on, the rename leaves `api_folders`/`api_requests` still
declaring `REFERENCES api_collections(id)`, so once the schema batch (step 2) recreates a real
`api_collections` table, their foreign keys resolve to it automatically with zero further
action. The indexes are dropped rather than carried across because a renamed table keeps
indexes registered under their original names, which would collide with the schema batch
recreating indexes of the same name on the new tables.

**`migrate_api_tables_finish` (`src/CodeFlow.App/Storage/Migrations.cs`)** — runs after the schema batch has
recreated the four tables in their current (workspace-scoped) shape:

1. If `api_collections_legacy` doesn't exist, return immediately (nothing to finish).
2. `SELECT id FROM workspaces ORDER BY sort_order, created_at LIMIT 1` — the **oldest**
   workspace by the app's own sort order, tie-broken by creation time.
3. If there is no workspace at all, return immediately **without touching the legacy
   tables** — see `STORE-006`.
4. `PRAGMA foreign_keys = OFF;`
5. Four `INSERT INTO <table> (...) SELECT ..., <target_workspace_id> AS workspace_id, ...
   FROM <table>_legacy` statements — collections, then environments (`WHERE is_global = 0`,
   deliberately excluding the legacy global row — see `STORE-008`), then history, then
   cookies.
6. `DROP TABLE` all four `*_legacy` tables; `PRAGMA foreign_keys = ON;`.

## Models

19 structs in `src/CodeFlow.App/Workspaces/WorkspaceModels.cs`, all `. None
carries ` or ` — every JSON key is the the sidecar field's
literal `snake_case` name. `NewProject`'s six optional link fields (`ado_org`, `ado_project`,
`ado_repo_id`, `github_owner`, `github_repo`, `github_host`) carry `, so a
frontend payload may omit them entirely (deserializing as `None`) instead of having to send
an explicit `null`.

| Struct | Fields (type) | Maps to |
|---|---|---|
| `Workspace` | `id: string`, `name: string`, `icon: string`, `color: string`, `sort_order: long`, `created_at: string`, `git_name: string?`, `git_email: string?` | `workspaces` row |
| `Project` | `id: string`, `workspace_id: string`, `name: string`, `local_path: string`, `remote_url: string?`, `color: string`, `icon: string`, `ado_org/ado_project/ado_repo_id: string?`, `github_owner/github_repo/github_host: string?`, `sort_order: long`, `created_at: string` | `projects` row |
| `NewProject` | Same fields as `Project` minus `id`/`sort_order`/`created_at` | Input DTO for `create_project` |
| `ReviewContext` | `id`, `workspace_id`, `name`, `content: string`, `enabled: bool`, `created_at: string` | `review_contexts` row |
| `ReviewRunSummary` | `id`, `project_id`, `project_name: string`, `pr_id: long`, `pr_title: string`, `iter: long`, `level: string`, `findings_count: long`, `created_at: string` | Projection: `review_runs` LEFT JOIN `projects`, plus `json_extract`/`json_array_length` — not a stored shape |
| `ReviewRunDetail` | `id`, `project_id`, `pr_id: long`, `iter: long`, `level: string`, `meta: string`, `review_md: string`, `diff: string`, `findings: string`, `created_at: string` | Projection: `review_runs` row minus `workspace_id` |
| `WorkspaceAgent` | `id`, `workspace_id`, `name`, `role`, `provider`, `model`, `prompt: string`, `enabled: bool`, `sort_order: long`, `created_at: string` | `workspace_agents` row |
| `WorkspaceSkill` | `id`, `workspace_id`, `skill_name`, `source_repo: string`, `enabled: bool`, `installed_at: string` | `workspace_skills` row |
| `ActivityLogEntry` | `id`, `project_id`, `session_id: string?`, `engine_session_id: string?`, `question`, `answer: string`, `trace: string?`, `created_at: string`, `response_time_ms: long?`, `provider/model/engine_version: string?`, `is_error: bool` | `activity_log` row |
| `JobHistoryEntry` | `id`, `project_id`, `kind`, `label: string`, `custom_label: string?`, `status: string`, `result/error: string?`, `meta: string`, `created_at: string` | `job_history` row |
| `ChatConversationSummary` | `session_id`, `project_id`, `title`, `created_at`, `updated_at: string`, `turn_count: long` | Derived: grouped `activity_log` (+ `conversation_titles` for the title override) — not a stored shape |
| `WorkspaceMcp` | `id`, `workspace_id`, `name`, `command`, `args: string` (space-separated), `env: string` (`KEY=value` lines), `enabled: bool`, `created_at: string` | `workspace_mcps` row |
| `ApiCollection` | `id`, `workspace_id`, `name`, `description`, `auth: string` (JSON `AuthConfig`), `pre_script`, `post_script: string`, `variables: string` (JSON `ApiVariable[]`), `sort_order: long`, `created_at`, `updated_at: string` | `api_collections` row |
| `ApiFolder` | `id`, `collection_id`, `parent_id: string?`, `name`, `description`, `auth`, `pre_script`, `post_script: string`, `sort_order: long`, `created_at: string` | `api_folders` row |
| `ApiRequestRow` | `id`, `collection_id`, `folder_id: string?`, `name`, `protocol`, `method`, `url: string`, `spec: string` (JSON `ApiRequestSpec`), `sort_order: long`, `created_at`, `updated_at: string` | `api_requests` row |
| `ApiTree` | `collections: IReadOnlyList<ApiCollection>`, `folders: IReadOnlyList<ApiFolder>`, `requests: IReadOnlyList<ApiRequestRow>` | Composite response of `load_tree` — not a stored shape |
| `ApiEnvironment` | `id`, `workspace_id`, `name`, `variables: string` (JSON `ApiVariable[]`), `is_global: bool`, `sort_order: long`, `created_at: string` | `api_environments` row |
| `ApiHistoryEntry` | `id`, `workspace_id`, `request_id: string?`, `name`, `protocol`, `method`, `url: string`, `status/duration_ms/size_bytes: long?`, `snapshot: string` (JSON `{request, response}`), `created_at: string` | `api_history` row |
| `ApiCookie` | `id`, `workspace_id`, `domain`, `path`, `name`, `value: string`, `secure: bool`, `http_only: bool`, `expires: string?`, `updated_at: string` | `api_cookies` row |

`src/CodeFlow.App/Activity/ActivityLogStore.cs` additionally defines `TurnMeta<'a>` (`:653-662`, `) — a
non-model, non-serialized parameter-grouping struct (`provider`, `model`, `engine_version:
string?`, `response_time_ms: long?`) that `add_activity_log` takes by value to keep
its own signature short. It is not one of the 19 counted models.

Three of the 18 tables have **no corresponding record at all**: `app_settings`
(`get_setting`/`set_setting` pass a bare `string`), `workspace_prompts`
(`get_workspace_prompt`/`set_workspace_prompt` likewise), and `conversation_titles` (read only
through a private `HashMap<string, string>` helper, `conversation_titles()` at
`:805-809`).

## Query semantics

### Timestamps and ordering

`now()` (`src/CodeFlow.App/Activity/ActivityLogStore.cs`) is `Utc.now().to_rfc3339()` and is the source of every
`created_at`/`updated_at`/`installed_at` value written anywhere in these two files. Every
list query that orders by time does so with a plain SQL `ORDER BY` over this TEXT column —
there is no separate numeric/epoch timestamp column anywhere in the schema.

### Deletes are all hard deletes

No table in this schema has a `deleted_at`/`is_deleted` column, and no query in
`src/CodeFlow.App/Activity/ActivityLogStore.cs` or `src/CodeFlow.App/ApiClient/ApiTreeStore.cs` does anything other than `DELETE FROM ... WHERE ...`.
Row removal is always physical and immediate.

### Cascade map

Deleting a **workspace** cascades (via `ON DELETE CASCADE`) to: `projects`,
`review_contexts`, `workspace_prompts`, `workspace_skills`, `workspace_agents`,
`workspace_mcps`, `api_collections` (→ `api_folders` → nested `api_folders`/`api_requests`),
`api_environments`, `api_history`, `api_cookies`. `review_runs` is **not** in this list
directly — see `STORE-010`.

Deleting a **project** cascades to: `review_runs`, `activity_log`, `job_history`,
`conversation_titles`.

Deleting an **api_collection** cascades to its `api_folders` and `api_requests`; deleting an
`api_folder` cascades to its child `api_folders` and the `api_requests` under it.

### `move_node` (`src/CodeFlow.App/ApiClient/ApiTreeStore.cs`)

Reparents one `api_folders` or `api_requests` row and renumbers its new siblings so
`sort_order` stays dense `0..n`:

1. If moving a folder, `is_within_subtree` (`:530-547`) walks up from the destination
   `parent_id` toward the root, capped at `MAX_FOLDER_DEPTH = 256` iterations; if it reaches
   the folder being moved, the move is rejected (`"A folder cannot be moved inside
   itself"`). If the walk exceeds 256 hops without terminating (a corrupt `parent_id` chain),
   it also returns `true` (blocks the move) rather than looping forever — a deliberate
   fail-closed default, not a bug.
2. The node's current workspace and the destination collection's workspace are compared
   (both resolved by joining up to `api_collections.workspace_id`); a mismatch is rejected
   (`"A node cannot be moved to a collection in another workspace"`).
3. The node's `collection_id`/parent column is updated.
4. If moving a folder, `carry_subtree_to_collection` (`:571-586`) runs a `WITH RECURSIVE`
   CTE to rewrite `collection_id` on every folder and request beneath it, so descendants stay
   reachable from the tree under the new collection.
5. The destination's siblings (same `collection_id` + parent, excluding the moved id) are
   read in current order, the moved id is inserted at `index` (clamped to `[0, len]`), and
   every sibling is rewritten with a fresh dense `sort_order`.

All of this runs inside one `unchecked_transaction()`. Errors are `void`, not
`SqliteException` — the cycle/workspace checks are caller-mistake guards, not database
failures.

### `denormalize()` (`src/CodeFlow.App/ApiClient/ApiTreeStore.cs`)

Extracts `method`/`url` out of a request's `spec` JSON to keep the denormalized tree columns
honest. If `spec` fails to parse as JSON, it's treated as `JsonValueKind.Null`; a missing or
non-string `method`/`url` key reads as `""`; an empty `method` after that still defaults to
`"GET"` (`url` has no such fallback — it can end up `""`).

### `duplicate_collection` / `duplicate_request` / `duplicate_environment`

All three: fresh UUID, `name` becomes `"{original name} copy"`, fresh `created_at`/
`updated_at`, and the copy's `sort_order` is drawn from `next_*_order` in the *same*
workspace/collection/parent scope as the original (so it lands last among its new siblings).
`duplicate_collection` (`:246-322`) additionally deep-copies every folder and request under
the source: folders are inserted in two passes (all folders first with `parent_id = NULL`,
then a second pass sets the remapped `parent_id`) because a child folder can appear before its
parent in the source order, and inserting it with an already-remapped, not-yet-existing
parent id would violate the self-referencing foreign key.

### `add_history` (`src/CodeFlow.App/ApiClient/ApiTreeStore.cs`)

Insert is `ON CONFLICT(id) DO NOTHING` (idempotent by id) and honors a caller-supplied
`created_at` when non-empty (so a replayed/re-imported entry keeps the instant the request
actually ran). Every insert is followed, in the same transaction, by trimming that
**workspace's** history back to `HISTORY_HARD_CAP = 2000` rows — a hard backstop deliberately
set well above the settings UI's own `historyLimit` (default 500, which only controls how many
rows are *shown*).

### `upsert_cookie` (`src/CodeFlow.App/ApiClient/ApiTreeStore.cs`)

Keyed on the natural key `(workspace_id, domain, path, name)` via `ON CONFLICT(...) DO
UPDATE`, mirroring how `Set-Cookie` identifies a cookie on the wire — never accumulates
duplicates for the same key, and never crosses from one workspace's jar into another's.

### `list_chat_conversations` (`src/CodeFlow.App/Activity/ActivityLogStore.cs`)

Reads every `activity_log` row for the project **where `session_id IS NOT NULL`**
(`all_activity_log_entries`, `:734-741`) — rows written before session tracking existed are
silently excluded from every chat-history feature, permanently. Groups by `session_id`
(the app-minted **conversation** id, not `engine_session_id`); a conversation's `title` is its
**first** turn's `question` (insertion order), `updated_at` is its **last** turn's
`created_at`, `turn_count` is the number of turns. When `search` is given, only conversations
where *any* turn's `question` or `answer` (case-folded) contains the needle survive. Any row
in `conversation_titles` for the project overrides the derived title after grouping. Final
result order: `updated_at` descending.

### `last_turn_provider` (`src/CodeFlow.App/Activity/ActivityLogStore.cs`)

`SELECT provider FROM activity_log WHERE project_id = ?1 AND session_id = ?2 AND provider IS
NOT NULL ORDER BY created_at DESC LIMIT 1` — rows predating provider tracking have `provider
= NULL` and are filtered out entirely by the `IS NOT NULL` clause, so a conversation whose
every turn predates that column returns `None`, indistinguishable from a conversation that
doesn't exist at all.

### `get_conversation_messages` (`src/CodeFlow.App/Activity/ActivityLogStore.cs`)

Orders `ASC` by `created_at` (oldest first) so the frontend can flatten turns directly into
`[user, assistant, user, assistant, ...]`.

### `workspace_prompt_default` / `get_workspace_prompt` / `set_workspace_prompt`

`get_workspace_prompt` (`:204-216`) never returns an empty string: a stored row whose
`content`, once trimmed, is empty is treated exactly like a missing row, and both fall back to
`workspace_prompt_default(kind)`. `set_workspace_prompt` passing `""` is therefore how the UI
implements "restore default" — it's a normal upsert, not a delete. `workspace_prompt_default`
(`:190-199`) returns the built-in review methodology text for `"pr_description"`'s counterpart
default and for anything unrecognized, `""` for `"sdd_stages"` (a text store with no built-in
default — the guide itself is static frontend content, never persisted), and the review
methodology otherwise.

### `add_review_run` (`src/CodeFlow.App/Activity/ActivityLogStore.cs`)

`ON CONFLICT(id) DO NOTHING` — idempotent by id, reusing the calling job's own id so the job
and its durable review-run row share identity. Once written, a run's `review_md`, `diff`,
`meta` etc. are never updated by any function in these files; the only mutation path is
`set_review_run_findings`, which overwrites `findings` alone.

### `unlink_project` (`src/CodeFlow.App/Activity/ActivityLogStore.cs`)

Clears all six link columns (`ado_org`, `ado_project`, `ado_repo_id`, `github_owner`,
`github_repo`, `github_host`) unconditionally, regardless of which host, if any, was actually
set. There is no database constraint enforcing "at most one host" — `link_project_ado`
(`:351-363`) and `link_project_github` (`:365-377`) each only ever touch their own three
columns, so nothing in this layer prevents a project ending up with both an ADO and a GitHub
link simultaneously if a caller invoked both.

## Rules

### STORE-001 Single mutex-guarded connection, panic-on-bootstrap-failure
**Implementation**: `src/CodeFlow.App/Storage/Database.cs`
**Behaviour**: `init()` calls `AppPaths`().expect(...)`, then
`Connection.open(`AppPaths`())`, then `Migrations.Run`(&conn)`, then wraps the result in
`Db(Mutex.new(conn))`. This is the only `Connection` the app ever opens; every query function
takes `&Connection` and every caller reaches it through the same mutex.
**Inputs / outputs**: No inputs; returns `Microsoft.Data.Sqlite`<Db>`.
**Edge cases**: A directory-creation failure calls `.expect(...)`, which **panics the
process** — it is not surfaced as a normal error path. A connection-open or migration failure
propagates as `SqliteException` through `?`.
**Frontend dependency**: none — runs before any the shell state, and therefore before any command,
exists.
**Markers**: none

### STORE-002 No migration version table; every step re-derives its own "already ran" state
**Implementation**: `src/CodeFlow.App/Storage/Migrations.cs`
**Behaviour**: `Migrations.Run` executes unconditionally on every launch. Table-adding steps
check `sqlite_master` (`table_exists`); column-adding steps check `PRAGMA table_info`
(`has_column`); the schema batch itself uses `CREATE TABLE IF NOT EXISTS` / `CREATE INDEX IF
NOT EXISTS`. There is no `schema_migrations` table and no `PRAGMA user_version` read or
written anywhere in this codebase.
**Inputs / outputs**: n/a
**Edge cases**: A half-run of the plain `CREATE TABLE IF NOT EXISTS` batch (process killed
mid-batch) is safe to resume — each statement is independently idempotent, so the next launch
simply creates whatever wasn't created yet. The one step that is **not** safe to resume this
way is `migrate_api_tables_finish` (`STORE-007`).
**Frontend dependency**: none
**Markers**: none

### STORE-003 Two migration-ordering dependencies
**Implementation**: `src/CodeFlow.App/Storage/Migrations.cs`
**Behaviour**: (1) `migrate_api_tables_begin` must run before the schema batch — it relies on
finding the *old*-shaped `api_collections` (no `workspace_id` column) to decide whether to
rename it aside; running after the batch would always see the new shape and never trigger. (2)
`migrate_review_standards_into_prompts` must run before `backfill_workspace_prompts`. Both
write into `workspace_prompts` keyed on `(workspace_id, 'review_standard')`; the standards
migration uses `INSERT OR IGNORE`, and the backfill only inserts where the row doesn't already
exist. If the backfill ran first, it would seed the built-in default into every workspace that
hadn't been migrated yet, and the subsequent `INSERT OR IGNORE` from the real
`workspace_review_standards` content would then find that key already occupied and silently
discard the user's actual saved standard — permanently, since the source table is dropped in
the same step.
**Inputs / outputs**: n/a
**Edge cases**: The actual call order in `run()` gets both right (`migrate_api_tables_begin`
first; `migrate_review_standards_into_prompts` before `backfill_workspace_prompts`). This rule
exists to make the dependency explicit for the port, where nothing enforces the order except
call-site discipline.
**Frontend dependency**: none
**Markers**: none

### STORE-004 Two-phase FK-column-add for the four `api_*` roots
**Implementation**: `src/CodeFlow.App/Storage/Migrations.cs`
**Behaviour**: See "Migrations" above for the full sequence. In summary: rename the four
tables aside under `PRAGMA legacy_alter_table = ON` (so children's `REFERENCES` clauses are
left pointing at the *original* name), let the schema batch recreate them in their current
(workspace-scoped) shape, then copy every legacy row across with a resolved `workspace_id`
and drop the legacy tables.
**Inputs / outputs**: n/a
**Edge cases**: `PRAGMA foreign_keys` is turned `OFF` for the whole rename-and-copy operation
and explicitly turned back `ON` at the end of `migrate_api_tables_finish` — a reader must not
assume it stays on throughout `run()`.
**Frontend dependency**: none
**Markers**: none

### STORE-005 `PRAGMA legacy_alter_table` is what keeps `api_folders`/`api_requests` intact across the rename
**Implementation**: `src/CodeFlow.App/Storage/Migrations.cs`
**Behaviour**: Without `legacy_alter_table = ON`, SQLite's `ALTER TABLE ... RENAME TO` (3.25+)
automatically rewrites `REFERENCES` clauses in *other* tables that pointed at the renamed
table. Turning it on suppresses that rewrite, so `api_folders`/`api_requests` keep declaring
`REFERENCES api_collections(id)` (not `api_collections_legacy(id)`) through the rename, and
resolve correctly once the schema batch recreates a real `api_collections`.
**Inputs / outputs**: n/a
**Edge cases**: This is the exact behaviour the extracted case
`migrating_a_pre_workspace_database_keeps_every_row_and_reparents_it` asserts by reading
`sqlite_master.sql` for `api_folders` afterward and checking it contains
`REFERENCES api_collections(id)` and not the substring `"legacy"`.
**Frontend dependency**: none
**Markers**: none

### STORE-006 `migrate_api_tables_finish` defers indefinitely when no workspace exists
**Implementation**: `src/CodeFlow.App/Storage/Migrations.cs`
**Behaviour**: If `SELECT id FROM workspaces ORDER BY sort_order, created_at LIMIT 1` returns
no row, the function returns immediately, leaving the four `*_legacy` tables (and their rows)
untouched. The very next launch after a workspace is created finds the legacy tables still
present and finishes the copy then.
**Inputs / outputs**: n/a
**Edge cases**: A database with pre-workspace API data and zero workspaces stays in this
half-migrated state (live schema recreated and empty, legacy tables intact) across arbitrarily
many launches, until a workspace is created.
**Frontend dependency**: none
**Markers**: none

### STORE-007 `migrate_api_tables_finish` is not safe to resume after a crash mid-copy
**Implementation**: `src/CodeFlow.App/Storage/Migrations.cs`
**Behaviour**: The four `INSERT INTO ... SELECT ...` copy statements (collections,
environments, history, cookies) run as plain sequential `conn.execute` calls with **no
enclosing transaction** and **no `ON CONFLICT` clause** — unlike `add_review_run` (`ON
CONFLICT(id) DO NOTHING`) or the several `unchecked_transaction()`-wrapped multi-statement
functions elsewhere in `src/CodeFlow.App/ApiClient/ApiTreeStore.cs` (`duplicate_collection`, `move_node`,
`reorder_collections`, `add_history`, `duplicate_request`, `duplicate_environment`). If the
process is killed between any two of the four `INSERT` statements, the rows already copied are
already committed (SQLite autocommits each statement individually outside an explicit
transaction).
**Inputs / outputs**: n/a
**Edge cases**: On the next launch, `migrate_api_tables_begin`'s guard is now false (the live
`api_collections` already has `workspace_id`), so it does not re-rename anything, and
`migrate_api_tables_finish` runs again because `api_collections_legacy` still exists. It
re-runs the same four `INSERT ... SELECT` statements from scratch — including the one(s) that
already succeeded before the crash — and the re-insert of an already-copied row fails with a
primary-key (`id`) collision, aborting `run()` with an error on every subsequent launch. The
suspected-correct behaviour is either wrapping the four copies and the trailing `DROP TABLE`s
in one transaction (all-or-nothing), or making the four `INSERT`s idempotent the same way
`add_review_run` is (`ON CONFLICT(id) DO NOTHING`). Ported as-is — not fixed.
**Frontend dependency**: none
**Markers**: `BUG-STORE-a`

### STORE-008 `ensure_globals_environment` is keyed on `is_global`, not a fixed id
**Implementation**: `src/CodeFlow.App/Storage/Migrations.cs`
**Behaviour**: `INSERT INTO api_environments (...) SELECT ..., w.id, 'Globals', '[]', 1, -1,
?1 FROM workspaces w WHERE NOT EXISTS (SELECT 1 FROM api_environments e WHERE e.workspace_id =
w.id AND e.is_global = 1)`. Runs every launch; a workspace that already has any row with
`is_global = 1` (even if the user renamed it) is skipped, so renaming "Globals" never causes a
duplicate to be seeded on the next launch. New rows are seeded with `sort_order = -1`, which is
why `list_environments`'s `ORDER BY sort_order, created_at` always puts Globals first.
**Inputs / outputs**: n/a
**Edge cases**: In the workspace-migration flow, the pre-workspace Globals row
(`e-glob` in the legacy schema) is explicitly excluded from the copy in
`migrate_api_tables_finish` (`WHERE is_global = 0`) — the target workspace gets a *fresh*
Globals row from this function instead of the carried-over legacy one, so there is never a
double Globals after a migration.
**Frontend dependency**: none
**Markers**: none

### STORE-009 Hard-delete-only; no soft-delete column anywhere
**Implementation**: `src/CodeFlow.App/Activity/ActivityLogStore.cs`, `src/CodeFlow.App/ApiClient/ApiTreeStore.cs` (every `delete_*`/`DELETE FROM` call site)
**Behaviour**: Every deletion in both files is a physical `DELETE FROM ... WHERE ...`. No
table carries a `deleted_at` or `is_deleted` column.
**Inputs / outputs**: n/a
**Edge cases**: Combined with the FK cascade map (see "Query semantics"), deleting a workspace
or project is a genuinely destructive, unrecoverable operation for everything scoped beneath
it except `review_runs.workspace_id`-only orphaning — see `STORE-010`.
**Frontend dependency**: `commands/*.rs` (see `01-ipc-surface.md`) for every `delete_*`
command.
**Markers**: none

### STORE-010 `review_runs.workspace_id` is a write-time copy with no FK and no upkeep
**Implementation**: `src/CodeFlow.App/Storage/Migrations.cs`; `src/CodeFlow.App/Activity/ActivityLogStore.cs`;
`src/CodeFlow.App/Activity/ActivityLogStore.cs`
**Behaviour**: `review_runs.workspace_id` is declared `TEXT NOT NULL` with **no `REFERENCES`
clause** — unlike every other `workspace_id` column in the schema. It is set once, by the
caller of `add_review_run`, at insert time. `list_review_runs` and
`purge_workspace_review_runs` both filter directly on this stored column
(`WHERE r.workspace_id = ?1`); neither joins through `project_id` to the project's *current*
workspace. `move_project_to_workspace` (`:391-397`) updates only `projects.workspace_id` and
touches nothing in `review_runs`.
**Inputs / outputs**: n/a
**Edge cases**: A moved project takes its runs along: `move_project_to_workspace` updates
`review_runs.workspace_id` in the same transaction as `projects.workspace_id` (this closed
`BUG-STORE-b` — 1.7.2 left the copy stale, so the history vanished from the new workspace's
list while staying purgeable by the old one). The column remains denormalised and without an
FK; its truth now comes from that upkeep plus the `RealignReviewRunWorkspaces` migration step,
which repairs rows stranded by moves made before the fix and is naturally idempotent.
**Frontend dependency**: `commands/*.rs` (see `01-ipc-surface.md`).
**Markers**: `BUG-STORE-b` **closed**

### STORE-011 `unlink_project` clears both VCS hosts unconditionally
**Implementation**: `src/CodeFlow.App/Activity/ActivityLogStore.cs`
**Behaviour**: `link_project_ado` and `link_project_github` each write only their own three
columns; nothing clears the other host's columns as a side effect of linking one. `unlink_project`
clears all six columns in one `UPDATE`, regardless of which (if any) were set. "At most one
host" is an application-level convention, not a database constraint — no `CHECK` enforces it.
**Inputs / outputs**: n/a
**Edge cases**: Nothing in this layer prevents a project having both an ADO link and a GitHub
link simultaneously if both `link_project_ado` and `link_project_github` are called on the
same project id.
**Frontend dependency**: `src/CodeFlow.App/Workspaces/WorkspaceCommands.cs` and related (see `01-ipc-surface.md`).
**Markers**: none

### STORE-012 Workspace prompt "blank means default" and per-kind defaults
**Implementation**: `src/CodeFlow.App/Activity/ActivityLogStore.cs`
**Behaviour**: `get_workspace_prompt` returns the stored `content` only if, trimmed, it is
non-empty; otherwise it returns `workspace_prompt_default(kind)`. `set_workspace_prompt("")` is
therefore the "restore default" action, implemented as an ordinary upsert (`ON
CONFLICT(workspace_id, kind) DO UPDATE`), never a delete.
**Inputs / outputs**: `workspace_prompt_default("pr_description")` → the built-in PR
description template; `workspace_prompt_default("sdd_stages")` → `""` (no built-in default —
static frontend content, never persisted); any other `kind`, including `"review_standard"` →
the built-in review methodology text.
**Edge cases**: A `kind` with no seeded row and no override falls through to the same default
text as an explicit blank save — the two states are indistinguishable from this function's
return value alone.
**Frontend dependency**: `commands/*.rs` (see `01-ipc-surface.md`).
**Markers**: none

### STORE-013 `add_review_run` is idempotent by id; only `findings` is ever mutated afterward
**Implementation**: `src/CodeFlow.App/Activity/ActivityLogStore.cs`
**Behaviour**: `INSERT ... ON CONFLICT(id) DO NOTHING` — a second call with the same `id`
(the job's own id, reused as the run's id) is a silent no-op, not an overwrite. The only other
write path touching an existing `review_runs` row is `set_review_run_findings`, which
overwrites `findings` alone; `review_md`, `diff`, `meta` and every other column are immutable
once written.
**Inputs / outputs**: n/a
**Edge cases**: none beyond the above.
**Frontend dependency**: `commands/*.rs` (see `01-ipc-surface.md`).
**Markers**: none

### STORE-014 Chat conversations group by the app's `session_id`, never by the engine's resume token
**Implementation**: `src/CodeFlow.App/Activity/ActivityLogStore.cs`
**Behaviour**: `activity_log.session_id` is the app-minted conversation id, stable for the
conversation's whole life; `engine_session_id` is the engine CLI's own resume token, which may
be constant across unrelated conversations (Gemini/agy: one fixed sentinel for every run) or
may change mid-conversation (Claude CLI: a new id per resumed turn). `list_chat_conversations`
groups strictly by `session_id`. Title = first turn's question (insertion order); `updated_at`
= last turn's `created_at`; `turn_count` = number of turns; a `conversation_titles` row, if
present, overrides the derived title after grouping. `search`, when given, keeps a conversation
if *any* turn's question or answer (case-folded) contains the needle. Final order: `updated_at`
descending.
**Inputs / outputs**: n/a
**Edge cases**: Rows with `session_id IS NULL` (written before session tracking existed) are
excluded from `all_activity_log_entries` entirely and therefore invisible to every chat-history
feature — permanently, since there is no migration that backfills a synthetic session id for
them.
**Frontend dependency**: `commands/*.rs` (see `01-ipc-surface.md`).
**Markers**: none

### STORE-015 `last_turn_provider` cannot distinguish "no turns" from "turns predate tracking"
**Implementation**: `src/CodeFlow.App/Activity/ActivityLogStore.cs`
**Behaviour**: `SELECT provider FROM activity_log WHERE project_id = ?1 AND session_id = ?2
AND provider IS NOT NULL ORDER BY created_at DESC LIMIT 1`. A conversation whose every turn was
recorded before the `provider` column existed (all `NULL`) returns `None` from this query — the
identical result a conversation id with zero rows produces.
**Inputs / outputs**: `string?`.
**Edge cases**: The caller (outside these files, in the CLI-resume logic) is documented to
read `None` as "can't tell" and keep whatever session it already has, rather than treat it as
"definitely no provider ever ran here."
**Frontend dependency**: none directly — consumed by `session_for_provider`,
outside this document's scope.
**Markers**: none

### STORE-016 `HISTORY_HARD_CAP` is a per-workspace hard backstop, independent of the UI's display limit
**Implementation**: `src/CodeFlow.App/ApiClient/ApiTreeStore.cs`
**Behaviour**: Every `add_history` insert is followed, in the same transaction, by `DELETE FROM
api_history WHERE workspace_id = ?1 AND id NOT IN (SELECT id ... ORDER BY created_at DESC LIMIT
2000)`. The 2000 cap and the trim scope (one workspace) are independent of the settings UI's
`historyLimit` (default 500), which only controls how many rows a `list_history` call returns
for display.
**Inputs / outputs**: n/a
**Edge cases**: A workspace with heavy API traffic never evicts a rarely-used workspace's
history — the trim's `WHERE workspace_id = ?1` scopes it to the workspace that just received a
new entry.
**Frontend dependency**: `commands/*.rs` (see `01-ipc-surface.md`).
**Markers**: none

### STORE-017 `move_node`: cycle guard, cross-workspace guard, dense per-kind renumbering
**Implementation**: `src/CodeFlow.App/ApiClient/ApiTreeStore.cs`
**Behaviour**: See "Query semantics" above for the full five-step sequence. Folders and
requests are renumbered against siblings of their *own* kind only (`api_folders` and
`api_requests` have independent `sort_order` columns) because the tree UI always renders
folders above requests within a parent.
**Inputs / outputs**: `void` — human-readable error strings
(`"A folder cannot be moved inside itself"`, `"A node cannot be moved to a collection in
another workspace"`, `"Unknown {kind} {id}"`, `"Unknown collection {collection_id}"`,
`"Unknown node kind {other}"`) rather than `SqliteException`.
**Edge cases**: `is_within_subtree`'s walk is capped at `MAX_FOLDER_DEPTH = 256`; a chain that
doesn't terminate within that many hops is treated as cyclic (the move is blocked) rather than
looped forever.
**Frontend dependency**: `commands/*.rs` (see `01-ipc-surface.md`).
**Markers**: none

### STORE-018 `denormalize()` method/url extraction and defaulting
**Implementation**: `src/CodeFlow.App/ApiClient/ApiTreeStore.cs`
**Behaviour**: Parses `spec` as JSON (a failure to parse is treated as `JsonValueKind.Null`, not
propagated as an error); reads `.method`/`.url` as strings, defaulting each missing/non-string
value to `""`; then, only for `method`, an empty result is further defaulted to `"GET"`. `url`
has no equivalent fallback.
**Inputs / outputs**: `(method: string, url: string)`.
**Edge cases**: A `spec` that isn't valid JSON at all silently produces `("GET", "")` rather
than an error.
**Frontend dependency**: `commands/*.rs` (see `01-ipc-surface.md`) — feeds `create_request`'s
denormalized `method`/`url` columns.
**Markers**: none

### STORE-019 `duplicate_*` deep-copy semantics
**Implementation**: `src/CodeFlow.App/ApiClient/ApiTreeStore.cs`
**Behaviour**: `duplicate_collection`, `duplicate_request`, `duplicate_environment` each mint a
fresh UUID, name the copy `"{original} copy"`, stamp fresh `created_at`/`updated_at`, and place
the copy last among its new siblings (`next_*_order` in the same scope as the source).
`duplicate_collection` additionally deep-copies every folder and request beneath the source
collection: folders are inserted in two passes — first every folder with `parent_id = NULL`,
then a second pass sets each copy's remapped `parent_id` — because the source's folder listing
order can put a child before its own parent, and setting an already-remapped, not-yet-inserted
parent id in a single pass would violate the self-referencing FK.
**Inputs / outputs**: n/a
**Edge cases**: `duplicate_environment` is explicitly allowed on the Globals row (`is_global =
1`); the copy itself is always an ordinary environment (`is_global: false`), never a second
Globals.
**Frontend dependency**: `commands/*.rs` (see `01-ipc-surface.md`).
**Markers**: none

### STORE-020 `api_cookies` upsert is keyed on the wire identity, not the row id
**Implementation**: `src/CodeFlow.App/ApiClient/ApiTreeStore.cs`
**Behaviour**: `upsert_cookie`'s `ON CONFLICT(workspace_id, domain, path, name) DO UPDATE`
matches the natural key a `Set-Cookie` response identifies a cookie by. A second write for the
same `(workspace_id, domain, path, name)` replaces the existing row's `value`/`secure`/
`http_only`/`expires`/`updated_at` in place; it never creates a duplicate.
**Inputs / outputs**: n/a
**Edge cases**: The scope is per-workspace — the same `(domain, path, name)` in two different
workspaces are two independent rows, so a staging session cookie in one workspace's jar never
overwrites the same host's cookie in another's.
**Frontend dependency**: `commands/*.rs` (see `01-ipc-surface.md`).
**Markers**: none

### STORE-021 `delete_environment` protects the Globals row; `duplicate_environment` does not
**Implementation**: `src/CodeFlow.App/ApiClient/ApiTreeStore.cs`
**Behaviour**: `delete_environment` is `DELETE FROM api_environments WHERE id = ?1 AND
is_global = 0` — a call targeting the Globals row (`is_global = 1`) affects zero rows and is
silently a no-op; there is no separate error path for it. `duplicate_environment` has no such
guard and can be called on the Globals row, producing an ordinary (non-global) copy.
**Inputs / outputs**: n/a
**Edge cases**: n/a
**Frontend dependency**: `commands/*.rs` (see `01-ipc-surface.md`).
**Markers**: none

### STORE-022 Unreachable `DEFAULT ''` on the four `api_*` `workspace_id` columns
**Implementation**: `src/CodeFlow.App/Storage/Migrations.cs`
**Behaviour**: `api_collections`, `api_environments`, `api_history` and `api_cookies` all
declare `workspace_id TEXT NOT NULL DEFAULT '' REFERENCES workspaces(id) ON DELETE CASCADE`.
Every `INSERT` in `src/CodeFlow.App/ApiClient/ApiTreeStore.cs` and `src/CodeFlow.App/Storage/Migrations.cs` that targets these tables supplies
`workspace_id` explicitly; none of them ever relies on the column default.
**Inputs / outputs**: n/a
**Edge cases**: If the default were ever hit (an `INSERT` that omits the column), it would
attempt to satisfy the `REFERENCES workspaces(id)` constraint with the value `''`, which no
`workspaces` row ever has — the insert would fail its foreign-key check rather than silently
succeed with an unscoped row.
**Frontend dependency**: none
**Markers**: `AMBIGUOUS-STORE-a` — the source never exercises this default and gives no reason
for its presence; it's unclear whether the C# port's schema should keep a byte-identical (but
practically unsatisfiable) default, drop it, or make the column `NOT NULL` with no default. Do
not guess which was intended; this needs an explicit decision before the port's schema is
finalized.

### STORE-023 Plaintext credential JSON alongside a keychain-based secret store elsewhere in the app
**Implementation**: `src/CodeFlow.App/Storage/Migrations.cs`; `src/CodeFlow.App/Workspaces/WorkspaceModels.cs`
**Behaviour**: `api_collections.auth`, `api_folders.auth`, `api_requests.spec` (which embeds
its own per-request `auth`, per the doc comment "JSON `ApiRequestSpec`: params, headers, body,
auth, scripts, protocol settings") and `api_history.snapshot` (a full captured `{request,
response}`, including whatever headers/auth were actually sent) are all plain `TEXT` columns
holding JSON, stored and read back verbatim — the backend never parses or redacts their
contents (per `src/CodeFlow.App/ApiClient/ApiTreeStore.cs`'s own header comment: "the only thing read out of it is
`method`/`url`"). Any API key, bearer token, Basic-auth credential or custom secret header a
user configures for a request or collection is therefore persisted in plaintext inside
`codeflow.db`.
**Inputs / outputs**: n/a
**Edge cases**: n/a
**Frontend dependency**: `commands/*.rs` (API-client commands; see `01-ipc-surface.md`).
**Markers**: `DIVERGENCE-STORE-a` — the app's *own* provider/service credentials are kept in
the OS keychain elsewhere in this codebase (outside this document's scope), which makes the
plaintext-in-SQLite storage of *user-entered, third-party* API credentials here a deliberate
inconsistency rather than an oversight this document can resolve. Preserve exactly as-is in the
port; do not move it to a keychain as part of this port without an explicit product decision,
since the frontend's plain-JSON-blob editing model for `auth`/`spec` depends on it staying a
transparent, freely-editable string.

## Test coverage

| extracted case | Source | Fixture | Kind |
|---|---|---|---|
| `migrating_a_pre_workspace_database_keeps_every_row_and_reparents_it` | `src/CodeFlow.App/Storage/Migrations.cs` | `migrations.vectors.json#reparents-legacy-rows-to-oldest-workspace` | scenario |
| `migration_is_idempotent_and_a_fresh_database_needs_no_migration` | `src/CodeFlow.App/Storage/Migrations.cs` | `migrations.vectors.json#migration-is-idempotent-on-a-legacy-database` | scenario |
| `a_database_with_no_workspace_keeps_the_legacy_rows` | `src/CodeFlow.App/Storage/Migrations.cs` | `migrations.vectors.json#no-workspace-keeps-legacy-rows-until-one-exists` | scenario |
| `separate_conversations_stay_separate_even_when_the_engine_reuses_one_session_id` | `src/CodeFlow.App/Activity/ActivityLogStore.cs` | `queries.vectors.json#conversation-id-not-engine-session-id-is-the-grouping-key` | scenario |
| `one_conversation_stays_one_activity_even_when_the_engine_changes_session_id` | `src/CodeFlow.App/Activity/ActivityLogStore.cs` | `queries.vectors.json#one-conversation-survives-an-engine-session-id-change-mid-conversation` | scenario |
| `a_conversation_reports_the_provider_of_its_latest_turn` | `src/CodeFlow.App/Activity/ActivityLogStore.cs` | `queries.vectors.json#conversation-reports-latest-turns-provider` | scenario |
| `a_conversation_without_recorded_providers_reports_none` | `src/CodeFlow.App/Activity/ActivityLogStore.cs` | `queries.vectors.json#conversation-without-recorded-providers-reports-none` | scenario |
| `a_conversation_keeps_the_engine_session_of_each_turn` | `src/CodeFlow.App/Activity/ActivityLogStore.cs` | `queries.vectors.json#get-conversation-messages-tracks-the-latest-turns-engine-session` | scenario |

`src/CodeFlow.App/Storage/Database.cs`, `src/CodeFlow.App/Workspaces/WorkspaceModels.cs` and `src/CodeFlow.App/ApiClient/ApiTreeStore.cs` carry no ` functions of their own —
`src/CodeFlow.App/ApiClient/ApiTreeStore.cs`'s behaviour (tree loading, moves, duplicates, cookie/history upsert) is
exercised only indirectly, through the command layer, and has no dedicated the sidecar unit test in
this file; there is nothing to extract as a fixture for it beyond what "Query semantics" and
"Rules" already specify in prose. All 8 tests in scope are `scenario` kind, per the
convention that every test in `src/CodeFlow.App/Storage/Migrations.cs` and `src/CodeFlow.App/Activity/ActivityLogStore.cs` is scenario-kind (each
builds a migrated or seeded in-memory `Connection` first).

## Markers raised

| Marker | Summary |
|---|---|
| `BUG-STORE-a` | `migrate_api_tables_finish`'s four-table row copy runs as unwrapped, non-idempotent `INSERT` statements; a crash between them makes every subsequent launch fail with a primary-key collision instead of resuming cleanly. |
| ~~`BUG-STORE-b`~~ **CLOSED** | `review_runs.workspace_id` was a write-time denormalization with no FK and no upkeep; `move_project_to_workspace` never updated it, so a moved project's review history silently fell out of `list_review_runs` for its new workspace while remaining deletable via `purge_workspace_review_runs` scoped to its old one. Closed: the move updates it in the same transaction, and `RealignReviewRunWorkspaces` backfills databases that diverged before the fix. See `91-known-bugs.md`. |
| `AMBIGUOUS-STORE-a` | The `DEFAULT ''` on `api_collections`/`api_environments`/`api_history`/`api_cookies`.`workspace_id` can never actually satisfy its own `REFERENCES workspaces(id)` constraint and is never relied upon by any `INSERT` in these files — unclear whether the port should preserve, drop, or replace it. |
| `DIVERGENCE-STORE-a` | `api_collections.auth`, `api_folders.auth`, `api_requests.spec` and `api_history.snapshot` store user-entered API credentials as plaintext JSON in SQLite, unlike the app's own credentials which live in the OS keychain elsewhere in the codebase. Preserve as-is. |
