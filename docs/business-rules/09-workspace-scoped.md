# 09 — Workspace-scoped features

Workspaces and projects, the flat `app_settings` key/value store, the per-workspace prompt
overrides, the SDD/Harness agent roster, review contexts, review-run memory, MCP servers, the
skills subsystem, and the chat/job activity history — everything a workspace or project hangs
its configuration off, minus the SQL that stores it (`src/CodeFlow.App/Activity/ActivityLogStore.cs`, owned by the storage
document) and minus the AI-engine dispatch that *consumes* most of these settings
(`src/CodeFlow.App/Ai/AiCommands.cs`, `src/CodeFlow.App/Review/ReviewCommands.cs`, owned by the AI-engines/review-pipeline
documents). Where this document cites those two files it is documenting the shape of data that
crosses through commands owned here (`app_settings` keys, `WorkspaceAgent`/`WorkspaceMcp`/
`WorkspaceSkill` rows, `job_history`/`activity_log` rows) — never their AI dispatch behaviour.

## Scope

- `src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`, `WorkspaceStore.cs`, `ProjectStore.cs`, `Settings.cs`
- `src/CodeFlow.App/Workspaces/SkillCommands.cs`, `SkillStore.cs`, `SkillFiles.cs`, `SkillInstaller.cs`, `SkillSync.cs`
- `src/CodeFlow.App/Workspaces/ReviewContextStore.cs`, `WorkspaceAgentStore.cs`, `WorkspaceMcpStore.cs`, `McpConfig.cs`
- `src/CodeFlow.App/Activity/ActivityCommands.cs`

## Commands

Full parameter/return signatures live in `01-ipc-surface.md`. One line each, in source order.

**`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`** — workspaces and projects:
- `pick_folder` — native folder picker, dispatched off the main thread to avoid a deadlock with macOS's picker (WS-001).
- `default_clone_dir` — the default "Clone repository" target directory.
- `create_workspace` — creates a workspace and seeds its two editable prompt overrides.
- `list_workspaces` — every workspace, ordered by `sort_order, created_at`.
- `delete_workspace` — deletes a workspace; cascades through FKs (WS-002).
- `rename_workspace` — renames a workspace; blank is refused (WS-009).
- `update_workspace_color`
- `update_workspace_git_identity` — sets or clears (both nulls) the workspace's commit-identity override (WS-008).
- `create_project` — creates a project under a workspace.
- `list_projects` — a workspace's projects.
- `get_project`
- `delete_project` — cascades through FKs (WS-002).
- `move_project_to_workspace` — reparents a project; changes which workspace-scoped config applies to it (WS-003).
- `update_project_color`

**`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`** — settings, prompts, review memory, agents, review contexts, MCP servers:
- `get_setting` / `set_setting` — generic `app_settings` key/value passthrough (WS-004).
- `get_workspace_prompt` — resolves a workspace's prompt override for `kind`, or the builtin default.
- `set_workspace_prompt` — saves an override; an empty string clears it back to the builtin.
- `default_workspace_prompt` — the builtin text for `kind`, for the editor's "restore default".
- `list_review_runs` — a workspace's saved PR-review runs, newest first.
- `get_review_run` — one saved run's full content.
- `mark_review_finding` — flips a finding's `estado` (`falso_positivo`/`ignorado`/back to open).
- `delete_review_run` / `delete_review_runs_for_pr` / `purge_workspace_review_runs`
- `export_review_runs` — writes saved runs to disk as `PR-<n>_<timestamp>/` folders.
- `list_workspace_agents` / `upsert_workspace_agent` / `delete_workspace_agent` — the SDD/Harness agent roster.
- `list_review_contexts` / `upsert_review_context` / `delete_review_context`
- `list_workspace_mcps` / `upsert_workspace_mcp` / `delete_workspace_mcp`

**`src/CodeFlow.App/Workspaces/SkillCommands.cs`** — the skills subsystem:
- `install_workspace_skill` — `npx skills add` into the workspace's skill store, streaming progress.
- `list_workspace_skills` / `remove_workspace_skill` / `set_workspace_skill_enabled`
- `create_custom_skill` — a new skill authored in-app.
- `import_skill_from_folder` — copies an existing `SKILL.md` folder into the workspace store.
- `list_skill_files` / `read_skill_file` / `write_skill_file` / `delete_skill_file` — per-file editing.
- `sync_skills_into_project` (not a a registered command — a `pub fn` helper called from `src/CodeFlow.App/Ai/AiCommands.cs`/`src/CodeFlow.App/Review/ReviewCommands.cs`) — the managed-folder sync into a project's `.claude/skills`.

**`src/CodeFlow.App/Activity/ActivityCommands.cs`** — chat conversations and job history:
- `list_chat_conversations` — a project's conversations, grouped from `activity_log`, newest first.
- `get_chat_conversation` — every turn of one conversation, oldest first.
- `delete_chat_conversation` / `rename_chat_conversation`
- `list_job_history` — a project's finished PR-review / analysis / PR-action runs, newest first.
- `rename_job_history_entry` / `delete_job_history_entry`

## Workspaces and projects

A **workspace** groups projects (repos) plus everything scoped to it: prompt overrides, review
contexts, the SDD/Harness agent roster, MCP servers, installed skills, and saved review-run
memory. A **project** is one local repo clone, optionally linked to an Azure DevOps repo or a
GitHub repo (`ado_org`/`ado_project`/`ado_repo_id` vs `github_owner`/`github_repo`/`github_host`
on the `Project` row — mutually-exclusive linking is enforced elsewhere, not in this document's
files).

Every workspace-scoped table (`review_contexts`, `workspace_prompts`, `workspace_skills`,
`workspace_agents`, `workspace_mcps`, `review_runs`) declares `workspace_id … REFERENCES
workspaces(id) ON DELETE CASCADE`, and `projects.workspace_id` does too. `src/CodeFlow.App/Storage/Migrations.cs`
sets `PRAGMA foreign_keys = ON` before creating any table, so these cascades are live, not
decorative.

