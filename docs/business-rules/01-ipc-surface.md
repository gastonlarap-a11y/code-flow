# 01 — IPC surface

The complete contract between the frontend and the backend: every command the frontend can
call, and every event the backend can push. This document owns the *surface*; what each
command actually does is described in the domain document linked from its section heading.

## Scope

- `src/CodeFlow.App/Ipc/CommandRegistry.cs` — the registry every feature contributes to

Each feature folder registers its own commands with an `Add…Commands(…)` extension method, and
`src/CodeFlow.App/Program.cs` calls them; the bootstrap sequence is owned by
`02-bootstrap-platform.md`. Those commands appear in the table below because the table is the
contract; their semantics do not.

## Reconciliation

Established by parsing the tree, not by reading it:

| Set | Count | Source |
|---|---|---|
| Registered in `generate_handler!` | 220 | `src/CodeFlow.App/Program.cs` |
| Defined with a registered command | 220 | all `commands/*.rs` |
| Distinct commands invoked by the frontend | 219 | `renderer/src/lib/ipc/commands.ts`, `apiCommands.ts` |
| Registered but never invoked | 1 | `debug_is_running` — `DEAD` |
| Invoked but not registered | 0 | — |
| Duplicate command names | 0 | — |

The registered and defined sets are identical, in both directions. No command reaches the
backend through any path other than these two wrapper files — no `invoke` from
`renderer/src/lib/bridge/host.ts` is imported anywhere else in the frontend.

Nine frontend files do bypass this boundary, but for *non-command* the shell APIs (window
controls, dialogs, opener, OS detection, updater, webview drag-and-drop). They are
inventoried in `02-bootstrap-platform.md`, because their replacements live in the Electron
shell rather than in the C# core.

## Parameter conventions

- **Caller parameters** are what the frontend passes. the transport deserialises them from the
  `invoke` payload object; the frontend sends `camelCase` and the transport maps it to the the sidecar
  `snake_case` parameter name.
- **Injected** parameters are supplied by the shell, not by the caller: `State<Db>`,
  `State<ApiRegistry>`, `State<TerminalRegistry>`, `State<WatcherRegistry>`, `AppHandle`,
  `Window`. They are listed so the C# core knows which ambient dependencies each command
  needs, and are never part of the IPC payload.
- A return type of `T` reaches the frontend as a resolved promise of `T` or
  a rejected promise whose value is the `string`. **Those strings are not free text** —
  several are parsed by the frontend (see `13-cross-language-contracts.md`) and must be
  reproduced exactly.

## Events

Thirteen event names, 23 emit call sites, **19 distinct (name, producer) pairs**. Events
are broadcast globally — `app.emit(...)`; there is no `emit_to` anywhere in the tree, so
every window receives every event and filtering is the frontend's job (each payload
carries the id it belongs to).

| Event | Producer | Payload | Fires when |
|---|---|---|---|
| `ai:output` | `src/CodeFlow.App/Ai/AiRunRegistry.cs` | `{ runId, stream: "stdout" \| "stderr", line }` | Every line an AI CLI writes, as it writes it. The UI dims `stderr` because most CLIs use it for progress chatter, not failures. |
| `git:progress` | `src/CodeFlow.App/Git/GitNetwork.cs` (stdout) | `{ op, line }` | Each line of a streamed `git` network operation. |
| `git:progress` | `src/CodeFlow.App/Git/GitNetwork.cs` (stderr) | `{ op, line }` | Same shape; `git` writes progress to stderr. |
| `git:done` | `src/CodeFlow.App/Git/GitNetwork.cs` | `{ op, success, message }` | The streamed `git` process exited. `message` falls back to `git {op} exited with {status}` when both streams were empty. |
| `terminal:output` | `src/CodeFlow.App/Terminal/TerminalRegistry.cs` | `{ id, data }` | PTY bytes read, decoded lossily as UTF-8. |
| `terminal:exit` | `src/CodeFlow.App/Terminal/TerminalRegistry.cs` | `{ id }` | The PTY reader hit EOF. |
| `debug:paused` | not implemented (deferred) | `PausedEvent { reason, frames[] }` | DAP adapter reported a stop and the stack trace resolved. |
| `debug:paused` | not implemented (deferred) | `PausedEvent { reason, frames[] }` | V8 Inspector `Debugger.paused`. |
| `debug:resumed` | not implemented (deferred) | `()` | Execution continued. |
| `debug:resumed` | not implemented (deferred) | `()` | Execution continued. |
| `debug:output` | not implemented (deferred) | `OutputEvent { kind, text }` | Adapter `output` event. |
| `debug:output` | not implemented (deferred), `:422` | `OutputEvent { kind, text }` | Console API call, and raw process output. |
| `debug:terminated` | not implemented (deferred), `:297` | `()` | Adapter terminated, and reader loop ended. |
| `debug:terminated` | not implemented (deferred) | `()` | Inspector session ended. |
| `repo:fs-changed` | `src/CodeFlow.App/Files/RepoWatcher.cs` | `{ repoPath }` | The working tree changed, subject to the watcher's throttle. |
| `skills:progress` | `src/CodeFlow.App/Workspaces/SkillCommands.cs`, `:71` | `{ line }` | Each line of a skill install subprocess (both streams). |
| `api:stream-message` | `src/CodeFlow.App/ApiClient/WebSocketStream.cs` | `StreamMessage` | A frame arrived on a live WebSocket or Socket.IO connection. |
| `api:stream-message` | `src/CodeFlow.App/ApiClient/MqttConnection.cs` | `StreamMessage` | A message arrived on a subscribed MQTT topic. |
| `api:stream-status` | `src/CodeFlow.App/ApiClient/WebSocketStream.cs` | `StreamStatusEvent` | A WebSocket/Socket.IO connection changed state. |
| `api:stream-status` | `src/CodeFlow.App/ApiClient/MqttConnection.cs` | `StreamStatusEvent` | An MQTT connection changed state. |

The four `debug:*` names each have **two independent producers** with **one shared payload
type** — not implemented (deferred) imports `PausedEvent`, `OutputEvent`, `StackFrame` and `Variable` from
not implemented (deferred). The port must keep that single contract: a frontend consumer cannot tell,
and must not need to tell, which backend produced the event.

The two `api:*` names are the only ones referenced through constants
(`EVENT_STREAM_MESSAGE` / `EVENT_STREAM_STATUS`, `src/CodeFlow.App/ApiClient/ApiModels.cs`) rather than string
literals at the call site.

Every one of the 13 names has exactly one `listen` wrapper in `renderer/src/lib/ipc/events.ts`.

## Commands

Grouped by defining file, in registration order. `Injected` lists the the shell-supplied
dependencies; it is not part of the payload.

