-- Seed artefact for STORE query scenarios (the activity-log store activity_log / chat-conversation tests).
--
-- Run migrations::run() against an empty database first (this file assumes the full current
-- schema already exists — it only inserts rows), then apply this file, then the case-specific
-- INSERT statements listed under each fixture case's "steps" in queries.vectors.json.
--
-- One workspace and one project, with fixed ids so every case is byte-reproducible (the original
-- tests instead call create_workspace/create_project, which mint random UUIDs and a wall-clock
-- created_at — replaced here with fixed values so ordering by created_at is deterministic).

INSERT INTO workspaces (id, name, icon, color, sort_order, created_at)
    VALUES ('ws-1', 'ws', 'folder', '#fff', 0, '2024-01-01T00:00:00+00:00');

INSERT INTO projects (
    id, workspace_id, name, local_path, remote_url, color, icon,
    ado_org, ado_project, ado_repo_id, github_owner, github_repo, github_host,
    sort_order, created_at
) VALUES (
    'proj-1', 'ws-1', 'proj', '/tmp/proj', NULL, '#fff', 'folder',
    NULL, NULL, NULL, NULL, NULL, NULL,
    0, '2024-01-01T00:00:00+00:00'
);