### WS-001 `pick_folder` is dispatched off the main thread to avoid a picker deadlock
**Implementation**: `src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`
**Behaviour**: `pick_folder` is `async` and uses `dialog().file().pick_folder(callback)` plus a
the async oneshot` channel, awaited, rather than a blocking call. The doc comment states why:
a non-async command runs on the main thread; `blocking_pick_folder` would park that thread on the
channel while the OS picker itself needs the main thread free to pump events — the two deadlock
and the app stops responding on macOS.
**Inputs / outputs**: no params; returns `string?` (the chosen path, or `None` if
cancelled).
**Edge cases**: none beyond cancellation.
**Frontend dependency**: `01-ipc-surface.md` (`pickFolder`).
**Markers**: none.

### WS-002 Deleting a workspace or project cascades through foreign keys
**Implementation**: `src/CodeFlow.App/Workspaces/WorkspaceCommands.cs` (`delete_workspace`), `:74` (`delete_project`); cascade
declared in `src/CodeFlow.App/Storage/Migrations.cs` (`projects`, `review_contexts`, `workspace_prompts`,
`workspace_skills`, `workspace_agents`, `workspace_mcps` all reference `workspaces(id) ON DELETE
CASCADE`; `review_runs`, `activity_log`, `job_history`, `conversation_titles` all reference
`projects(id) ON DELETE CASCADE`).
**Behaviour**: `delete_workspace` issues a single `DELETE FROM workspaces WHERE id = ?1` and
relies entirely on SQLite's FK cascade to remove every dependent row — its own projects, and
(transitively, once those projects are gone) their `review_runs`/`activity_log`/`job_history`/
`conversation_titles` rows, plus the workspace's own `review_contexts`/`workspace_prompts`/
`workspace_skills`/`workspace_agents`/`workspace_mcps` rows. `delete_project` cascades the same
way one level down. Neither command touches the filesystem: a project's `local_path` clone and a
workspace's `skills/` directory (`AppPaths`) are left on disk untouched.
**Inputs / outputs**: `id: string` → `void`.
**Edge cases**: `review_runs.workspace_id` is a plain `TEXT NOT NULL` column with **no** FK to
`workspaces` (only `review_runs.project_id → projects` is a real FK) — deleting a workspace
removes its review runs only because their projects are removed first, not directly. A review
run somehow left with a `workspace_id` pointing at a workspace whose projects were moved out from
under it (see WS-003) is not cleaned up by either delete path.
**Frontend dependency**: `01-ipc-surface.md` (`deleteWorkspace`, `deleteProject`).
**Markers**: none.

### WS-003 Moving a project between workspaces changes which config applies to it, retroactively
**Implementation**: `src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`; `src/CodeFlow.App/Activity/ActivityLogStore.cs` (`move_project_to_workspace`)
**Behaviour**: `move_project_to_workspace` is a single `UPDATE projects SET workspace_id = ?2
WHERE id = ?1`. Review contexts, the agent roster, MCP servers, installed skills and the prompt
overrides are all resolved from a project's *current* `workspace_id` at the moment an AI action
runs (`analyze_working_changes`, `send_chat_message`, `review_pull_request`, … in
`src/CodeFlow.App/Ai/AiCommands.cs`/`src/CodeFlow.App/Review/ReviewCommands.cs` all do the store then read every workspace-scoped table
off `project.workspace_id`). Moving a project instantly and retroactively swaps in the destination
workspace's whole configuration — its review contexts, its skills, its MCP servers, its agent
roster, its `review_standard`/`pr_description` overrides — for every subsequent action on that
project. Nothing under `review_runs` moves with it: saved review-run memory stays stamped with
the `workspace_id` it was created under, so the memory manager's per-workspace list
(`list_review_runs`) stops showing a moved project's history, while `review_runs.project_id`
still resolves fine for anything that looks the run up by project instead.
**Inputs / outputs**: `id: string, workspace_id: string` → `void`.
**Edge cases**: no validation that the destination workspace exists (a bad id just leaves the
project unreachable from any workspace's project list; the FK on `projects.workspace_id` — if
enforced strictly — would instead make the `UPDATE` fail outright. Whether it does is not
determined from these four files; see `AMBIGUOUS-WS-a`).
**Frontend dependency**: `01-ipc-surface.md` (`moveProjectToWorkspace`).
**Markers**: `AMBIGUOUS-WS-a` — whether `move_project_to_workspace` can succeed with a
nonexistent `workspace_id` (silently orphaning the project) depends on whether SQLite's FK
enforcement rejects the `UPDATE`, which in turn depends on `PRAGMA foreign_keys` state at call
time — not determinable from `src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`/`src/CodeFlow.App/Activity/ActivityLogStore.cs` alone (it is set once in `Migrations.Run`,
outside both). Decide before porting whether the destination workspace should be validated
explicitly.

### WS-009 A workspace can be renamed, and cannot be renamed to nothing
**Implementation**: `src/CodeFlow.App/Workspaces/WorkspaceStore.cs` (`Rename`);
`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs` (`rename_workspace`).
**Behaviour**: `rename_workspace(id, name)` trims the name and writes it to `workspaces.name`,
touching no other column. A name that is empty once trimmed throws `ArgumentException` and the
stored name survives. No migration: the column has existed since the table did — until this rule
there was simply no command that wrote to it, so a workspace kept whatever name it was created
with for the life of the install.
**Inputs / outputs**: `id: string, name: string` → `void`.
**Edge cases**: the guard lives in the store rather than only in the form, because a workspace is
chosen from a list by its name alone — the header menu, the command bar's workspace scope and the
settings list all render exactly that string, so a blank one is a row that cannot be told apart or
picked with confidence. An unknown `id` updates zero rows and reports success, matching
`update_workspace_color` and `delete_workspace`. Duplicate names are allowed: nothing keys off the
name, and two workspaces called "Work" is the user's business.
**Frontend dependency**: `settings/ProjectsSettings.tsx` (the pencil on each workspace row).
**Markers**: none.

### WS-008 A workspace may override the global git identity for commits made through the app
**Implementation**: `src/CodeFlow.App/Workspaces/WorkspaceStore.cs` (`UpdateGitIdentity`,
`ResolveGitIdentity`); `src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`
(`update_workspace_git_identity`); consumed by `src/CodeFlow.App/Git/GitCommands.cs`
(`ResolveAuthor`, GIT-028/GIT-036). Columns added by `AddGitIdentityToWorkspaces`
(`src/CodeFlow.App/Storage/Migrations.cs`).
**Behaviour**: `workspaces.git_name`/`git_email` are nullable and travel as a pair:
`update_workspace_git_identity(id, name, email)` writes both values to set an override and both
nulls to clear it. At commit time (`commit`, `merge_branch`, `complete_merge`) the sidecar resolves
the author with `ResolveGitIdentity(repoPath)` — an exact `projects.local_path = repoPath` join to
the owning workspace — so a workspace's identity signs every commit made through the app in its
projects, without touching `~/.gitconfig` (GIT-027) or the repo's own `.git/config`. Terminal
commits in the same repo are deliberately unaffected.
**Inputs / outputs**: `id: string, name: string?, email: string?` → `void`; resolution returns
`(name?, email?)`, `(null, null)` meaning "fall back to the global identity".
**Edge cases**: a repo not registered as any project resolves to `(null, null)` — the global
identity signs, same as before this rule existed. Two projects sharing one `local_path` (nothing
in the schema prevents it) resolve to the first row — which one is unspecified, not a guarantee.
A half-set pair (possible only via out-of-band DB edits) is discarded by the git layer's
both-or-neither rule (GIT-028).
**Frontend dependency**: `updateWorkspaceGitIdentity` (`workspaceStore.setWorkspaceGitIdentity`);
Settings → Git (`GitSettings.tsx` + `WorkspaceGitIdentities.tsx`) lists the default identity and
one row per workspace.
**Markers**: none.

### `default_clone_dir` uses the Windows-only base path
**Implementation**: `src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`; `src/CodeFlow.App/Platform/AppPaths.cs`
**Behaviour**: `default_clone_dir` returns `AppPaths`()`, which is `base_dir().join("repos")`.
`base_dir()` is the hardcoded literal `C:\CodeFlow` on Windows, and `$HOME/CodeFlow` on every
other OS. So on Windows, cloning defaults under `C:\CodeFlow\repos\<name>` regardless of which
drive/user profile the app itself is installed under.
**Markers**: `DIVERGENCE-WS-a` — this is the canonical `C:\CodeFlow` divergence named in
`00-conventions.md`'s marker table. Preserve the hardcoded Windows path as-is; it is deliberate,
not a bug, and the whole persisted-state tree (`codeflow.db`, `workspaces/`, `repos/`) is rooted
there.