### `src/CodeFlow.App/Platform/AppCommands.cs` — 2 commands → [02-bootstrap-platform](02-bootstrap-platform.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `quit_app`<br><sub>`src/CodeFlow.App/Platform/AppCommands.cs`</sub> | — | `()` | AppHandle | `quitApp` |
| `reset_app_data`<br><sub>`src/CodeFlow.App/Platform/AppCommands.cs`</sub> | — | `Result&lt;(), string&gt;` | AppHandle | `resetAppData` |

### `src/CodeFlow.App/Workspaces/WorkspaceCommands.cs` — 12 commands → [09-workspace-scoped](09-workspace-scoped.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `pick_folder`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs` · async</sub> | — | `Option&lt;string&gt;` | AppHandle | `pickFolder` |
| `default_clone_dir`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | — | `string` | — | `defaultCloneDir` |
| `create_workspace`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `name: string`<br>`icon: string`<br>`color: string` | `Result&lt;Workspace, string&gt;` | State | `createWorkspace` |
| `list_workspaces`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | — | `Result&lt;Vec&lt;Workspace&gt;, string&gt;` | State | `listWorkspaces` |
| `delete_workspace`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `deleteWorkspace` |
| `rename_workspace`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `id: string`<br>`name: string` | `Result&lt;(), string&gt;` | State | `renameWorkspace` |
| `update_workspace_color`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `id: string`<br>`color: string` | `Result&lt;(), string&gt;` | State | `updateWorkspaceColor` |
| `update_workspace_git_identity`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `id: string`<br>`name: Option&lt;string&gt;`<br>`email: Option&lt;string&gt;` | `Result&lt;(), string&gt;` | State | `updateWorkspaceGitIdentity` |
| `create_project`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `input: NewProject` | `Result&lt;Project, string&gt;` | State | `createProject` |
| `list_projects`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `workspace_id: string` | `Result&lt;Vec&lt;Project&gt;, string&gt;` | State | `listProjects` |
| `get_project`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `id: string` | `Result&lt;Option&lt;Project&gt;, string&gt;` | State | `getProject` |
| `delete_project`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `deleteProject` |
| `move_project_to_workspace`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `id: string`<br>`workspace_id: string` | `Result&lt;(), string&gt;` | State | `moveProjectToWorkspace` |
| `update_project_color`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `id: string`<br>`color: string` | `Result&lt;(), string&gt;` | State | `updateProjectColor` |

### `src/CodeFlow.App/Git/GitCommands.cs` — 41 commands → [04-git](04-git.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `get_status`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string` | `Result&lt;repo.RepoStatusInfo, string&gt;` | — | `getStatus` |
| `list_commits`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`all_refs: bool`<br>`limit: int` | `Result&lt;Vec&lt;graph.CommitInfo&gt;, string&gt;` | — | `listCommits` |
| `list_unpushed_commits`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string` | `Result&lt;Vec&lt;graph.CommitInfo&gt;, string&gt;` | — | `listUnpushedCommits` |
| `list_branches`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string` | `Result&lt;Vec&lt;branch.BranchInfo&gt;, string&gt;` | — | `listBranches` |
| `create_branch`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`name: string`<br>`start_point: Option&lt;string&gt;` | `Result&lt;(), string&gt;` | — | `createBranch` |
| `delete_branch`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`name: string`<br>`is_remote: bool` | `Result&lt;(), string&gt;` | — | `deleteBranch` |
| `checkout_local_branch`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`name: string` | `Result&lt;(), string&gt;` | — | `checkoutLocalBranch` |
| `checkout_detached`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`refname: string` | `Result&lt;(), string&gt;` | — | `checkoutDetached` |
| `checkout_remote_tracking`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`remote_branch: string` | `Result&lt;string, string&gt;` | — | `checkoutRemoteTracking` |
| `list_stashes`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string` | `Result&lt;Vec&lt;stash.StashInfo&gt;, string&gt;` | — | `listStashes` |
| `stash_save`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`message: Option&lt;string&gt;`<br>`include_untracked: bool` | `Result&lt;(), string&gt;` | — | `stashSave` |
| `stash_apply`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`index: int` | `Result&lt;string, string&gt;`<br><sub>`"applied"` \| `"conflicts"` \| `"not_found"` \| `"uncommitted_changes"` \| `"unknown"`</sub> | — | `stashApply` |
| `stash_pop`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`index: int` | `Result&lt;string, string&gt;`<br><sub>same five values</sub> | — | `stashPop` |
| `stash_drop`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`index: int` | `Result&lt;(), string&gt;` | — | `stashDrop` |
| `rename_stash`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`index: int`<br>`new_message: string` | `Result&lt;(), string&gt;` | — | `renameStash` |
| `get_working_diff`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string` | `Result&lt;Vec&lt;`FileDiffInfo`&gt;, string&gt;` | — | `getWorkingDiff` |
| `get_staged_diff`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string` | `Result&lt;Vec&lt;`FileDiffInfo`&gt;, string&gt;` | — | `getStagedDiff` |
| `get_commit_diff`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`oid: string` | `Result&lt;Vec&lt;`FileDiffInfo`&gt;, string&gt;` | — | `getCommitDiff` |
| `list_commit_files`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`oid: string` | `Result&lt;Vec&lt;`CommitFileInfo`&gt;, string&gt;` | — | `listCommitFiles` |
| `get_commit_file_diff`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`oid: string`<br>`file_path: string`<br>`old_path: string?` | `Result&lt;Vec&lt;`FileDiffInfo`&gt;, string&gt;` | — | `getCommitFileDiff` |
| `stage_file`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`file_path: string` | `Result&lt;(), string&gt;` | — | `stageFile` |
| `stage_all`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string` | `Result&lt;(), string&gt;` | — | `stageAll` |
| `unstage_file`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`file_path: string` | `Result&lt;(), string&gt;` | — | `unstageFile` |
| `unstage_all`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string` | `Result&lt;(), string&gt;` | — | `unstageAll` |
| `discard_file_changes`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`file_path: string` | `Result&lt;(), string&gt;` | — | `discardFileChanges` |
| `discard_all_changes`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string` | `Result&lt;(), string&gt;` | — | `discardAllChanges` |
| `commit`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`message: string`<br>`author_name: Option&lt;string&gt;`<br>`author_email: Option&lt;string&gt;` | `Result&lt;string, string&gt;` | — | `commitChanges` |
| `reset_to_commit`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`oid: string`<br>`mode: string` | `Result&lt;(), string&gt;` | — | `resetToCommit` |
| `list_remotes`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string` | `Result&lt;Vec&lt;remotes.RemoteInfo&gt;, string&gt;` | — | `listRemotes` |
| `set_remote_url`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`name: string`<br>`url: string` | `Result&lt;(), string&gt;` | — | `setRemoteUrl` |
| `get_git_identity`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | — | `Result&lt;identity.GitIdentity, string&gt;` | — | `getGitIdentity` |
| `set_git_identity`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `name: string`<br>`email: string` | `Result&lt;(), string&gt;` | — | `setGitIdentity` |
| `merge_branch`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`branch_name: string` | `Result&lt;merge.MergeOutcome, string&gt;` | — | `mergeBranch` |
| `is_merging`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string` | `Result&lt;bool, string&gt;` | — | `isMerging` |
| `list_conflicts`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string` | `Result&lt;Vec&lt;merge.ConflictFile&gt;, string&gt;` | — | `listConflicts` |
| `resolve_conflict_side`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`rel_path: string`<br>`side: string` | `Result&lt;(), string&gt;` | — | `resolveConflictSide` |
| `mark_conflict_resolved`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`rel_path: string` | `Result&lt;(), string&gt;` | — | `markConflictResolved` |
| `complete_merge`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string`<br>`message: string` | `Result&lt;string, string&gt;` | — | `completeMerge` |
| `abort_merge`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs`</sub> | `repo_path: string` | `Result&lt;(), string&gt;` | — | `abortMerge` |
| `git_clone`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs` · async</sub> | `url: string`<br>`dest: string` | `Result&lt;(), string&gt;` | AppHandle | `gitClone` |
| `git_fetch`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs` · async</sub> | `repo_path: string`<br>`remote_name: Option&lt;string&gt;` | `Result&lt;(), string&gt;` | AppHandle | `gitFetch` |
| `git_pull`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs` · async</sub> | `repo_path: string` | `Result&lt;(), string&gt;` | AppHandle | `gitPull` |
| `git_push`<br><sub>`src/CodeFlow.App/Git/GitCommands.cs` · async</sub> | `repo_path: string`<br>`set_upstream: bool` | `Result&lt;(), string&gt;` | AppHandle | `gitPush` |

