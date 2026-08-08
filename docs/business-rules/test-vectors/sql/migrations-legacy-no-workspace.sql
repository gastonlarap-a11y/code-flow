-- Seed artefact for STORE migration scenario:
--   a_database_with_no_workspace_keeps_the_legacy_rows
--
-- Same pre-workspace schema as migrations-legacy-pre-workspace.sql (see that file's header for
-- provenance), but seeded with a single `api_collections` row and *no* `workspaces` row at all —
-- the case `migrate_api_tables_finish` must leave untouched because
-- there is nowhere to assign the legacy rows to.
--
-- Used by fixture case id: migrations.vectors.json#no-workspace-keeps-legacy-rows-until-one-exists

PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS workspaces (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    icon        TEXT NOT NULL DEFAULT 'folder',
    color       TEXT NOT NULL DEFAULT '#6366f1',
    sort_order  INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL
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

CREATE TABLE IF NOT EXISTS review_contexts (
    id           TEXT PRIMARY KEY,
    workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
    name         TEXT NOT NULL,
    content      TEXT NOT NULL DEFAULT '',
    enabled      INTEGER NOT NULL DEFAULT 1,
    created_at   TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS workspace_prompts (
    workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
    kind         TEXT NOT NULL,
    content      TEXT NOT NULL DEFAULT '',
    updated_at   TEXT NOT NULL,
    PRIMARY KEY (workspace_id, kind)
);

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

CREATE TABLE IF NOT EXISTS conversation_titles (
    session_id  TEXT PRIMARY KEY,
    project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    title       TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);

-- ===================== API client (pre-workspace shape) =====================

CREATE TABLE IF NOT EXISTS api_collections (
    id           TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
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
    protocol      TEXT NOT NULL DEFAULT 'http',
    method        TEXT NOT NULL DEFAULT 'GET',
    url           TEXT NOT NULL DEFAULT '',
    spec          TEXT NOT NULL DEFAULT '{}',
    sort_order    INTEGER NOT NULL DEFAULT 0,
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_api_requests_parent
    ON api_requests (collection_id, folder_id, sort_order);

CREATE TABLE IF NOT EXISTS api_environments (
    id           TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    variables   TEXT NOT NULL DEFAULT '[]',
    is_global   INTEGER NOT NULL DEFAULT 0,
    sort_order  INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS api_history (
    id           TEXT PRIMARY KEY,
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
CREATE INDEX IF NOT EXISTS idx_api_history_time ON api_history (created_at DESC);

CREATE TABLE IF NOT EXISTS api_cookies (
    id         TEXT PRIMARY KEY,
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
    ON api_cookies (domain, path, name);

-- Seed rows: one collection, no workspace at all.

INSERT INTO api_collections (id, name, created_at, updated_at) VALUES ('c1', 'Kept', 't', 't');