## Settings

`app_settings` is a flat `key TEXT PRIMARY KEY, value TEXT NOT NULL` table
(`src/CodeFlow.App/Storage/Migrations.cs`). `get_setting`/`set_setting` (`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`) are a pure
generic passthrough — the store/`set_setting` take an arbitrary caller-supplied
`key: string`, with **no validation, no enum, no schema**. Nothing in the four files owned by
this document restricts which keys exist; the frontend is free to read/write any key string
through this pair of commands, and several keys below (`github_connections`) are written
**exclusively** from the frontend — no the sidecar code ever calls `set_setting` for them.

The table below is every key literal found by searching the store(` /
the store(` call sites across the shell (not just this document's four files —
the generic commands above are the only way any of these keys is ever read or written, so the
namespace itself is this document's to catalogue even though most readers live in
`src/CodeFlow.App/Ai/AiCommands.cs` and `src/CodeFlow.App/Review/ReviewCommands.cs`, owned by the AI-engines/review-pipeline
documents). `XLANG-004`/`XLANG-005` in `13-cross-language-contracts.md` already cross-check the
task-routing half of this table against its TypeScript mirror in detail (including
`BUG-XLANG-a`); it is not re-litigated here.

| Key (VERBATIM) | Read by | Missing/blank falls back to |
|---|---|---|
| `ai_provider` | `src/CodeFlow.App/Ai/AiCommands.cs` (`active_provider`) | `"claude"` |
| `ai_provider_{task}` — 9 keys, `task ∈ {commit, analyze, review, pr_description, chat, fix, conflict, inline, ticket_review}` (`AiRouting.Tasks`, `src/CodeFlow.App/Ai/AiRouting.cs`) | `src/CodeFlow.App/Ai/AiCommands.cs` (`provider_for`) | the global `ai_provider` |
| `{provider}_{task}_model` | `src/CodeFlow.App/Ai/AiCommands.cs` (`load_ai_config`) | for `task = commit`: the engine's `commit_message_model()` if non-empty, else `{provider}_model`; for every other task: `{provider}_model` |
| `{provider}_model` | `src/CodeFlow.App/Ai/AiCommands.cs` | `""` |
| `{provider}_binary_path` | `src/CodeFlow.App/Ai/AiCommands.cs` | `engine.default_binary()` |
| `{provider}_allowed_tools` (comma-separated) | `src/CodeFlow.App/Ai/AiCommands.cs` | `""` → empty tool list |
| `commit_template` — legacy key `claude_commit_template` | `src/CodeFlow.App/Ai/AiCommands.cs` via `shared_template` | legacy key, then `""` (engine falls back to its own built-in commit template downstream) |
| `resolve_conflict_template` — legacy key `claude_resolve_conflict_template` | `src/CodeFlow.App/Ai/AiCommands.cs` via `shared_template` | legacy key, then `""` |
| `analyze_template` — legacy key `claude_analyze_template` | `src/CodeFlow.App/Ai/AiCommands.cs` via `shared_template` | legacy key, then `""` |
| `github_connections` (JSON array of `{host: string, …}`) | `src/CodeFlow.App/Review/ReviewCommands.cs` (`github_known_hosts`) | absent/unparseable → only `github.com` is a known host |

`shared_template` (`src/CodeFlow.App/Ai/AiCommands.cs`) is the "`shared_template` resolves e.g.
`commit_template` with an older `claude_commit_template` fallback" mechanism named in this
document's brief: it reads the new unprefixed key first, and only if that is absent **or
blank** falls through to the legacy `claude_*`-prefixed key, returning `""` if neither is set.
These three templates are provider-independent (`shared_template`'s own doc comment: "the same
text applies to whatever engine a task routes to") — unlike `commit_template` here (an
`app_settings` key, one for the whole install), `review_standard` and `pr_description` are a
different, workspace-scoped mechanism (their own `workspace_prompts` table, not `app_settings`
at all) — see **Prompt templates** below. Do not conflate the two: `commit_template`/
`resolve_conflict_template`/`analyze_template` have no per-workspace override and no builtin
constant read through this fallback chain (the `""` empty-string fallback is the actual observed
floor — whatever built-in default `src/CodeFlow.App/Ai/` etc. apply when handed `""` lives
in `src/CodeFlow.App/Ai/AiOperations.cs`, owned by the AI-engines document).

**Not enumerable from the sidecar**: because `get_setting`/`set_setting` accept any key, keys
that are only ever read and written by the frontend (UI theme, language, window state, and
`github_connections` above) have no the sidecar call site to discover by grepping this tree. This table
is complete for backend-known keys only.

### WS-004 `get_setting`/`set_setting` is an unvalidated generic key/value store
**Implementation**: `src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`; `src/CodeFlow.App/Activity/ActivityLogStore.cs`
**Behaviour**: `set_setting` upserts `(key, value)` with `ON CONFLICT(key) DO UPDATE`; `get_setting`
does a single `SELECT value … WHERE key = ?1`, `.optional()`. Both accept any string; there is no
allow-list, no type coercion (every value is stored and returned as `string`), and no
migration path if a key is renamed — a renamed key silently orphans whatever was stored under
the old name (this is exactly what the `commit_template`/`claude_commit_template` legacy-fallback
pattern above exists to paper over for the three shared templates; nothing equivalent exists for
any other key in the table above).
**Inputs / outputs**: `key: string` (+ `value: string` for the setter) → `string?` / `void`.
**Edge cases**: an empty-string `value` is stored as a real row and returned as `Some("")`, not
`None` — every reader above that treats "blank" as "unset" (`filter(|s|
!s.trim().is_empty())`) does so in its own call site, not in `get_setting` itself.
**Frontend dependency**: `01-ipc-surface.md` (`getSetting`, `setSetting`); the entire Settings
screen.
**Markers**: none.

## Prompt templates

**What the code actually implements is a two-level cascade, not the three-level versioned one
this document's brief describes.** There is no `app_settings`/"global" layer for
`review_standard` or `pr_description`, no version number stored anywhere for either builtin, and
no diff-surfaced-on-upgrade mechanism — none of `src/CodeFlow.App/Ai/AiOperations.cs`, `src/CodeFlow.App/Activity/ActivityLogStore.cs`, or `src/CodeFlow.App/Storage/Migrations.cs`
carries anything resembling a builtin-version field or an edited-since-version-N check. The
actual cascade, verified against `src/CodeFlow.App/Activity/ActivityLogStore.cs` and `src/CodeFlow.App/Storage/Migrations.cs`:

1. **Workspace row** in `workspace_prompts` (`PRIMARY KEY (workspace_id, kind)`), if present and
   its `content` is non-blank after `.trim()`.
2. Otherwise, **the hardcoded builtin constant** for `kind` — `workspace_prompt_default`
   (`src/CodeFlow.App/Activity/ActivityLogStore.cs`): `"pr_description"` → ai.DEFAULT_PR_DESCRIPTION_TEMPLATE;
   `"ticket_review_standard"` → `Prompts.DefaultTicketReviewStandard`;
   `"sdd_stages"` → `""` (no builtin text exists for this kind — see the edge case below);
   anything else, including `"review_standard"`, → ai.DEFAULT_PR_REVIEW_STANDARD.

**`ticket_review_standard` needs its own arm because the catch-all is not an error case.** A kind
without an arm resolves to the PR methodology and nothing fails, so "restore default" on the ticket
standard would have silently handed back a prompt that never mentions a work item — a review that
quietly stops reporting acceptance criteria, which reads as the model refusing.
`SettingsTests.The_ticket_review_standard_does_not_fall_through_to_the_pr_one` pins it (`WI-011`).

There is no third "global" step and no first-match-wins search across multiple candidate sources
— it is exactly these two, and step 2 is a compile-time constant, not a stored/versioned row. A
blank save (`set_workspace_prompt(…, content: "")`) is how "restore default" works: it clears the
row's content back to blank, and step 2 kicks back in on the next read; the row itself is never
deleted, `ON CONFLICT(workspace_id, kind) DO UPDATE` overwrites it in place.

`create_workspace` (`src/CodeFlow.App/Activity/ActivityLogStore.cs`) seeds `review_standard`,
`ticket_review_standard` and `pr_description` rows
with their builtin defaults at workspace creation, so a fresh workspace's `workspace_prompts` table
already has real (non-blank) text for all three — the fallback in step 2 only actually fires for
workspaces created before this seeding existed, backfilled by
`backfill_workspace_prompts` (`src/CodeFlow.App/Storage/Migrations.cs`, an `INSERT OR IGNORE` per workspace that
lacks a row for either kind), or if a save later blanks the row out again. `sdd_stages` is **never**
seeded or backfilled — it only ever gets a row when the frontend calls `set_workspace_prompt` for
it directly.

**Two commands drive this** (both owned here): `get_workspace_prompt(workspace_id, kind)` reads
the resolved text (never really "always non-empty" — see the edge case below);
`set_workspace_prompt(workspace_id, kind, content)` writes the override, empty string = reset.
`default_workspace_prompt(kind)` (no `workspace_id`) is the pure builtin lookup, for the editor's
"restore default" preview before saving.

Two consumers read `review_standard`/`pr_description` (both outside this document's scope, cited
for completeness): `src/CodeFlow.App/Review/ReviewCommands.cs` (`review_pull_request`, `review_pr_from_link`) read
`review_standard`; `src/CodeFlow.App/Review/ReviewCommands.cs` (`generate_pr_description`) reads `pr_description`. Neither
consumer has a "workspace vs global" choice to make — they call `get_workspace_prompt` with the
project's own `workspace_id` and get back whatever the two-level cascade above resolves to.

**Edge case — `get_workspace_prompt`'s doc comment overstates its own guarantee.**
`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs` documents the command as "Always non-empty — a blanked/absent row
resolves to the built-in default." That is true for `review_standard` and `pr_description` (both
have a non-empty builtin), but **false for `kind = "sdd_stages"`**: its builtin is the empty
string (`src/CodeFlow.App/Activity/ActivityLogStore.cs`), so an absent or blanked `sdd_stages` row legitimately returns `""`
through the very same code path the doc comment describes as always non-empty. The `sdd_stages`
kind's own comment explains why: "The SDD/Harness pipeline stages reuse this per-workspace text
store; they start empty (no preconfig — the user defines them)." This is not a bug in the
resolution logic — it behaves exactly as written — just an inaccurate doc comment on the command
that reads it.