### `src/CodeFlow.App/Git/Checkpoints.cs` — 3 commands → [04-git](04-git.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `list_ai_checkpoints`<br><sub>`src/CodeFlow.App/Git/Checkpoints.cs`</sub> | `repo_path: string` | `Result&lt;Vec&lt;checkpoint.CheckpointInfo&gt;, string&gt;` | — | `listAiCheckpoints` |
| `restore_ai_checkpoint`<br><sub>`src/CodeFlow.App/Git/Checkpoints.cs`</sub> | `repo_path: string`<br>`checkpoint_id: string` | `Result&lt;Vec&lt;string&gt;, string&gt;` | — | `restoreAiCheckpoint` |
| `delete_ai_checkpoint`<br><sub>`src/CodeFlow.App/Git/Checkpoints.cs`</sub> | `repo_path: string`<br>`checkpoint_id: string` | `Result&lt;(), string&gt;` | — | `deleteAiCheckpoint` |

### `src/CodeFlow.App/Workspaces/WorkspaceCommands.cs` — 21 commands → [09-workspace-scoped](09-workspace-scoped.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `get_setting`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `key: string` | `Result&lt;Option&lt;string&gt;, string&gt;` | State | `getSetting` |
| `set_setting`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `key: string`<br>`value: string` | `Result&lt;(), string&gt;` | State | `setSetting` |
| `get_workspace_prompt`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `workspace_id: string`<br>`kind: string` | `Result&lt;string, string&gt;` | State | `getWorkspacePrompt` |
| `set_workspace_prompt`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `workspace_id: string`<br>`kind: string`<br>`content: string` | `Result&lt;(), string&gt;` | State | `setWorkspacePrompt` |
| `default_workspace_prompt`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `kind: string` | `string` | — | `defaultWorkspacePrompt` |
| `list_review_runs`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `workspace_id: string` | `Result&lt;Vec&lt;ReviewRunSummary&gt;, string&gt;` | State | `listReviewRuns` |
| `get_review_run`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `id: string` | `Result&lt;Option&lt;ReviewRunDetail&gt;, string&gt;` | State | `getReviewRun` |
| `mark_review_finding`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `run_id: string`<br>`finding_id: string`<br>`estado: string`<br>`motivo: Option&lt;string&gt;` | `Result&lt;(), string&gt;` | State | `markReviewFinding` |
| `delete_review_run`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `deleteReviewRun` |
| `delete_review_runs_for_pr`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `project_id: string`<br>`pr_id: long` | `Result&lt;(), string&gt;` | State | `deleteReviewRunsForPr` |
| `purge_workspace_review_runs`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `workspace_id: string` | `Result&lt;(), string&gt;` | State | `purgeWorkspaceReviewRuns` |
| `export_review_runs`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `workspace_id: string`<br>`id: Option&lt;string&gt;`<br>`dest_dir: string` | `Result&lt;int, string&gt;` | State | `exportReviewRuns` |
| `list_workspace_agents`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `workspace_id: string` | `Result&lt;Vec&lt;WorkspaceAgent&gt;, string&gt;` | State | `listWorkspaceAgents` |
| `upsert_workspace_agent`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `id: Option&lt;string&gt;`<br>`workspace_id: string`<br>`name: string`<br>`role: string`<br>`provider: string`<br>`model: string`<br>`prompt: string`<br>`enabled: bool` | `Result&lt;WorkspaceAgent, string&gt;` | State | `upsertWorkspaceAgent` |
| `delete_workspace_agent`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `deleteWorkspaceAgent` |
| `list_review_contexts`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `workspace_id: string` | `Result&lt;Vec&lt;ReviewContext&gt;, string&gt;` | State | `listReviewContexts` |
| `upsert_review_context`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `id: Option&lt;string&gt;`<br>`workspace_id: string`<br>`name: string`<br>`content: string`<br>`enabled: bool` | `Result&lt;ReviewContext, string&gt;` | State | `upsertReviewContext` |
| `delete_review_context`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `deleteReviewContext` |
| `list_workspace_mcps`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `workspace_id: string` | `Result&lt;Vec&lt;WorkspaceMcp&gt;, string&gt;` | State | `listWorkspaceMcps` |
| `upsert_workspace_mcp`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `id: Option&lt;string&gt;`<br>`workspace_id: string`<br>`name: string`<br>`command: string`<br>`args: string`<br>`env: string`<br>`enabled: bool` | `Result&lt;WorkspaceMcp, string&gt;` | State | `upsertWorkspaceMcp` |
| `delete_workspace_mcp`<br><sub>`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `deleteWorkspaceMcp` |

### `src/CodeFlow.App/Workspaces/SkillCommands.cs` — 10 commands → [09-workspace-scoped](09-workspace-scoped.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `install_workspace_skill`<br><sub>`src/CodeFlow.App/Workspaces/SkillCommands.cs` · async</sub> | `workspace_id: string`<br>`source_repo: string`<br>`skill_name: string` | `Result&lt;WorkspaceSkill, string&gt;` | AppHandle, State | `installWorkspaceSkill` |
| `list_workspace_skills`<br><sub>`src/CodeFlow.App/Workspaces/SkillCommands.cs`</sub> | `workspace_id: string` | `Result&lt;Vec&lt;WorkspaceSkill&gt;, string&gt;` | State | `listWorkspaceSkills` |
| `remove_workspace_skill`<br><sub>`src/CodeFlow.App/Workspaces/SkillCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `removeWorkspaceSkill` |
| `set_workspace_skill_enabled`<br><sub>`src/CodeFlow.App/Workspaces/SkillCommands.cs`</sub> | `id: string`<br>`enabled: bool` | `Result&lt;(), string&gt;` | State | `setWorkspaceSkillEnabled` |
| `create_custom_skill`<br><sub>`src/CodeFlow.App/Workspaces/SkillCommands.cs`</sub> | `workspace_id: string`<br>`name: string`<br>`skill_md: string` | `Result&lt;WorkspaceSkill, string&gt;` | State | `createCustomSkill` |
| `import_skill_from_folder`<br><sub>`src/CodeFlow.App/Workspaces/SkillCommands.cs`</sub> | `workspace_id: string`<br>`src_dir: string` | `Result&lt;WorkspaceSkill, string&gt;` | State | `importSkillFromFolder` |
| `list_skill_files`<br><sub>`src/CodeFlow.App/Workspaces/SkillCommands.cs`</sub> | `workspace_id: string`<br>`skill_name: string` | `Result&lt;Vec&lt;string&gt;, string&gt;` | — | `listSkillFiles` |
| `read_skill_file`<br><sub>`src/CodeFlow.App/Workspaces/SkillCommands.cs`</sub> | `workspace_id: string`<br>`skill_name: string`<br>`rel_path: string` | `Result&lt;string, string&gt;` | — | `readSkillFile` |
| `write_skill_file`<br><sub>`src/CodeFlow.App/Workspaces/SkillCommands.cs`</sub> | `workspace_id: string`<br>`skill_name: string`<br>`rel_path: string`<br>`content: string` | `Result&lt;(), string&gt;` | — | `writeSkillFile` |
| `delete_skill_file`<br><sub>`src/CodeFlow.App/Workspaces/SkillCommands.cs`</sub> | `workspace_id: string`<br>`skill_name: string`<br>`rel_path: string` | `Result&lt;(), string&gt;` | — | `deleteSkillFile` |

### `src/CodeFlow.App/Activity/ActivityCommands.cs` — 7 commands → [09-workspace-scoped](09-workspace-scoped.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `list_chat_conversations`<br><sub>`src/CodeFlow.App/Activity/ActivityCommands.cs`</sub> | `project_id: string`<br>`search: Option&lt;string&gt;` | `Result&lt;Vec&lt;ChatConversationSummary&gt;, string&gt;` | State | `listChatConversations` |
| `get_chat_conversation`<br><sub>`src/CodeFlow.App/Activity/ActivityCommands.cs`</sub> | `project_id: string`<br>`session_id: string` | `Result&lt;Vec&lt;ActivityLogEntry&gt;, string&gt;` | State | `getChatConversation` |
| `delete_chat_conversation`<br><sub>`src/CodeFlow.App/Activity/ActivityCommands.cs`</sub> | `project_id: string`<br>`session_id: string` | `Result&lt;(), string&gt;` | State | `deleteChatConversation` |
| `rename_chat_conversation`<br><sub>`src/CodeFlow.App/Activity/ActivityCommands.cs`</sub> | `project_id: string`<br>`session_id: string`<br>`title: string` | `Result&lt;(), string&gt;` | State | `renameChatConversation` |
| `list_job_history`<br><sub>`src/CodeFlow.App/Activity/ActivityCommands.cs`</sub> | `project_id: string` | `Result&lt;Vec&lt;JobHistoryEntry&gt;, string&gt;` | State | `listJobHistory` |
| `rename_job_history_entry`<br><sub>`src/CodeFlow.App/Activity/ActivityCommands.cs`</sub> | `id: string`<br>`label: string` | `Result&lt;(), string&gt;` | State | `renameJobHistoryEntry` |
| `delete_job_history_entry`<br><sub>`src/CodeFlow.App/Activity/ActivityCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `deleteJobHistoryEntry` |

### `src/CodeFlow.App/Security/SecretCommands.cs` — 9 commands → [10-security](10-security.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `set_ado_pat`<br><sub>`src/CodeFlow.App/Security/SecretCommands.cs`</sub> | `org: string`<br>`pat: string` | `Result&lt;(), string&gt;` | — | `setAdoPat` |
| `has_ado_pat`<br><sub>`src/CodeFlow.App/Security/SecretCommands.cs`</sub> | `org: string` | `Result&lt;bool, string&gt;` | — | `hasAdoPat` |
| `delete_ado_pat`<br><sub>`src/CodeFlow.App/Security/SecretCommands.cs`</sub> | `org: string` | `Result&lt;(), string&gt;` | — | `deleteAdoPat` |
| `set_github_token`<br><sub>`src/CodeFlow.App/Security/SecretCommands.cs`</sub> | `host: string`<br>`token: string` | `Result&lt;(), string&gt;` | — | `setGithubToken` |
| `has_github_token`<br><sub>`src/CodeFlow.App/Security/SecretCommands.cs`</sub> | `host: string` | `Result&lt;bool, string&gt;` | — | `hasGithubToken` |
| `delete_github_token`<br><sub>`src/CodeFlow.App/Security/SecretCommands.cs`</sub> | `host: string` | `Result&lt;(), string&gt;` | — | `deleteGithubToken` |
| `set_ai_api_key`<br><sub>`src/CodeFlow.App/Security/SecretCommands.cs`</sub> | `provider: string`<br>`key: string` | `Result&lt;(), string&gt;` | — | `setAiApiKey` |
| `has_ai_api_key`<br><sub>`src/CodeFlow.App/Security/SecretCommands.cs`</sub> | `provider: string` | `Result&lt;bool, string&gt;` | — | `hasAiApiKey` |
| `delete_ai_api_key`<br><sub>`src/CodeFlow.App/Security/SecretCommands.cs`</sub> | `provider: string` | `Result&lt;(), string&gt;` | — | `deleteAiApiKey` |

### `src/CodeFlow.App/Files/WatcherCommands.cs` — 1 commands → [10-security](10-security.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `scan_staged_secrets`<br><sub>`src/CodeFlow.App/Files/WatcherCommands.cs`</sub> | `repo_path: string` | `Result&lt;Vec&lt;SecretHit&gt;, string&gt;` | — | `scanStagedSecrets` |

### `src/CodeFlow.App/Ai/AiCommands.cs` — 14 commands → [05-ai-engines](05-ai-engines.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `generate_commit_message`<br><sub>`src/CodeFlow.App/Ai/AiCommands.cs` · async</sub> | `diff: string`<br>`run_id: Option&lt;string&gt;` | `Result&lt;string, string&gt;` | AppHandle, State | `generateCommitMessage` |
| `cancel_ai_run`<br><sub>`src/CodeFlow.App/Ai/AiCommands.cs`</sub> | `run_id: string` | `bool` | — | `cancelAiRun` |
| `list_ai_models`<br><sub>`src/CodeFlow.App/Ai/AiCommands.cs` · async</sub> | `provider: Option&lt;string&gt;` | `Result&lt;Vec&lt;string&gt;, string&gt;` | State | `listAiModels` |
| `check_ai_provider`<br><sub>`src/CodeFlow.App/Ai/AiCommands.cs` · async</sub> | `provider: string` | `Result&lt;ProviderStatus, string&gt;` | State | `checkAiProvider` |
| `resolve_conflict_with_ai`<br><sub>`src/CodeFlow.App/Ai/AiCommands.cs` · async</sub> | `repo_path: string`<br>`rel_path: string`<br>`run_id: Option&lt;string&gt;` | `Result&lt;string, string&gt;` | AppHandle, State | `resolveConflictWithAi` |
| `default_commit_template`<br><sub>`src/CodeFlow.App/Ai/AiCommands.cs`</sub> | — | `string` | — | `defaultCommitTemplate` |
| `default_review_template`<br><sub>`src/CodeFlow.App/Ai/AiCommands.cs`</sub> | — | `string` | — | `defaultReviewTemplate` |
| `default_analyze_template`<br><sub>`src/CodeFlow.App/Ai/AiCommands.cs`</sub> | — | `string` | — | `defaultAnalyzeTemplate` |
| `default_pr_description_template`<br><sub>`src/CodeFlow.App/Ai/AiCommands.cs`</sub> | — | `string` | — | `defaultPrDescriptionTemplate` |
| `default_resolve_conflict_template`<br><sub>`src/CodeFlow.App/Ai/AiCommands.cs`</sub> | — | `string` | — | `defaultResolveConflictTemplate` |
| ~~`analyze_working_changes`~~ | — | — | — | Superseded by `review_changes` (`Tickets/TicketCommands.cs`), which carries the scope and the ticket axes together. Its body still lives in `AiTurn.AnalyzeChangesAsync`. |
| `resolve_finding_with_ai`<br><sub>`src/CodeFlow.App/Ai/AiCommands.cs` · async</sub> | `project_id: string`<br>`finding_prompt: string`<br>`run_id: Option&lt;string&gt;` | `Result&lt;string, string&gt;` | AppHandle, State | `resolveFindingWithAi` |
| `send_chat_message`<br><sub>`src/CodeFlow.App/Ai/AiCommands.cs` · async</sub> | `project_id: string`<br>`message: string`<br>`session_id: Option&lt;string&gt;`<br>`conversation_id: Option&lt;string&gt;`<br>`run_id: Option&lt;string&gt;`<br>`agent_provider: Option&lt;string&gt;`<br>`agent_model: Option&lt;string&gt;`<br>`agent_prompt: Option&lt;string&gt;` | `Result&lt;ChatReply, string&gt;` | AppHandle, State | `sendChatMessage` |
| `inline_edit_with_ai`<br><sub>`src/CodeFlow.App/Ai/AiCommands.cs` · async</sub> | `rel_path: string`<br>`file_content: string`<br>`selection: string`<br>`instruction: string`<br>`run_id: Option&lt;string&gt;` | `Result&lt;string, string&gt;` | AppHandle, State | `inlineEditWithAi` |

### `src/CodeFlow.App/Review/ReviewCommands.cs` — 22 commands → [07-review-pipeline](07-review-pipeline.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `auto_link_project`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs`</sub> | `project_id: string` | `Result&lt;AutoLinkResult, string&gt;` | State | `autoLinkProject` |
| `ado_list_projects`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `org: string` | `Result&lt;Vec&lt;`AdoProject`&gt;, string&gt;` | — | `adoListProjects` |
| `ado_list_repos`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `org: string`<br>`project: string` | `Result&lt;Vec&lt;`AdoRepo`&gt;, string&gt;` | — | `adoListRepos` |
| `link_project_ado`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs`</sub> | `id: string`<br>`ado_org: string`<br>`ado_project: string`<br>`ado_repo_id: string` | `Result&lt;(), string&gt;` | State | `linkProjectAdo` |
| `unlink_project`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `unlinkProject` |
| `open_repo_in_browser`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs`</sub> | `project_id: string` | `Result&lt;(), string&gt;` | State | `openRepoInBrowser` |
| `open_external_url`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs`</sub> | `url: string` | `Result&lt;(), string&gt;` | — | `openExternalUrl` |
| `list_pull_requests`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `project_id: string` | `Result&lt;Vec&lt;`PullRequestSummary`&gt;, string&gt;` | State | `listPullRequests` |
| `resolve_pr_link`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `url: string` | `Result&lt;PrLinkResolution, string&gt;` | State | `resolvePrLink` |
| `review_pr_from_link`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `url: string`<br>`job_id: string`<br>`level: string`<br>`workspace_id: string`<br>`agent_provider: Option&lt;string&gt;`<br>`agent_model: Option&lt;string&gt;`<br>`agent_prompt: Option&lt;string&gt;` | `Result&lt;string, string&gt;` | AppHandle, State | `reviewPrFromLink` |
| `pr_link_pull_request`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `url: string` | `Result&lt;`PullRequestSummary`, string&gt;` | State | `prLinkPullRequest` |
| `pr_link_comment_threads`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `url: string` | `Result&lt;Vec&lt;`PrCommentThread`&gt;, string&gt;` | State | `prLinkCommentThreads` |
| `pr_link_decision`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `url: string` | `Result&lt;string, string&gt;` | State | `prLinkDecision` |
| `act_on_pr_link`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `url: string`<br>`action: string`<br>`body: Option&lt;string&gt;` | `Result&lt;`PullRequestSummary`, string&gt;` | State | `actOnPrLink` |
| `post_pr_link_review_comment`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `url: string`<br>`items: Vec&lt;PostFindingItem&gt;`<br>`post_summary: bool`<br>`summary: Option&lt;string&gt;` | `Result&lt;(), string&gt;` | State | `postPrLinkReviewComment` |
| `generate_pr_description`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `project_id: string`<br>`source_branch: string`<br>`target_branch: string`<br>`run_id: Option&lt;string&gt;` | `Result&lt;PrDescriptionDraft, string&gt;` | AppHandle, State | `generatePrDescription` |
| `create_pull_request`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `project_id: string`<br>`title: string`<br>`description: string`<br>`source_branch: string`<br>`target_branch: string`<br>`draft: bool` | `Result&lt;`PullRequestSummary`, string&gt;` | State | `createPullRequest` |
| `list_pr_comment_threads`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `project_id: string`<br>`pr_id: long` | `Result&lt;Vec&lt;`PrCommentThread`&gt;, string&gt;` | State | `listPrCommentThreads` |
| `review_pull_request`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `project_id: string`<br>`pr_id: long`<br>`job_id: string`<br>`level: string`<br>`// When an SDD/Harness agent runs this review`<br>`its provider + model + prompt for this run. agent_provider: Option&lt;string&gt;`<br>`agent_model: Option&lt;string&gt;`<br>`agent_prompt: Option&lt;string&gt;` | `Result&lt;string, string&gt;` | AppHandle, State | `reviewPullRequest` |
| `post_pr_review_comment`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `project_id: string`<br>`pr_id: long`<br>`run_id: string`<br>`items: Vec&lt;PostFindingItem&gt;`<br>`post_summary: bool`<br>`summary: Option&lt;string&gt;` | `Result&lt;(), string&gt;` | State | `postPrReviewComment` |
| `pr_review_decision`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `project_id: string`<br>`pr_id: long` | `Result&lt;string, string&gt;` | State | `prReviewDecision` |
| `act_on_pull_request`<br><sub>`src/CodeFlow.App/Review/ReviewCommands.cs` · async</sub> | `project_id: string`<br>`pr_id: long`<br>`action: string`<br>`body: Option&lt;string&gt;` | `Result&lt;PrActionOutcome, string&gt;` | State | `actOnPullRequest` |