## SDD / Harness agent roster

A **workspace agent** (`WorkspaceAgent`: `id, workspace_id, name, role, provider, model, prompt,
enabled, sort_order, created_at`) is a user-authored, per-workspace role with its own AI routing —
name, a free-text role description, a provider id, a model id, a free-text prompt, and an on/off
toggle. The roster starts **empty** for every workspace (`src/CodeFlow.App/Storage/Migrations.cs`: "Deliberately
empty by default — the user creates their own"); nothing seeds a default roster the way
`review_standard`/`pr_description` are seeded.

`list_workspace_agents` orders by `sort_order, created_at`. `upsert_workspace_agent` does an
`INSERT … ON CONFLICT(id) DO UPDATE`: when `id` is `Some` and an existing row is found, its
`sort_order`/`created_at` are preserved and carried into the update (`src/CodeFlow.App/Activity/ActivityLogStore.cs`);
when `id` is `None`, or is `Some` but matches no row, a fresh id is minted (or the caller-supplied
one is used with `sort_order = 0`) and `created_at = now()`. There is no dedicated
"reorder" command in this document's scope — `sort_order` can only be set indirectly through
whatever path originally inserted a row with a non-zero value, which none of these four files do
(every insert path here writes `sort_order = 0`).

**Wiring an agent as a per-run override.** Four commands — all owned by `src/CodeFlow.App/Ai/AiCommands.cs`/
`src/CodeFlow.App/Review/ReviewCommands.cs`, not this document, cited here because this is precisely how a `WorkspaceAgent` row
gets *used*, not just stored — take `agent_provider: string?`, `agent_model: string?`,
`agent_prompt: string?` as extra parameters: `send_chat_message` (`src/CodeFlow.App/Ai/AiCommands.cs`),
`analyze_working_changes` (`src/CodeFlow.App/Ai/AiCommands.cs`), `review_pull_request` (`src/CodeFlow.App/Review/ReviewCommands.cs`),
and `review_pr_from_link` (`src/CodeFlow.App/Review/ReviewCommands.cs`). In every one of the four, the same pattern
applies:

`csharp
var config = agentProvider is { Length: > 0 } p && agentModel is { Length: > 0 } m
    ? LoadAiConfigFor(connection, p.Trim(), m.Trim())
    : LoadAiConfig(connection, AiTask.Chat /* or Analyze, or Review */);
`

`load_ai_config_for(conn, provider, model)` (`src/CodeFlow.App/Ai/AiCommands.cs`) resolves that provider's
binary/tools from its own `app_settings` keys (`{provider}_binary_path`, `{provider}_allowed_tools`)
but takes the **model verbatim from the agent row** — it never touches the task-routing settings
(`ai_provider_{task}`, `{provider}_{task}_model`) at all. This is the "bypasses the per-task
routing lookup" behaviour: an active agent's `provider`/`model` completely replace step 1 and step
2 of the normal per-task cascade (`XLANG-005`'s table), not just override its result. The agent's
`prompt` is inserted as the **first** entry of the enabled review-contexts list under the label
`"Agent"` (`src/CodeFlow.App/Ai/AiCommands.cs`; `src/CodeFlow.App/Review/ReviewCommands.cs`), ahead of every workspace
review context, so its instructions frame the whole turn. `provider`/`model` are only honoured as
a pair — one present without the other falls through to normal per-task routing (the `Some(p),
Some(m)` guard requires both, and both non-blank).

## Review contexts

A **review context** (`ReviewContext`: `id, workspace_id, name, content, enabled, created_at`) is
a named, freeform block of instructions attached to a workspace, folded into the PR-review/
analyze/chat prompt whenever `enabled = true`. `list_review_contexts` orders by `created_at`
(insertion order, not alphabetical or by any user-settable rank — there is no `sort_order` column
on this table, unlike `workspace_agents`). `upsert_review_context` is `INSERT … ON CONFLICT(id) DO
UPDATE SET name, content, enabled` (`created_at` is never touched on update).

Consumption (outside this document's scope, cited for completeness): every one of the four AI
commands above builds its context list as `contexts.filter(|c| c.enabled).map(|c| (c.name,
c.content))`, i.e. **disabled contexts are silently dropped**, not sent with any "disabled" marker
— disabling one is indistinguishable, from the model's perspective, from never having created it.

## MCP servers

A **workspace MCP server** (`WorkspaceMcp`: `id, workspace_id, name, command, args, env, enabled,
created_at`) is a name plus a command to launch it, with `args` stored as a single
space-separated string (`src/CodeFlow.App/Workspaces/WorkspaceModels.cs`: "same convention as the shell — kept as plain text
rather than a JSON array so the settings UI can just be a single text input") and `env` as
`KEY=value` lines, one per line (`src/CodeFlow.App/Workspaces/WorkspaceModels.cs`). `list_workspace_mcps` orders by
`created_at`. `upsert_workspace_mcp` is `INSERT … ON CONFLICT(id) DO UPDATE SET name, command,
args, env, enabled`.

Consumption (`src/CodeFlow.App/Review/ReviewCommands.cs`, `build_mcp_config`, outside this document's scope but describing
exactly what a stored `WorkspaceMcp` row is *for*): only `enabled = true` rows are included; if
none are enabled the function returns `null` and no config file is written or referenced at
all for that run. Otherwise, `args` is re-split on whitespace (`split_whitespace`) into a JSON
array, `env` is parsed line-by-line on the first `=` (`split_once('=')`, both sides trimmed) into
a JSON object, and the whole set is written as `{"mcpServers": {name: {command, args, env}, …}}`
to `<base_dir>/workspaces/<workspace_id>/mcp.json` — one file per workspace, overwritten on every
AI run that has at least one enabled MCP server, and passed to the engine as its `--mcp-config`
path.

## Skills subsystem

A **workspace skill** (`WorkspaceSkill`: `id, workspace_id, skill_name, source_repo, enabled,
installed_at`) is a folder containing (at minimum) a `SKILL.md`, stored under the workspace's
canonical skill store:

`
<base_dir>/workspaces/<workspace_id>/skills/.claude/skills/<skill_name>
`

(`AppPaths`(workspace_id)` = `base_dir/workspaces/<id>/skills`; `skills_root` =
that `.join(".claude").join("skills")`; `skill_dir` = `skills_root.join(name)` —
`src/CodeFlow.App/Workspaces/SkillCommands.cs`.) `source_repo` records where it came from: the skills.sh repo
slug for an `npx`-installed skill, the literal string `"custom"` for one authored in-app
(`create_custom_skill`), or `"local"` for one imported from a folder (`import_skill_from_folder`).

### WS-005 Installing a skill shells out to `npx`, shimmed through `cmd /C` on Windows, streaming both output streams as one event
**Implementation**: `src/CodeFlow.App/Workspaces/SkillCommands.cs`
**Behaviour**: `npx_command()` (`:18-26`) returns `ProcessStartInfo`("cmd").args(["/C", "npx"])` on
Windows, `ProcessStartInfo`("npx")` elsewhere — the doc comment states why: `npx` is a `.cmd` shim on
Windows, and spawning it directly (without `cmd /C`) fails to launch at all, "the same class of
issue as calling any other npm-installed shim." `install_workspace_skill` runs `npx --yes skills
add <source_repo> --skill <skill_name>` with `current_dir` set to the workspace's skill root
(*not* the `.claude/skills` subfolder — `npx skills add` creates that itself), `stdin(Stdio.null())`,
and both `stdout`/`stderr` piped. Two the async runtimeed tasks read each stream line-by-line and
`emit("skills:progress", {line})` for every line from *either* stream, indistinguishably, while
also collecting the lines into `IReadOnlyList<string>` for error reporting. After `child.wait()`, a
non-zero exit returns `Err("npx skills add failed: {detail}")` where `detail` is the joined
stderr lines, or the joined stdout lines if stderr was empty. On success, the code additionally
verifies `dir/.claude/skills/<skill_name>` actually exists on disk before recording anything in
the database — a `0` exit status alone is not trusted.
**Inputs / outputs**: `workspace_id, source_repo, skill_name: string` → `WorkspaceSkill`. Emits `skills:progress: {line: string}` (event contract owned by `01-ipc-surface.md`)
zero or more times per call, from both processes' output interleaved in whatever order the two
background tasks happen to read them.
**Edge cases**: a `npx` binary that isn't on `PATH` fails at `cmd.spawn()` with `"failed to launch
npx: {e}"`, before any progress event is ever emitted. A success exit whose expected folder is
missing returns an error naming the exact path checked, suggesting a skill-name/repo mismatch.
**Frontend dependency**: `01-ipc-surface.md` (`installWorkspaceSkill`, `skills:progress`).
**Markers**: `DIVERGENCE` — the `cmd /C` shim is a deliberate, documented Windows workaround; do
not "simplify" it back to a direct `npx` spawn when porting.

### WS-006 `sync_skills_into_project` only ever touches folders whose name is a known `WorkspaceSkill` row
**Implementation**: `src/CodeFlow.App/Workspaces/SkillCommands.cs`
**Behaviour**: `sync_skills_into_project(skills: IReadOnlyList<WorkspaceSkill>, workspace_id, project_path)` is
how "managed" is determined — and it is determined **entirely by name, against the caller-supplied
`skills` slice**, never by any marker file or metadata written into the destination folder itself.
Given the full list of a workspace's `WorkspaceSkill` rows (both enabled and disabled):
1. For every row where `enabled == false`: `remove_dir_all(dest_root.join(&skill.skill_name))` —
   best-effort, error discarded (`let _ =`).
2. Build a `HashSet` of every row where `enabled == true`, keyed by `skill_name`.
3. If the workspace's skill-store root doesn't exist, or the enabled set is empty, return
   immediately — nothing is created or removed further.
4. Otherwise, for every **directory** directly under the workspace's `skills_root`, copy it into
   `dest_root.join(name)` **only if** `name` is in the enabled set from step 2; anything else
   under `skills_root` (a directory whose name isn't a currently-enabled skill — including one
   that's merely disabled, since step 1 already handled those, or a stray folder never recorded in
   the DB at all) is left untouched at the source and never copied.
`dest_root` is always `<project_path>/.claude/skills`. The function never lists what's *already*
in `dest_root` beyond the specific `skill.skill_name` folders it targets for removal in step 1 —
so **a folder under a project's `.claude/skills` whose name does not match any of this workspace's
`WorkspaceSkill.skill_name` values (past or present) is never read, written, or deleted by this
function**, at any point. That is the entire "managed" boundary: "managed" = "this workspace's DB
currently has (or ever had, for the disabled-removal step) a `workspace_skills` row with this
exact name"; anything else in that folder — a skill the user placed there by hand, or one
installed by a different tool — is invisible to this sync.
**Inputs / outputs**: `IReadOnlyList<WorkspaceSkill>, workspace_id: string, project_path: string` →
`void`. Not a a registered command; called from `src/CodeFlow.App/Ai/AiCommands.cs` (chat, analyze) and
`src/CodeFlow.App/Review/ReviewCommands.cs` (both PR-review commands) immediately before each AI run, always as best-effort
(`let _ = sync_skills_into_project(...)`) — a sync failure never blocks the run it precedes.
**Edge cases**: disabling a skill and then re-enabling it before the next sync is a no-op from the
destination's perspective (never removed, never re-copied) — the removal only actually fires on a
sync that observes it disabled. A skill folder deleted from the workspace store out-of-band
(manually, on disk) but still `enabled` in the DB is silently skipped by `copy_dir_recursive`'s
source-side `read_dir` loop (the `read_dir(&src_root)` in step 4 only iterates whatever actually
exists there) — no error, the project's `.claude/skills` simply keeps whatever stale copy it last
had, or has none.
**Frontend dependency**: none directly (internal helper); indirectly every AI-run command in
`src/CodeFlow.App/Ai/AiCommands.cs`/`src/CodeFlow.App/Review/ReviewCommands.cs` that calls it.
**Markers**: none — this is the load-bearing mechanism the "never delete a user's hand-written
skill" guarantee rests on; port the by-name matching exactly, including that it is scoped to
directory entries only (`entry.file_type()?.is_dir()`, `:276`), so a stray file dropped directly
under `.claude/skills` is never touched either.

### WS-007 `safe_skill_path` rejects `..` and empty path segments before any file-editing command touches disk
**Implementation**: `src/CodeFlow.App/Workspaces/SkillCommands.cs`
**Behaviour**: `read_skill_file`, `write_skill_file`, and `delete_skill_file` all resolve their
`rel_path` through `safe_skill_path`, which splits on both `/` and `\` and rejects the whole path
if any segment is `".."` or empty (`rel.split(['/', '\\']).any(|c| c == ".." || c.is_empty())`) —
returning `Err("invalid file path")` before joining anything onto `skill_dir(...)`. This is the
only traversal guard; there is no canonicalization/prefix check afterward (unlike, say, an
allow-listed root comparison) — it relies entirely on `..` never appearing in any accepted
segment.
**Inputs / outputs**: `workspace_id, skill_name, rel_path: string` (+ `content: string` for the
write) → `string` / `void`.
**Edge cases**: a leading or trailing slash produces an empty segment and is rejected the same
way as `..`. `skill_name` itself is re-sanitized through `sanitize_name` inside `safe_skill_path`
(`:239`), so a `skill_name` containing path separators is neutralized to `-` rather than escaping
`skill_dir`.
**Frontend dependency**: `01-ipc-surface.md` (`readSkillFile`, `writeSkillFile`,
`deleteSkillFile`) — the in-app skill file editor.
**Markers**: none.

### BUG-WS-a (closed) `remove_workspace_skill` removes the folder first and propagates its failure
**Implementation**: `src/CodeFlow.App/Workspaces/SkillCommands.cs`, `src/CodeFlow.App/Workspaces/SkillFiles.cs` (`RemoveDirectory`)
**Behaviour**: the command looks up the skill, calls `SkillFiles.RemoveDirectory` — which
throws `"Could not delete the skill's folder — close anything using it and try again: …"` on a
filesystem refusal, and treats an already-missing folder as the goal state — and only then
deletes the DB row. A failed folder delete therefore aborts before the row is touched, so
nothing is orphaned and the remove can simply be retried.
**Was (1.7.2)**: the row went first and the folder's failure was discarded, so an undeletable
folder (permissions, a locked file, an open handle) survived with no row left to find it by —
and because `create_custom_skill`/`import_skill_from_folder` refuse an existing directory, the
name was permanently blocked from reuse through the UI, with no visible cause.

### BUG-WS-b (closed) `install_workspace_skill` refuses an existing skill name, like its siblings
**Implementation**: `src/CodeFlow.App/Workspaces/SkillCommands.cs`
**Behaviour**: the install checks `Directory.Exists` on the skill's target folder **before**
invoking `npx skills add`, and refuses with the same named-collision error its two sibling
creation paths always used (`"A skill named \"{name}\" already exists in this workspace"`).
`workspace_skills` still has no `UNIQUE (workspace_id, skill_name)` constraint — the guard is
behavioural, matching `create_custom_skill`/`import_skill_from_folder`.
**Was (1.7.2)**: no guard anywhere — a re-install ran npx over the shared folder and added a
second `workspace_skills` row pointing at it; `list_workspace_skills` showed duplicates,
`sync_skills_into_project`'s name-set treated the folder as enabled while *either* row was, and
removing one row deleted the shared folder out from under the survivor.

## Activity log and job history

Two independent, project-scoped persisted logs back what `jobsStore`/`chatStore` only otherwise
hold in memory for the session (`src/CodeFlow.App/Storage/Migrations.cs`): **`activity_log`** for
open-ended chat turns, **`job_history`** for finished PR reviews, pre-commit analyses, and PR
actions (approve/reject/close). Neither table is written by any command owned in this document —
`src/CodeFlow.App/Activity/ActivityCommands.cs` is read/rename/delete only — but this document owns describing what ends up in
them, since that shape is exactly what `list_chat_conversations`/`get_chat_conversation`/
`list_job_history` expose.

**Per chat turn** (`ActivityLogEntry`, written by the store from
`src/CodeFlow.App/Ai/AiCommands.cs`): `id`, `project_id`, `session_id` (the app-minted **conversation** id —
stable for the conversation's whole life, distinct from the engine's own resume token),
`engine_session_id` (the engine's resume token for that specific turn, when it reported one),
`question`, `answer`, `trace` (JSON array of `{stream, line}`, the engine's live output during
that turn — `None` for turns recorded before tracing existed), `created_at`, `response_time_ms`
(timed around the engine call only, not the surrounding DB/IPC work), `provider`/`model`/
`engine_version` (recorded **per turn**, not read live from current settings at display time — a
reopened old conversation keeps claiming whatever engine actually answered it, even after routing
changes since), and `is_error`. **Failures are recorded**: `send_chat_message` logs a failed turn
(`is_error = true`, `answer` holds the error string, `model = None` since the engine never got
that far) unless the failure is a user-initiated cancellation (`e.starts_with(CANCELLED_MARKER)`),
which is deliberately **not** logged — "a run the user stopped isn't history: it has no answer,
and filing it would leave a permanent failed turn in the transcript for something they did on
purpose."

**Grouping into conversations** (`list_chat_conversations`, `src/CodeFlow.App/Activity/ActivityLogStore.cs`): every
`activity_log` row with a non-null `session_id` is grouped by that column; a conversation's
`title` defaults to its *first* turn's `question` text (rows are read `ORDER BY created_at ASC`,
so the first one inserted into the group map is the earliest), `updated_at` tracks the latest
turn's `created_at`, and `turn_count` is a running count — all computed in the sidecar over every row for
the project on each call, not via a SQL `GROUP BY`. A user rename (`rename_chat_conversation`) is
stored in a **separate** `conversation_titles` table keyed by `session_id`, and is applied as a
final override pass after grouping, taking priority over the auto-derived title. `search`, when
given, is matched case-insensitively against **both** `question` and `answer` of every turn (not
just titles), and keeps a conversation if *any* of its turns match — so a search hit can surface
a conversation whose first-question title doesn't contain the term at all. `delete_chat_conversation`
removes both the matching `activity_log` rows and the `conversation_titles` row together.

**Per finished job** (`JobHistoryEntry`, written by the store, called from three
places outside this document's scope — cited here because it defines exactly what
`list_job_history` returns): `id` (reused from the frontend's own in-flight job id, "so a job that
just ran this session and the same job reloaded from history after a restart share one identity"),
`project_id`, `kind`, `label`, `custom_label` (a user rename, taking priority over `label` when
set — `rename_job_history_entry` only ever writes this column), `status`, `result`, `error`,
`meta` (JSON, shape varies by `kind`), `created_at`. Three `kind` values are observed:
- `"analyze-changes"` (`src/CodeFlow.App/Ai/AiCommands.cs`) — recorded on **both** `status: "done"` (with
  `result`) and `status: "error"` (with `error`), for every completed run except a
  user-cancelled one (same `CANCELLED_MARKER` check as chat — a stopped analysis leaves no row at
  all).
- `"pr-review"` (`src/CodeFlow.App/Review/ReviewCommands.cs`) — same both-outcomes-recorded, both-excludes-cancellation
  pattern as `"analyze-changes"`.
- `"pr-action"` (`src/CodeFlow.App/Review/ReviewCommands.cs`, the approve/request-changes/close command) — **only ever
  recorded on success.** The VCS call (`set_reviewer_vote`/`abandon_pull_request`/
  `submit_pr_review`/`close_pull_request`) is awaited with `?` before `job_id` is even
  minted; a failure there returns the error directly to the caller and `add_job_history` is never
  reached. There is no `job_history` row, ever, for a failed PR approve/reject/close — this is a
  real asymmetry against the other two `kind`s (both of which explicitly log their failure branch)
  and is documented here as observed behaviour, not resolved as a bug or an ambiguity: nothing in
  `src/CodeFlow.App/Activity/ActivityCommands.cs` (the read side) determines or depends on which outcomes get written upstream,
  and the code that writes them is fully deterministic — it is simply asymmetric, unlike the
  session-cancellation exclusion above (which is the same *deliberate* rule applied consistently
  to both other kinds).

`list_job_history` orders by `created_at DESC` for one project; there is no cross-project or
cross-workspace job-history listing anywhere in this document's commands (unlike
`list_review_runs`, which is workspace-scoped). `delete_job_history_entry` is a plain `DELETE …
WHERE id = ?1` that affects `0` rows (not an error) if the job never got a `job_history` row in
the first place — "best-effort by design … the frontend removes it from memory regardless"
(`src/CodeFlow.App/Activity/ActivityLogStore.cs`).

### WS-010 A new repository takes the least-used colour, and the colour reaches every icon
**Implementation**: `renderer/src/lib/ui/projectColor.ts` · `renderer/src/state/workspaceStore.ts`
(`addProject`) · `components/home/HomeView.tsx` · `components/settings/ProjectsSettings.tsx` ·
`components/layout/CommandPalette.tsx`
**Behaviour**: `projects.color` has always existed and always started as the same indigo — the three
places that add a repository each wrote `#6366f1` by hand — so every repository looked alike and the
per-project picker in Settings had nothing to distinguish. `addProject` now decides, and it is the
only place that knows which colours are taken. A caller may still name one.
**Inputs / outputs**: the renderer's `NewProject.color` is optional; the wire still carries a string.
`nextProjectColor` returns the **least-used** hue of the eight `ACCENT_OPTIONS`, not the next in
sequence: a counter keeps advancing past colours freed by a removed repository and starts repeating
while some go unused. Ties break by palette order, so the same set of existing colours always gives
the same answer.
**Edge cases**: repositories added before this are recoloured on load, and **only** those still
carrying the old literal `#6366f1`. That value is the one thing that cannot have been chosen — the
picker offers eight hues and its indigo is `#6260ff` — so it identifies a default without guessing,
which is what makes doing it silently acceptable where a blanket pass would not be. Naturally
idempotent: after the first load nothing matches. A colour that cannot be saved leaves its
repository as it was, rather than showing one the next launch will not remember. A colour outside
the palette is ignored when counting rather than handed back out. Contrast needs no
new measurement: the picker only ever offered these eight, and `accentStore.test.ts` already holds
each to 4.5:1 as ink through `lib/ui/contrast.ts` — which is how a repository's colour is used
everywhere except the sidebar chip, whose fill-plus-white-glyph pairing is left alone.
**Frontend dependency**: the icon is tinted everywhere a repository appears — the sidebar row,
Home's recent projects, each Settings project row and the command palette. The sidebar was a filled
chip with a hardcoded white glyph, which is the fill/foreground pair `renderer-ui.md` warns must
move together; it is now a tinted glyph like the header pill, so one treatment covers every place.
**Markers**: none.

## Rules

| Rule | Title |
|---|---|
| WS-001 | `pick_folder` is dispatched off the main thread to avoid a picker deadlock |
| WS-002 | Deleting a workspace or project cascades through foreign keys |
| WS-003 | Moving a project between workspaces changes which config applies to it, retroactively |
| WS-004 | `get_setting`/`set_setting` is an unvalidated generic key/value store |
| WS-005 | Installing a skill shells out to `npx`, shimmed through `cmd /C` on Windows, streaming both output streams as one event |
| WS-006 | `sync_skills_into_project` only ever touches folders whose name is a known `WorkspaceSkill` row |
| WS-007 | `safe_skill_path` rejects `..` and empty path segments before any file-editing command touches disk |
| WS-008 | A workspace may override the global git identity for commits made through the app |
| WS-009 | A workspace can be renamed, and cannot be renamed to nothing |
| WS-010 | A new repository takes the least-used colour, and the colour reaches every icon |

(Full rule bodies are inline above, next to the feature they describe, rather than repeated here —
each carries its own `### WS-0NN` heading in **Workspaces and projects** / **Skills subsystem**.)

## Test coverage

No ` functions exist in any of this document's four files (`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`,
`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`, `src/CodeFlow.App/Workspaces/SkillCommands.cs`, `src/CodeFlow.App/Activity/ActivityCommands.cs`) — confirmed by a
full read of all four. `src/CodeFlow.App/Activity/ActivityLogStore.cs`, which does carry five ` functions covering
activity-log conversation grouping and provider-per-turn tracking, is owned by the storage
document, not this one (`00-conventions.md`'s ownership rule: one file, one document) — those
tests are accounted for there.

**No test-vectors fixtures produced by this document.**

## Markers raised

| Marker | Where | Summary |
|---|---|---|
| `AMBIGUOUS-WS-a` | WS-003 | Whether `move_project_to_workspace` can succeed with a nonexistent `workspace_id` depends on FK-enforcement state not determinable from `src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`/`src/CodeFlow.App/Activity/ActivityLogStore.cs` alone. |
| `DIVERGENCE-WS-a` | `default_clone_dir` | Hardcoded `C:\CodeFlow` on Windows — the canonical divergence named in `00-conventions.md`; preserve, do not "fix". |
| `DIVERGENCE` (unnumbered, local to WS-005) | WS-005 | The `cmd /C npx` shim on Windows is deliberate; preserve exactly. |
| ~~`BUG-WS-a`~~ **CLOSED** | Skills subsystem | `remove_workspace_skill` deleted the DB row before the filesystem removal and swallowed the latter's error, silently orphaning a folder that then blocked reuse of its name. Closed: folder first, failure propagated, missing folder idempotent. See `91-known-bugs.md`. |
| ~~`BUG-WS-b`~~ **CLOSED** | Skills subsystem | `install_workspace_skill` had no existing-skill guard (unlike `create_custom_skill`/`import_skill_from_folder`), so re-installing the same name created a duplicate `workspace_skills` row over one shared folder. Closed with the identical guard, run before npx. See `91-known-bugs.md`. |