### `src/CodeFlow.App/Providers/ProviderCommands.cs` — 2 commands → [06-providers](06-providers.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `link_project_github`<br><sub>`src/CodeFlow.App/Providers/ProviderCommands.cs`</sub> | `id: string`<br>`github_owner: string`<br>`github_repo: string`<br>`github_host: string` | `Result&lt;(), string&gt;` | State | `linkProjectGithub` |
| `github_authenticated_user`<br><sub>`src/CodeFlow.App/Providers/ProviderCommands.cs` · async</sub> | `host: string` | `Result&lt;string, string&gt;` | — | `githubAuthenticatedUser` |

### `src/CodeFlow.App/Files/FileCommands.cs` — 13 commands → [11-files-search-terminal](11-files-search-terminal.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `list_dir`<br><sub>`src/CodeFlow.App/Files/FileCommands.cs`</sub> | `repo_path: string`<br>`sub_path: Option&lt;string&gt;` | `Result&lt;Vec&lt;fsops.FileEntry&gt;, string&gt;` | — | `listDir` |
| `read_file_text`<br><sub>`src/CodeFlow.App/Files/FileCommands.cs`</sub> | `repo_path: string`<br>`rel_path: string` | `Result&lt;string, string&gt;` | — | `readFileText` |
| `write_file_text`<br><sub>`src/CodeFlow.App/Files/FileCommands.cs`</sub> | `repo_path: string`<br>`rel_path: string`<br>`content: string` | `Result&lt;(), string&gt;` | — | `writeFileText` |
| `write_file_bytes`<br><sub>`src/CodeFlow.App/Files/FileCommands.cs`</sub> | `path: string`<br>`contents: Vec&lt;byte&gt;` | `Result&lt;(), string&gt;` | — | `writeFileBytes` |
| `move_path`<br><sub>`src/CodeFlow.App/Files/FileCommands.cs`</sub> | `repo_path: string`<br>`from_rel: string`<br>`dest_dir: string` | `Result&lt;string, string&gt;` | — | `movePath` |
| `create_dir`<br><sub>`src/CodeFlow.App/Files/FileCommands.cs`</sub> | `repo_path: string`<br>`rel_path: string` | `Result&lt;(), string&gt;` | — | `createDir` |
| `create_file`<br><sub>`src/CodeFlow.App/Files/FileCommands.cs`</sub> | `repo_path: string`<br>`rel_path: string` | `Result&lt;(), string&gt;` | — | `createFile` |
| `open_in_default_app`<br><sub>`src/CodeFlow.App/Files/FileCommands.cs`</sub> | `repo_path: string`<br>`rel_path: string` | `Result&lt;(), string&gt;` | — | `openInDefaultApp` |
| `reveal_in_file_manager`<br><sub>`src/CodeFlow.App/Files/FileCommands.cs`</sub> | `path: string` | `Result&lt;(), string&gt;` | — | `revealInFileManager` |
| `open_in_vscode`<br><sub>`src/CodeFlow.App/Files/FileCommands.cs`</sub> | `path: string` | `Result&lt;(), string&gt;` | — | `openInVsCode` |
| `list_repo_files`<br><sub>`src/CodeFlow.App/Files/FileCommands.cs`</sub> | `repo_path: string` | `Result&lt;Vec&lt;string&gt;, string&gt;` | — | `listRepoFiles` |
| `search_repo`<br><sub>`src/CodeFlow.App/Files/FileCommands.cs`</sub> | `repo_path: string`<br>`query: string`<br>`options: `SearchOptions`<br>`max_results: int` | `Result&lt;SearchOptions, string&gt;` | — | `searchRepo` |
| `replace_in_repo`<br><sub>`src/CodeFlow.App/Files/FileCommands.cs`</sub> | `repo_path: string`<br>`query: string`<br>`replacement: string`<br>`options: `SearchOptions`<br>`only_path: Option&lt;string&gt;` | `Result&lt;SearchOptions, string&gt;` | — | `replaceInRepo` |

### `src/CodeFlow.App/Files/WatcherCommands.cs` — 2 commands → [11-files-search-terminal](11-files-search-terminal.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `start_watching`<br><sub>`src/CodeFlow.App/Files/WatcherCommands.cs`</sub> | `repo_path: string` | `Result&lt;(), string&gt;` | AppHandle, State | `startWatching` |
| `stop_watching`<br><sub>`src/CodeFlow.App/Files/WatcherCommands.cs`</sub> | `repo_path: string` | `Result&lt;(), string&gt;` | State | `stopWatching` |

### `src/CodeFlow.App/Terminal/TerminalCommands.cs` — 4 commands → [11-files-search-terminal](11-files-search-terminal.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `open_terminal`<br><sub>`src/CodeFlow.App/Terminal/TerminalCommands.cs`</sub> | `cwd: string` | `Result&lt;string, string&gt;` | AppHandle, State | `openTerminal` |
| `write_terminal`<br><sub>`src/CodeFlow.App/Terminal/TerminalCommands.cs`</sub> | `id: string`<br>`data: string` | `Result&lt;(), string&gt;` | State | `writeTerminal` |
| `resize_terminal`<br><sub>`src/CodeFlow.App/Terminal/TerminalCommands.cs`</sub> | `id: string`<br>`cols: ushort`<br>`rows: ushort` | `Result&lt;(), string&gt;` | State | `resizeTerminal` |
| `close_terminal`<br><sub>`src/CodeFlow.App/Terminal/TerminalCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `closeTerminal` |

### not implemented (deferred) — 10 commands → [12-debugging](12-debugging.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `debug_start`<br><sub>not implemented (deferred) · async</sub> | `cwd: string`<br>`node_binary: Option&lt;string&gt;`<br>`program: string`<br>`args: Vec&lt;string&gt;`<br>`breakpoints: HashMap&lt;string, Vec&lt;uint&gt;&gt;` | `Result&lt;(), string&gt;` | AppHandle | `debugStart` |
| `debug_start_adapter`<br><sub>not implemented (deferred) · async</sub> | `cwd: string`<br>`command: string`<br>`args: Vec&lt;string&gt;`<br>`launch_config: `JsonElement`<br>`breakpoints: HashMap&lt;string, Vec&lt;uint&gt;&gt;` | `Result&lt;(), string&gt;` | AppHandle | `debugStartAdapter` |
| `debug_stop`<br><sub>not implemented (deferred) · async</sub> | — | `Result&lt;(), string&gt;` | — | `debugStop` |
| `debug_continue`<br><sub>not implemented (deferred) · async</sub> | — | `Result&lt;(), string&gt;` | — | `debugContinue` |
| `debug_pause`<br><sub>not implemented (deferred) · async</sub> | — | `Result&lt;(), string&gt;` | — | `debugPause` |
| `debug_step`<br><sub>not implemented (deferred) · async</sub> | `kind: string` | `Result&lt;(), string&gt;` | — | `debugStep` |
| `debug_set_breakpoints`<br><sub>not implemented (deferred) · async</sub> | `breakpoints: HashMap&lt;string, Vec&lt;uint&gt;&gt;` | `Result&lt;(), string&gt;` | — | `debugSetBreakpoints` |
| `debug_properties`<br><sub>not implemented (deferred) · async</sub> | `object_id: string` | `Result&lt;Vec&lt;Variable&gt;, string&gt;` | — | `debugProperties` |
| `debug_evaluate`<br><sub>not implemented (deferred) · async</sub> | `frame_id: string`<br>`expression: string` | `Result&lt;Variable, string&gt;` | — | `debugEvaluate` |
| `debug_is_running` `DEAD`<br><sub>not implemented (deferred)</sub> | — | `bool` | — | **none — `DEAD`** |

### `src/CodeFlow.App/ApiClient/ApiCommands.cs` — 45 commands → [08-api-client](08-api-client.md)

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `api_load_tree`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `workspace_id: string` | `Result&lt;ApiTree, string&gt;` | State | `apiLoadTree` |
| `api_create_collection`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `workspace_id: string`<br>`name: string` | `Result&lt;ApiCollection, string&gt;` | State | `apiCreateCollection` |
| `api_update_collection`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `collection: ApiCollection` | `Result&lt;(), string&gt;` | State | `apiUpdateCollection` |
| `api_delete_collection`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `apiDeleteCollection` |
| `api_duplicate_collection`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `id: string` | `Result&lt;ApiCollection, string&gt;` | State | `apiDuplicateCollection` |
| `api_create_folder`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `collection_id: string`<br>`parent_id: Option&lt;string&gt;`<br>`name: string` | `Result&lt;ApiFolder, string&gt;` | State | `apiCreateFolder` |
| `api_update_folder`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `folder: ApiFolder` | `Result&lt;(), string&gt;` | State | `apiUpdateFolder` |
| `api_delete_folder`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `apiDeleteFolder` |
| `api_create_request`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `collection_id: string`<br>`folder_id: Option&lt;string&gt;`<br>`name: string`<br>`protocol: string`<br>`spec: string` | `Result&lt;ApiRequestRow, string&gt;` | State | `apiCreateRequest` |
| `api_update_request`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `request: ApiRequestRow` | `Result&lt;(), string&gt;` | State | `apiUpdateRequest` |
| `api_delete_request`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `apiDeleteRequest` |
| `api_duplicate_request`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `id: string` | `Result&lt;ApiRequestRow, string&gt;` | State | `apiDuplicateRequest` |
| `api_move_node`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `kind: string`<br>`id: string`<br>`collection_id: string`<br>`parent_id: Option&lt;string&gt;`<br>`index: long` | `Result&lt;(), string&gt;` | State | `apiMoveNode` |
| `api_reorder_collections`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `workspace_id: string`<br>`ids: Vec&lt;string&gt;` | `Result&lt;(), string&gt;` | State | `apiReorderCollections` |
| `api_list_environments`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `workspace_id: string` | `Result&lt;Vec&lt;ApiEnvironment&gt;, string&gt;` | State | `apiListEnvironments` |
| `api_create_environment`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `workspace_id: string`<br>`name: string` | `Result&lt;ApiEnvironment, string&gt;` | State | `apiCreateEnvironment` |
| `api_update_environment`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `environment: ApiEnvironment` | `Result&lt;(), string&gt;` | State | `apiUpdateEnvironment` |
| `api_delete_environment`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `apiDeleteEnvironment` |
| `api_duplicate_environment`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `id: string` | `Result&lt;ApiEnvironment, string&gt;` | State | `apiDuplicateEnvironment` |
| `api_list_history`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `workspace_id: string`<br>`limit: long` | `Result&lt;Vec&lt;ApiHistoryEntry&gt;, string&gt;` | State | `apiListHistory` |
| `api_add_history`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `entry: ApiHistoryEntry` | `Result&lt;(), string&gt;` | State | `apiAddHistory` |
| `api_delete_history`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `apiDeleteHistory` |
| `api_clear_history`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `workspace_id: string` | `Result&lt;(), string&gt;` | State | `apiClearHistory` |
| `api_list_cookies`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `workspace_id: string` | `Result&lt;Vec&lt;ApiCookie&gt;, string&gt;` | State | `apiListCookies` |
| `api_upsert_cookie`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `cookie: ApiCookie` | `Result&lt;(), string&gt;` | State | `apiUpsertCookie` |
| `api_delete_cookie`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `apiDeleteCookie` |
| `api_clear_cookies`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `workspace_id: string` | `Result&lt;(), string&gt;` | State | `apiClearCookies` |
| `api_send_http`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs` · async</sub> | `request: HttpSendRequest` | `Result&lt;HttpResponse, string&gt;` | — | `apiSendHttp` |
| `api_send_http_tracked`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs` · async</sub> | `id: string`<br>`request: HttpSendRequest` | `Result&lt;HttpResponse, string&gt;` | State | `apiSendHttpTracked` |
| `api_cancel_http`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `apiCancelHttp` |
| `api_ws_connect`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs` · async</sub> | `id: string`<br>`request: WsConnectRequest` | `Result&lt;(), string&gt;` | AppHandle | `apiWsConnect` |
| `api_ws_send`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `id: string`<br>`payload: string`<br>`binary: bool` | `Result&lt;(), string&gt;` | State | `apiWsSend` |
| `api_socketio_connect`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs` · async</sub> | `id: string`<br>`request: SocketIoConnectRequest` | `Result&lt;(), string&gt;` | AppHandle | `apiSocketioConnect` |
| `api_socketio_emit`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `id: string`<br>`event: string`<br>`payload_json: string` | `Result&lt;(), string&gt;` | State | `apiSocketioEmit` |
| `api_mqtt_connect`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs` · async</sub> | `id: string`<br>`request: MqttConnectRequest` | `Result&lt;(), string&gt;` | AppHandle | `apiMqttConnect` |
| `api_mqtt_publish`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `id: string`<br>`topic: string`<br>`payload: string`<br>`qos: byte`<br>`retain: bool` | `Result&lt;(), string&gt;` | State | `apiMqttPublish` |
| `api_mqtt_subscribe`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `id: string`<br>`topic: string`<br>`qos: byte` | `Result&lt;(), string&gt;` | State | `apiMqttSubscribe` |
| `api_mqtt_unsubscribe`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `id: string`<br>`topic: string` | `Result&lt;(), string&gt;` | State | `apiMqttUnsubscribe` |
| `api_stream_disconnect`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `id: string` | `Result&lt;(), string&gt;` | State | `apiStreamDisconnect` |
| `api_grpc_describe`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs` · async</sub> | `request: GrpcDescribeRequest` | `Result&lt;Vec&lt;GrpcServiceInfo&gt;, string&gt;` | — | `apiGrpcDescribe` |
| `api_grpc_call`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs` · async</sub> | `id: string`<br>`request: GrpcCallRequest` | `Result&lt;GrpcResponse, string&gt;` | State | `apiGrpcCall` |
| `api_read_file_base64`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `path: string` | `Result&lt;FileBase64, string&gt;` | — | `apiReadFileBase64` |
| `api_pick_file`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs` · async</sub> | `extensions: Vec&lt;string&gt;` | `Option&lt;string&gt;` | AppHandle | `apiPickFile` |
| `api_save_file`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs` · async</sub> | `default_name: string`<br>`contents: string` | `Result&lt;Option&lt;string&gt;, string&gt;` | AppHandle | `apiSaveFile` |
| `api_read_text_file`<br><sub>`src/CodeFlow.App/ApiClient/ApiCommands.cs`</sub> | `path: string` | `Result&lt;string, string&gt;` | — | `apiReadTextFile` |

### `src/CodeFlow.App/Tickets/TicketCommands.cs` — 13 commands → [14-work-items](14-work-items.md)

Every command here reads. Commenting and state transitions are a later, separately requested step,
so nothing on this surface can alter a board.

| Command | Caller parameters | Returns | Injected | TS wrapper |
|---|---|---|---|---|
| `update_workspace_ticket_account`<br><sub>`src/CodeFlow.App/Tickets/TicketCommands.cs`</sub> | `workspaceId: string`<br>`org: Option&lt;string&gt;`<br>`project: Option&lt;string&gt;` | `()` | State | `updateWorkspaceTicketAccount` |
| `resolve_ticket_account`<br><sub>`src/CodeFlow.App/Tickets/TicketCommands.cs`</sub> | `projectId: string` | `TicketAccount` | State | `resolveTicketAccount` |
| `resolve_ticket_link`<br><sub>`src/CodeFlow.App/Tickets/TicketCommands.cs`</sub> | `text: string` | `Option&lt;TicketLinkRef&gt;` | — | `resolveTicketLink` |
| `suggest_ticket_for_branch`<br><sub>`src/CodeFlow.App/Tickets/TicketCommands.cs`</sub> | `branch: string` | `Option&lt;TicketSuggestion&gt;` | — | `suggestTicketForBranch` |
| `sync_ticket`<br><sub>`src/CodeFlow.App/Tickets/TicketCommands.cs` · async</sub> | `org: string`<br>`project: string`<br>`externalId: string` | `Result&lt;Ticket, string&gt;` | State, HttpClient | `syncTicket` |
| `get_ticket`<br><sub>`src/CodeFlow.App/Tickets/TicketCommands.cs`</sub> | `ticketId: string` | `Option&lt;Ticket&gt;` | State | `getTicket` |
| `list_tickets`<br><sub>`src/CodeFlow.App/Tickets/TicketCommands.cs`</sub> | `projectId: string` | `Vec&lt;TicketWithLinks&gt;` | State | `listTickets` |
| `get_ticket_criteria`<br><sub>`src/CodeFlow.App/Tickets/TicketCommands.cs`</sub> | `ticketId: string` | `TicketCriteria` | State | `getTicketCriteria` |
| `link_branch_ticket`<br><sub>`src/CodeFlow.App/Tickets/TicketCommands.cs`</sub> | `projectId: string`<br>`branch: string`<br>`ticketId: string` | `()` | State | `linkBranchTicket` |
| `unlink_branch_ticket`<br><sub>`src/CodeFlow.App/Tickets/TicketCommands.cs`</sub> | `projectId: string`<br>`branch: string` | `()` | State | `unlinkBranchTicket` |
| `ticket_for_branch`<br><sub>`src/CodeFlow.App/Tickets/TicketCommands.cs`</sub> | `projectId: string`<br>`branch: string` | `Option&lt;Ticket&gt;` | State | `ticketForBranch` |
| `list_sprint_tickets`<br><sub>`src/CodeFlow.App/Tickets/TicketCommands.cs` · async</sub> | `org: string`<br>`project: string`<br>`team: Option&lt;string&gt;` | `Result&lt;Vec&lt;TicketSummary&gt;, string&gt;` | HttpClient | `listSprintTickets` |
| `list_my_tickets`<br><sub>`src/CodeFlow.App/Tickets/TicketCommands.cs` · async</sub> | `org: string`<br>`project: string` | `Result&lt;Vec&lt;TicketSummary&gt;, string&gt;` | HttpClient | `listMyTickets` |
| `preview_ticket`<br><sub>`src/CodeFlow.App/Tickets/TicketCommands.cs` · async</sub> | `org: string`<br>`project: string`<br>`externalId: string` | `Result&lt;Option&lt;TicketSummary&gt;, string&gt;` | HttpClient | `previewTicket` |
| `list_ticket_reviews`<br><sub>`src/CodeFlow.App/Tickets/TicketCommands.cs`</sub> | `projectId: string`<br>`branch: string` | `Result&lt;Vec&lt;TicketReviewResult&gt;, string&gt;` | Database | `listTicketReviews` |
| `review_changes`<br><sub>`src/CodeFlow.App/Tickets/TicketCommands.cs` · async</sub> | `projectId: string`<br>`jobId: string`<br>`branch: string`<br>`scope: "working" \| "branch"`<br>`withTicket: bool`<br>`baseRef: Option&lt;string&gt;`<br>`level: string`<br>`agentProvider: Option&lt;string&gt;`<br>`agentModel: Option&lt;string&gt;`<br>`agentPrompt: Option&lt;string&gt;` | `Result&lt;string, string&gt;` | Database, AiRunRegistry, HttpClient | `reviewChanges` |
