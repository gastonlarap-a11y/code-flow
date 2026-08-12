# 04 — Git

## Scope

- `src/CodeFlow.App/Git/` — `RepoStatus.cs`, `Diff.cs`, `Branches.cs`, `Merge.cs`, `Stash.cs`,
  `CommitGraph.cs`, `Checkpoints.cs`, `Remotes.cs`, `Identity.cs`, `GitNetwork.cs`
- `src/CodeFlow.App/Git/GitCommands.cs` — the 46 command registrations

`GitCommands.cs` is registration only: each of the 46 commands is a direct call into the function
of the same or an obviously related name in the files above. Parameters and return types are not
repeated here — see [01-ipc-surface.md](01-ipc-surface.md).

## Commands

All 43 `src/CodeFlow.App/Git/GitCommands.cs` commands and all 3 `src/CodeFlow.App/Git/Checkpoints.cs` commands are
synchronous thin wrappers except the four network operations, which are `async` and stream
progress (see GIT-034).

| Command | What calling it does |
|---|---|
| `get_status` | Reads `git2::Repository.statuses` and buckets every entry into staged/unstaged/untracked/conflicted, plus current branch and detached-HEAD flag. |
| `list_commits` | Walks history (current HEAD, or every local+remote ref) topologically, newest first, up to `limit`. |
| `list_unpushed_commits` | Walks commits reachable from HEAD but not from its upstream; empty list if HEAD is detached or has no upstream. |
| `list_branches` | Lists local and remote branches with ahead/behind counts against upstream. |
| `create_branch` | Creates a branch at `start_point` (revparsed) or at HEAD if omitted. Does not check it out. |
| `delete_branch` | Deletes a local or remote-tracking branch reference. |
| `checkout_local_branch` | Checks out an existing local branch's tree and moves HEAD to it. |
| `checkout_detached` | Checks out any revparse-able ref/commit/tag/SHA without moving a branch pointer. |
| `checkout_remote_tracking` | Creates (or reuses) a local branch tracking `<remote>/<name>` and checks it out. |
| `list_stashes` | Lists every stash with its reflog index, message, and commit oid. |
| `stash_save` | Stashes the working tree (and index) into a new stash entry. |
| `stash_apply` | Applies a stash by index without removing it, and reports how that went (GIT-015). |
| `stash_pop` | Applies a stash by index and removes it only if it applied without conflicts (GIT-015). |
| `stash_drop` | Removes a stash entry by index. |
| `rename_stash` | Replaces a stash's reflog message via drop-then-reappend (GIT-014). |
| `get_working_diff` | Full-file-context diff of the working tree against the index (includes untracked content). |
| `get_staged_diff` | Full-file-context diff of the index against HEAD's tree. |
| `get_commit_diff` | Diff of a single commit against its first parent (root commits diff against an empty tree). |
| `list_commit_files` | The paths and statuses one commit touched, with no content at all (GIT-035). |
| `get_commit_file_diff` | Full-file-context diff of one file inside one commit, filtered by pathspec (GIT-035). |
| `stage_file` | Adds (or, if absent on disk, removes) one path in the index. |
| `stage_all` | Adds every path matching `*` to the index (equivalent to `git add -A`). |
| `unstage_file` | Resets one path in the index to HEAD's version. |
| `unstage_all` | Rewrites the whole index from HEAD's tree. |
| `discard_file_changes` | Force-checks-out one path from the index onto the working tree. |
| `discard_all_changes` | Reverts every tracked working-tree change to the index and deletes every untracked file (GIT-011). |
| `commit` | Writes the index as a tree and commits it onto HEAD, signed per the GIT-028 precedence (explicit author → workspace identity → configured signature). |
| `reset_to_commit` | Moves HEAD (and the current branch) to a commit with soft/mixed/hard semantics (GIT-002). |
| `list_remotes` | Lists configured remotes and their URLs. |
| `set_remote_url` | Sets both fetch and push URL for a remote to the same value. |
| `get_git_identity` | Reads the global `user.name`/`user.email` from the default git config. |
| `set_git_identity` | Writes the global `user.name`/`user.email`. |
| `merge_branch` | Merges a local or remote branch into HEAD: up-to-date, fast-forward, clean merge commit, or conflicts. A created merge commit signs per GIT-036. |
| `is_merging` | True while `MERGE_HEAD` / merge state is active. |
| `list_conflicts` | Lists paths with unresolved index conflicts. |
| `resolve_conflict_side` | Takes one side (ours/theirs) of a conflict wholesale, writes it to disk, and stages it. |
| `mark_conflict_resolved` | Stages whatever is currently on disk for a conflicted path, as-is. |
| `complete_merge` | Commits the resolved index as a two-parent merge commit (signed per GIT-036) and clears merge state. |
| `abort_merge` | Force-checks-out HEAD's tree and clears merge state, discarding the in-progress merge. |
| `git_clone` | Shells out to `git clone <url> <dest>`, streaming progress. |
| `git_fetch` | Shells out to `git fetch <remote>` (default `origin`), streaming progress. |
| `git_pull` | Shells out to `git pull`, streaming progress. |
| `git_push` | Shells out to `git push` or `git push -u origin <branch>`, streaming progress. |
| `list_ai_checkpoints` | Lists AI-run checkpoints newest first, each with the paths currently differing from it. |
| `restore_ai_checkpoint` | Writes every currently-differing path back to its checkpointed content; deletes paths absent from the snapshot. |
| `delete_ai_checkpoint` | Deletes a checkpoint ref. |

## Data model / contracts

- `RepoStatusInfo { staged, unstaged, untracked, conflicted: IReadOnlyList<FileStatusEntry>, current_branch: string?, is_detached: bool }` — `FileStatusEntry { path, status }`, where `status` is one of the string literals in GIT-001.
- `CommitInfo { id, short_id, summary, author_name, author_email, timestamp (long, UTC seconds), parent_ids: IReadOnlyList<string>, refs: IReadOnlyList<string> }` — `short_id` is the first 7 hex characters of `id` (fewer if `id` is somehow shorter). `refs` holds every local branch, remote branch and tag shorthand pointing at that commit (annotated tags peeled to the commit they point at).
- `BranchInfo { name, is_head, is_remote, upstream: string?, ahead: int, behind: int, target: string? }`.
- `StashInfo { index: int, message: string, oid: string }`.
- `FileDiffInfo { old_path: string?, new_path: string?, status: string, hunks: IReadOnlyList<DiffHunkInfo> }`; `DiffHunkInfo { header: string, lines: IReadOnlyList<DiffLine> }`; `DiffLine { origin: string (one char, e.g. "+", "-", " "), content, old_lineno: uint?, new_lineno: uint? }`. `status` is one of the `Delta` labels in GIT-010.
- `CommitFileInfo { old_path: string?, new_path: string?, status: string }` — `FileDiffInfo` without its hunks, and the whole point of the type: the graph lists a commit's files without paying for their content (GIT-035). `status` comes from the same `Delta` labels as `FileDiffInfo`.
- `MergeOutcome { status: string, conflicts: IReadOnlyList<string> }` — `status` ∈ `{"up_to_date", "fast_forward", "merged", "conflicts"}`.
- `ConflictFile { path: string }`.
- `ConflictVersions { base, ours, theirs: string }` — lossily UTF-8-decoded blob content from the merge index's ancestor/our/their stages; empty string when that side has no entry (added-on-one-side or deleted). Not exposed through a a registered command directly — `resolve_conflict_with_ai` (`src/CodeFlow.App/Ai/AiCommands.cs`, owned by `05-ai-engines.md`) calls `conflict_versions` internally.
- `RemoteInfo { name, url }`.
- `GitIdentity { name: string?, email: string? }`.
- `CheckpointInfo { id: string, kind: string, created_at: long (unix seconds), changed_paths: IReadOnlyList<string> }`.
- `GitProgressEvent { op: string, line: string }`, `GitDoneEvent { op: string, success: bool, message: string }` — see `01-ipc-surface.md`'s event table for producers.

## Rules

### GIT-001 Status buckets a file into exactly one of four categories
**Implementation**: `src/CodeFlow.App/Git/RepoStatus.cs`
**Behaviour**: `get_status` calls `git2::Repository.statuses` with `include_untracked(true)` and `recurse_untracked_dirs(true)`, then for each entry picks the *first* matching bucket in this fixed priority order: conflicted → staged (added/modified/deleted/renamed/typechange, in that sub-order) → untracked → unstaged (modified/deleted/renamed/typechange, in that sub-order). A path that is simultaneously staged and further modified in the working tree is reported once, as staged only — the unstaged branches are never reached for it because `is_conflicted`/`is_index_*` are checked before `is_wt_*`. Entries matching none of these (e.g. ignored) are silently dropped.
**Inputs / outputs**: `repo_path: string` → `RepoStatusInfo`.
**Edge cases**: Detached HEAD → `current_branch: None`, `is_detached: true`. A file with no `entry.path()` (rare libgit2 edge case for some renames) is skipped entirely.
**Frontend dependency**: sidebar Changes panel and commit gating.
**Markers**: `BUG-GIT-a` **closed** — rename detection is on in `StatusRequest` (both halves), so the renamed branches in the priority order above are live. Fixed alongside GIT-010/011.

### GIT-002 Reset mode selects which of {HEAD, index, working tree} moves
**Implementation**: `src/CodeFlow.App/Git/RepoStatus.cs`
**Behaviour**: `reset_to_commit(path, target_oid, mode)` resolves `target_oid` to a commit object and calls `repo.reset()` with `ResetType.Soft` for `mode == "soft"`, `ResetType.Hard` for `mode == "hard"`, and `ResetType.Mixed` for every other string (including typos — there is no validation, unrecognised modes silently become "mixed"). Per libgit2 semantics: **soft** moves HEAD/branch only, leaving the index and working tree untouched (the target's diff from the old HEAD appears fully staged). **mixed** moves HEAD/branch and resets the index to the target's tree, but leaves the working tree untouched (the diff appears fully unstaged). **hard** moves HEAD/branch, resets the index, and force-overwrites every working-tree file to match the target — uncommitted changes are destroyed.
**Inputs / outputs**: `repo_path, oid, mode: string` → `()`.
**Edge cases**: `mode` is caller-supplied free text; the frontend only ever sends `"mixed"` (repoStore.ts:442, undo-last-commit action → `resetToCommit(repoPath, commit.parent_ids[0], "mixed")`). Nothing in this module offers a confirmation gate for `"hard"` — the doc comment at `src/CodeFlow.App/Git/RepoStatus.cs` notes callers "should get explicit confirmation for that one," but this file contains no such gate; it is the caller's responsibility.
**Frontend dependency**: `repoStore.ts` undo-commit action (`resetToCommit(..., "mixed")`).
**Markers**: none

### GIT-003 `CHECKOUT_CONFLICT_PREFIX` is the one checkout error the frontend parses
**Implementation**: `src/CodeFlow.App/Git/Branches.cs`
**Behaviour**: `checkout_error(e: git2::Error)` — the error mapper for both `checkout_local_branch` and `checkout_detached` — special-cases `git2::ErrorCode.Conflict`: it formats the returned string as `CHECKOUT_CONFLICT_PREFIX + e.message()` instead of the bare libgit2 message. Every other libgit2 error code falls through to the bare `e.message().to_string()`. This is the *only* error-string prefix anywhere in the git domain that a UI parser keys off of; no other function in this file, `src/CodeFlow.App/Git/Merge.cs`, `src/CodeFlow.App/Git/Stash.cs`, `src/CodeFlow.App/Git/Diff.cs`, `src/CodeFlow.App/Git/Checkpoints.cs`, or `src/CodeFlow.App/Git/GitNetwork.cs` defines or emits a comparable machine-parsed prefix (`src/CodeFlow.App/Git/GitNetwork.cs`'s `git:done` fallback message, GIT-034, is a full free-text sentence, not a prefix contract).
**Inputs / outputs**: the constant, verbatim:
`
CHECKOUT_CONFLICT_PREFIX = "CHECKOUT_CONFLICT: "
`
**Edge cases**: only fires when libgit2 reports `ErrorCode.Conflict` — i.e. the checkout would clobber uncommitted working-tree changes. Any other checkout failure (bad ref, corrupt repo, etc.) returns the raw libgit2 English message with no prefix.
**Frontend dependency**: `renderer/src/state/repoStore.ts` — `const CHECKOUT_CONFLICT_PREFIX = "CHECKOUT_CONFLICT: "` (independently duplicated, not imported) is checked with `String(e).includes(CHECKOUT_CONFLICT_PREFIX)` in `checkoutGuarded` to offer a way through instead of just surfacing the error. Three answers, not two: **carry** (`stash_save` → checkout → `stash_apply`, so the work lands on the branch being switched to), **stash** (`stash_save` → checkout, the work stays parked), and **cancel** — which returns quietly, since declining an offer is not a failure. A `"conflicts"` outcome (GIT-015) is not reported as an error either: the stash survives and the conflict UI takes over (GIT-019).

**Carry uses `stash_apply`, never `stash_pop`, and the entry is never dropped automatically.** Pop deletes the stash the moment it applies cleanly, which leaves a window where the only copy of the work is the working tree — and when the destination branch already contains that same content the apply changes nothing, so popping deletes the backup having carried nothing across. That is not hypothetical: it is how a day of uncommitted work read as lost. The entry stays until the user drops it from the sidebar.

Because "applied" alone cannot tell "your changes arrived" from "this branch already had them", the outcome is read off the working tree: the stash took everything with it (untracked included), so the tree was clean when the checkout finished and anything present afterwards is what the apply brought. Still clean means nothing was brought, and **that case is a dialog, not a toast** — it leaves the Changes panel empty, which is indistinguishable from having lost the work unless something says otherwise, and a toast that fades in five seconds did not. The same check and the same dialog cover a stash applied from the sidebar.

A checkout also **bumps `refreshSeq`** in `repoStore` and clears `status`/`workingDiff`/`stagedDiff` before starting. `RepoWatcher` (`11-files-search-terminal.md`) emits `repo:fs-changed` on the leading edge of the burst a checkout creates — i.e. while it is still writing files — and the renderer answers with a `refreshAll()`, so a read of the branch being left could resolve after the branch being entered and win. Loads capture the sequence on entry and drop their result if it moved, which is what stops the outgoing branch's files from flashing on the incoming one.
**Markers**: `VERBATIM`

### GIT-004 Local-branch checkout moves HEAD and the branch pointer together
**Implementation**: `src/CodeFlow.App/Git/Branches.cs`
**Behaviour**: `checkout_local_branch` resolves the branch's own ref name, revparses it to an object, force-checks-out its tree (`repo.checkout_tree`, no `CheckoutBuilder` options — i.e. libgit2's default *safe* checkout, which is what raises `ErrorCode.Conflict` on colliding uncommitted changes), then `repo.set_head(&refname)`.
**Inputs / outputs**: `repo_path, name: string` → `()`, or `Err(CHECKOUT_CONFLICT_PREFIX + msg)` per GIT-003.
**Edge cases**: branch must already exist locally (`find_branch(..., BranchType.Local)`); no creation-on-demand.
**Frontend dependency**: branch switcher.
**Markers**: none

### GIT-005 Detached checkout never moves a branch pointer
**Implementation**: `src/CodeFlow.App/Git/Branches.cs`
**Behaviour**: `checkout_detached(path, refname)` revparses `refname` (accepts a local branch, remote branch, tag, or raw SHA), peels to a commit, force-checks-out its tree, then `repo.set_head_detached(commit.id())` — HEAD points directly at the commit OID, no branch ref is touched or created.
**Inputs / outputs**: `repo_path, refname: string` → `()`; same `CHECKOUT_CONFLICT_PREFIX` mapping as GIT-003 on conflict.
**Edge cases**: any revparse-able expression works, including `HEAD~3`, tags, remote branches.
**Frontend dependency**: commit-graph "checkout this commit" action.
**Markers**: none

### GIT-006 Connecting to a remote branch reuses an existing same-named local branch as-is
**Implementation**: `src/CodeFlow.App/Git/Branches.cs`
**Behaviour**: `checkout_remote_tracking(path, "origin/feature-x")` splits on the first `/` to get the short name (`feature-x`). If a local branch with that short name already exists, it is checked out **unchanged** — its upstream is not touched, not verified, and not overwritten, even if it tracks something else or nothing at all. Only when no such local branch exists does it create one at the remote branch's tip and call `set_upstream(Some(remote_branch))` before checking out.
**Inputs / outputs**: `repo_path, remote_branch: string` (must contain at least one `/`) → `string`, the local short name.
**Edge cases**: `remote_branch` without a `/` → `Err("expected a name like 'origin/feature-x'")`. A remote name containing `/` itself (rare) would be split incorrectly by `split_once('/')` since only the *first* slash is used as the separator, but the remote portion (`_remote_name`) is discarded and unused anyway — only `short_name` (everything after the first `/`) matters.
**Frontend dependency**: remote-branches list "checkout" action.
**Markers**: `AMBIGUOUS-GIT-a` — whether a pre-existing local branch with a different upstream (or none) is the intended reuse target, or should instead be rejected/re-pointed, is not determined by the source.

### GIT-007 Branch listing computes ahead/behind only for local branches with an upstream
**Implementation**: `src/CodeFlow.App/Git/Branches.cs`
**Behaviour**: `list_branches` enumerates `repo.branches(None)` (both local and remote). For each **local** branch it looks up `branch.upstream()`; if one resolves and both the local and upstream refs have a target OID, `repo.graph_ahead_behind(local_oid, up_oid)` supplies `(ahead, behind)`. Remote branches always report `ahead: 0, behind: 0` and `upstream: None` — the ahead/behind counters exist to compare a local branch to *its* upstream, not to compare two remote refs. Any failure at any step (no upstream configured, upstream ref unresolvable, `graph_ahead_behind` erroring) silently leaves `ahead`/`behind` at their initialised `0`.
**Inputs / outputs**: `repo_path: string` → `IReadOnlyList<BranchInfo>`.
**Edge cases**: a branch with an upstream configured in `.git/config` that no longer exists as a ref (e.g. deleted on the remote, not yet pruned locally) silently reports `0/0` rather than an error.
**Frontend dependency**: sidebar branch list ahead/behind badges. The counts only refresh when `list_branches` is re-invoked — nothing in this file recomputes them automatically; the frontend's `autoFetchSeconds` preference (`GitSettings.tsx`, owned by `09-workspace-scoped.md`) drives a timer that calls the same `gitFetch` action used by the manual Fetch button (`repoStore.ts` `fetch()`, ~491-504), then `refreshBranches()` — i.e. background ahead/behind refresh is: (1) shell out to `git fetch` (GIT-034, updates the remote-tracking refs on disk), then (2) re-run `list_branches`, which re-derives ahead/behind from those now-updated refs via `graph_ahead_behind`. There is no separate ahead/behind computation path.
**Markers**: none

### GIT-008 Branch creation targets `start_point` or falls back to HEAD
**Implementation**: `src/CodeFlow.App/Git/Branches.cs`
**Behaviour**: `create_branch(path, name, start_point)` revparses `start_point` and peels it to a commit when given; otherwise peels HEAD to a commit. Calls `repo.branch(name, &target, false)` — the trailing `false` is libgit2's `force` flag, so creating a branch that already exists **fails** rather than overwriting it.
**Inputs / outputs**: `repo_path, name: string, start_point: string?` → `()`. Does not check the new branch out.
**Edge cases**: `start_point` can be any revparse-able expression (tag, SHA, other branch, remote branch).
**Frontend dependency**: "new branch" dialog.
**Markers**: none

### GIT-009 Branch deletion is a bare ref delete, local or remote-tracking
**Implementation**: `src/CodeFlow.App/Git/Branches.cs`
**Behaviour**: `delete_branch(path, name, is_remote)` finds the branch by name and `BranchType` (`Remote` or `Local`) and calls `branch.delete()`. No check for "is this the currently checked-out branch," no check for unmerged commits, no confirmation logic anywhere in this function — libgit2 itself refuses to delete the branch HEAD currently points to, and that refusal surfaces as a plain (non-prefixed) error string.
**Inputs / outputs**: `repo_path, name: string, is_remote: bool` → `()`.
**Edge cases**: deleting a remote-tracking branch (`is_remote: true`) only removes the local `refs/remotes/<remote>/<name>` ref; it does not touch the remote server.
**Frontend dependency**: branch list delete action.
**Markers**: none

### GIT-010 Diff status labels, including renamed (live since BUG-GIT-a's fix) and copied (rare)
**Implementation**: `src/CodeFlow.App/Git/Diff.cs`, `src/CodeFlow.App/Git/Diff.cs`
**Behaviour**: `StatusLabel` maps every `ChangeKind` to a string (`"added"`, `"deleted"`, `"modified"`, `"renamed"`, `"copied"`, `"typechange"`, `"conflicted"`, `"untracked"`, `"ignored"`, else `"unmodified"`). Every user-facing diff runs with `CompareOptions.Similarity = SimilarityOptions.Renames` — stated explicitly so behaviour cannot drift with the user's `diff.renames` config — so a pure rename is one `Renamed` entry carrying both paths and an empty hunk list. Copy detection stays off (git's own default), so `"copied"` is defined but rare. Checkpoints' internal `Compare` deliberately keeps `SimilarityOptions.None`: its restore projection needs a rename's delete and add as two entries.
**Inputs / outputs**: n/a (internal helper).
**Edge cases**: none — unconditional on every diff this module produces.
**Frontend dependency**: Changes/diff panels; `renderer/src/lib/fileStatus.ts` already mapped and coloured both labels before the fix, so no renderer change accompanied it.
**Markers**: `BUG-GIT-a` **closed** (was: no diff ever called the rename-detection pass, so a rename arrived as an unrelated delete plus add and these labels were dead branches).

### GIT-011 `get_status`'s rename branches, live since BUG-GIT-a's fix
**Implementation**: `src/CodeFlow.App/Git/RepoStatus.cs`
**Behaviour**: `Label` includes `RenamedInIndex`/`RenamedInWorkdir` branches (mapping to `"renamed"`), and `StatusRequest` sets `DetectRenamesInIndex`/`DetectRenamesInWorkDir` to `true` — both stated explicitly (index is LibGit2Sharp's default, workdir is not) so the pair cannot drift apart. A staged rename is reported once, as `("staged", "renamed")` under the new path. `TypeChangeInIndex`/`TypeChangeInWorkdir` fire independently of rename detection, as they always did.
**Inputs / outputs**: n/a (internal helper of GIT-001).
**Edge cases**: none — unconditional, the status half of GIT-010's fix.
**Frontend dependency**: same as GIT-001 — the Changes panel's `"renamed"` icon/label, defined all along, now renders.
**Markers**: `BUG-GIT-a` **closed** (was: the detection flags were forced off, so these branches never fired).

### GIT-012 Discard-all touches only what the Changes section shows, never staged or conflicted content
**Implementation**: `src/CodeFlow.App/Git/Diff.cs`
**Behaviour**: `discard_all_changes(path)` walks `repo.statuses()` (untracked included, recursive) and splits paths into two lists, **skipping conflicted paths entirely**: `untracked` (`is_wt_new`) and `tracked` (`is_wt_modified || is_wt_deleted || is_wt_renamed || is_wt_typechange`). Staged-only changes (`is_index_*` with no matching `is_wt_*`) are in neither list and are left completely alone. For `tracked` paths it does one `repo.checkout_index(Some(&mut index), force + explicit path list)` — i.e. every listed path is restored from the **current index**, not from HEAD, so a file that is staged *and further* edited on top keeps its staged content and only the unstaged edit on top is discarded (test `discard_all_keeps_staged_content`). For `untracked` paths it removes the file from disk (`System.IO`, tolerating `NotFound`), then walks up through now-possibly-empty parent directories calling `System.IO` (which only succeeds on an empty directory) until it hits the workdir root or a non-empty directory, cleaning up directories the deletion emptied.
**Inputs / outputs**: `repo_path: string` → `()`.
**Edge cases**: a file inside a directory that is itself untracked is still walked up correctly since `remove_dir` silently fails (and the loop stops) the moment a directory isn't empty. Errors deleting a specific untracked file (other than "not found") abort the whole operation with `Err(format!("{file_path}: {e}"))`, leaving remaining untracked files undeleted — not atomic/all-or-nothing at the filesystem level.
**Frontend dependency**: "Discard all changes" action in the Changes panel.
**Markers**: none

### GIT-013 Stage/unstage are index-level ops; staging a missing path stages its removal
**Implementation**: `src/CodeFlow.App/Git/Diff.cs`
**Behaviour**: `stage_file(path, file_path)` checks whether `file_path` exists on disk under the repo root; if it does, `index.add_path`; if it does not, `index.remove_path` — i.e. calling `stage_file` on a path the user just deleted from disk stages the deletion, matching `git add <deleted-file>` semantics. `stage_all` is `index.add_all(["*"], IndexAddOption.DEFAULT, None)` — equivalent to `git add -A .` (adds new/modified and stages deletions, no conflict-resolution behaviour beyond the default). `unstage_file` is `repo.reset_default(HEAD_commit, [file_path])` (per-path reset to HEAD). `unstage_all` replaces the whole index with HEAD's tree (`index.read_tree(head_tree)`).
**Inputs / outputs**: `repo_path, file_path: string` (or none for the `_all` variants) → `()`.
**Edge cases**: `unstage_file`/`unstage_all` on a repo with no commits yet (`repo.head()` fails) return an error — there is no HEAD to reset to.
**Frontend dependency**: Changes panel stage/unstage buttons and checkboxes.
**Markers**: none

### GIT-014 Stash rename is a drop-and-reappend reflog trick that reorders the stack
**Implementation**: `src/CodeFlow.App/Git/Stash.cs`
**Behaviour**: Git has no native "rename a stash" operation — a stash's message lives in the `refs/stash` reflog, and reflog entries can't be edited in place. `rename_stash(path, index, new_message)`: (1) reads the `refs/stash` reflog, gets the entry at `index`, and captures its **new-OID** (`entry.id_new()`) — the stash commit itself; (2) calls `repo.stash_drop(index)`, the same code path the Drop button uses, which removes that reflog entry (and, for `index == 0`, also updates the working `refs/stash` ref); (3) calls `repo.reference("refs/stash", oid, true, new_message)`, which **both** retargets `refs/stash` to the captured OID **and** appends exactly one fresh reflog entry using `new_message` verbatim as the reflog message — no `git stash push`-style `"On <branch>: "` prefix is added, unlike messages the ordinary `git stash push -m` CLI path produces. A manual `Reflog.append`+`write` splice was tried and rejected (per the source comment) because it left a stray duplicate reflog entry instead of truly replacing the original.
**Inputs / outputs**: `repo_path: string, index: int, new_message: string` → `()`, `Err("Stash not found")` if `index` is out of range.
**Edge cases**: the reappended entry always becomes the newest — `stash@{0}` — regardless of which index was renamed. Renaming a stash that was *not* already at the top therefore reorders the whole stack: every stash that was above it shifts down by one slot, and the renamed stash becomes `stash@{0}`. This is documented in the source as "the same trade-off `git stash pop && git stash push -m \"...\"` has." No stash is lost or duplicated (verified in both tests) — reordering is the only side effect.
**Inputs / outputs (continued)**: after renaming index 1 out of 2 (`["second", "first"]` before), the result is `["renamed(first)", "second"]` — the entry that was at index 1 becomes index 0, and everything else shifts down.
**Frontend dependency**: stash list rename action; the frontend must re-fetch `list_stashes` after a rename and expect the whole ordering to have possibly changed, not just the renamed entry's text.
**Markers**: `DIVERGENCE-GIT-a` — deliberate implementation choice (there is no libgit2/git primitive to do this any other way); must be preserved, including the reordering-to-top side effect.

### GIT-015 Stash save/apply/pop/drop are direct libgit2 stash calls
**Implementation**: `src/CodeFlow.App/Git/Stash.cs`
**Behaviour**: `list_stashes` uses `repo.stash_foreach`, returning `(index, message, oid)` per entry in stack order (index 0 = most recent). `stash_save(path, message, include_untracked)` uses `StashFlags.DEFAULT`, adding `StashFlags.INCLUDE_UNTRACKED` when requested; the message defaults to `"WIP"` when `None` is passed. Neither a "keep index" nor a "keep staged separately" option is exposed — libgit2's default stash flags stash both the index and the working tree together (the equivalent of plain `git stash push`, not `git stash push --keep-index`). `stash_apply`/`stash_pop` both use a fresh default `StashApplyOptions` (no conflict-callback customisation) and **return their outcome** as one of `"applied"`, `"conflicts"`, `"not_found"`, `"uncommitted_changes"` (`"unknown"` for anything a future LibGit2Sharp adds). `stash_drop` removes an entry without applying it.

The outcome is decided by the **index**, not by `StashApplyStatus` alone: LibGit2Sharp reports `Applied` for an apply that wrote conflict markers and left conflicted index entries behind — its own `Conflicts` value covers only the case where the merge could not be attempted at all — so `Applied` plus a non-empty `repo.Index.Conflicts` is reported as `"conflicts"`.

`stash_pop` is `Apply` followed by `Stashes.Remove(index)` **only when the outcome is `"applied"`**, deliberately not `StashCollection.Pop`. Verified against a real repository: LibGit2Sharp's `Pop` drops the entry even when the apply conflicted, leaving the only copy of that work half-merged on disk with the stash gone. Real `git stash pop` keeps the entry in that case; so does this.
**Inputs / outputs**: `repo_path, index: int` (+ `message: string?, include_untracked: bool` for save; `new_message: string` for rename) → `string` for apply/pop, `()` for the rest.
**Edge cases**: a conflicting apply/pop is **not** an error and throws nothing — it is an outcome to act on, the same shape `MergeOutcome.status == "conflicts"` uses for a merge. `CHECKOUT_CONFLICT_PREFIX` is still not reused here (GIT-031): that prefix marks an error the frontend parses, and there is no error to mark.
**Frontend dependency**: stash panel save/apply/pop/drop actions, which announce a non-`"applied"` outcome instead of finishing silently; also invoked programmatically by `checkoutGuarded` (`repoStore.ts`) for both recovery paths of GIT-003.
**Markers**: none

### GIT-016 Merge resolves to one of four outcomes, in priority order
**Implementation**: `src/CodeFlow.App/Git/Merge.cs`
**Behaviour**: `merge_branch(path, branch_name)` finds `branch_name` as a local branch first, falling back to a remote branch (`find_branch(..., Local).or_else(|_| find_branch(..., Remote))`), peels it to a commit, and runs `repo.merge_analysis`. In priority order: (1) **up_to_date** — analysis says so, no-op, `conflicts: []`. (2) **fast_forward** — moves the current branch's ref directly to their commit's OID and force-checks-out HEAD; no merge commit is created. (3) Otherwise, runs `repo.merge(IReadOnlyList<annotated>, None, None)` (libgit2's three-way merge into the working index) and inspects `index.has_conflicts()`: if true, returns **conflicts** with the list of conflicted paths (repo state is left mid-merge, `MERGE_HEAD` set, nothing committed) and no cleanup is performed yet; if false, writes the merged index as a tree, commits it with two parents (`[head_commit, their_commit]`) and message `"Merge branch '{branch_name}'"`, calls `repo.cleanup_state()`, and returns **merged**.
**Inputs / outputs**: `repo_path, branch_name: string` → `MergeOutcome { status, conflicts }`.
**Edge cases**: `branch_name` ambiguous between a local and identically-named remote branch always resolves to the **local** one (the `.or_else` only triggers if the local lookup errors). A fast-forward merge does not create a merge commit even if the caller might have expected one — matches native `git merge`'s default (non-`--no-ff`) behaviour.
**Frontend dependency**: merge action in the branch list / graph.
**Markers**: none

### GIT-017 Conflict resolution reads and writes the merge index's three stages
**Implementation**: `src/CodeFlow.App/Git/Merge.cs`, `112-199`
**Behaviour**: A conflicted path in libgit2's index has up to three stage entries: **stage 1 (ancestor)**, **stage 2 (ours — the branch merged into)**, **stage 3 (theirs — the incoming branch)**; any of the three is absent when that side added or deleted the file. `conflict_paths` (used by `list_conflicts`) dedupes conflicted paths, picking whichever of ours/theirs/ancestor exists for the display path. `conflict_versions(path, rel_path)` reads all three blobs (empty string for an absent side) into `ConflictVersions { base, ours, theirs }` for the AI resolver, decoded UTF-8-lossy. `resolve_conflict_side(path, rel_path, side)` takes `"ours"` or `"theirs"` (any other string → `Err("side must be 'ours' or 'theirs'")`), requires that side's entry to exist (`Err("that side has no content for this file (it was added/deleted)")` otherwise), writes that blob's raw bytes to the working-tree file, then `index.add_path` — which, on a still-conflicted index entry, clears all three conflict stages for that path and re-adds it as a normal staged entry from the current working-tree content. `mark_conflict_resolved(path, rel_path)` does the same final `index.add_path` step alone, for the case where the user hand-edited the file in the embedded editor instead of picking a whole side.
**Inputs / outputs**: `repo_path, rel_path, side: string` → `()`; `conflict_versions` (internal, not a a registered command in this file — consumed by `resolve_conflict_with_ai` in `05-ai-engines.md`'s `src/CodeFlow.App/Ai/AiCommands.cs`) → `ConflictVersions`.
**Edge cases**: `resolve_conflict_side`/`mark_conflict_resolved` write to `<repo_path>/<rel_path>` directly with `System.IO`, so both operate purely on disk + index — no relationship to `discard_all_changes` (which explicitly skips conflicted paths, GIT-012).
**Frontend dependency**: conflict resolution panel (whole-side buttons call `resolve_conflict_side`; the embedded editor's "mark resolved" calls `mark_conflict_resolved`); `resolveConflictWithAi` (05-ai-engines.md) reads via `conflict_versions` and is expected to end by calling one of these two to actually stage its result.
**Markers**: none

### GIT-018 Completing or aborting a merge is a plain two-parent commit or a forced reset to HEAD
**Implementation**: `src/CodeFlow.App/Git/Merge.cs`
**Behaviour**: `complete_merge(path, message)` refuses if `index.has_conflicts()` (`Err("There are still unresolved conflicts")`), otherwise writes the index as a tree, commits with parents `[HEAD, MERGE_HEAD's target]`, and calls `repo.cleanup_state()`. It reads `MERGE_HEAD` directly via `find_reference("MERGE_HEAD")` rather than trusting any state cached from the earlier `merge_branch` call — so it works correctly even if the app process restarted mid-conflict. `abort_merge(path)` force-checks-out HEAD's current tree (discarding every working-tree/index change the merge attempt made) and calls `repo.cleanup_state()` — it does **not** inspect or require conflicts to exist; calling it while merging cleanly (no conflicts, not yet committed) still discards that clean merge's staged result.
**Inputs / outputs**: `repo_path, message: string` (complete) or `repo_path` (abort) → `string` (new commit oid) or `()`.
**Edge cases**: `complete_merge` on a repo with no `MERGE_HEAD` (i.e. not actually merging) → `Err("MERGE_HEAD has no target")` or the `find_reference` error, not a friendly "nothing to complete" message.
**Frontend dependency**: "complete merge" / "abort merge" buttons, shown **only** while `is_merging` is true. That gate is load-bearing rather than cosmetic: `abort_merge` never checks whether a merge is in progress, so offering it over a conflicted-but-unmerged tree (GIT-015's conflicting stash) would be a `reset --hard HEAD` that discards every uncommitted change. Outside a merge the panel offers a discard of its own, which is safe for a different reason — the stash those conflicts came from is still in the list.
**Markers**: none

### GIT-019 `is_merging` reflects libgit2's repository state exactly
**Implementation**: `src/CodeFlow.App/Git/Merge.cs`
**Behaviour**: `repo.state() == RepositoryState.Merge`. Any other in-progress state (rebase, cherry-pick, revert, bisect — none of which this codebase's commands ever start) reports `false`, since none of `src/CodeFlow.App/Git/GitCommands.cs`'s other functions ever leave the repo in those states.
**Inputs / outputs**: `repo_path: string` → `bool`.
**Edge cases**: **it is not the same question as "are there conflicts"**. `is_merging` is `MERGE_HEAD` and nothing else, while `list_conflicts` (GIT-017) reads the index — and a stash that applies with conflicts (GIT-015) marks the index without ever writing `MERGE_HEAD`. Anything that gates on `is_merging` alone is blind to that state.
**Frontend dependency**: what the conflict UI is shown for is `is_merging` **or** a non-empty `list_conflicts` — `refreshMergeState` reads both unconditionally. Gating the read on `is_merging` is what once left a working tree full of conflict markers with no UI offering to resolve them. `is_merging` still decides the panel's footer: only a real merge offers "complete merge" and "abort merge" (GIT-018).
**Markers**: none

### GIT-020 Commit graph is topological+chronological, HEAD-only or every ref
**Implementation**: `src/CodeFlow.App/Git/CommitGraph.cs`
**Behaviour**: `list_commits(path, all_refs, limit)` builds a ref→names map first (every branch/remote-branch/tag shorthand, annotated tags peeled to their target commit), then walks with `Sort.TOPOLOGICAL | Sort.TIME`. `all_refs == true` pushes `refs/heads/*` and `refs/remotes/*` as walk roots (all local and remote branches — **not** tags, so a tag-only commit unreachable from any branch is never visited even though its name would appear in `refs` if reached another way); `all_refs == false` pushes only `HEAD`. Results are truncated to `limit` (`walk.take(limit)`).
**Inputs / outputs**: `repo_path: string, all_refs: bool, limit: int` → `IReadOnlyList<CommitInfo>`.
**Edge cases**: `limit == 0` → empty vec (no walking error). A commit reachable by both a branch and a tag gets both names in `refs`.
**Frontend dependency**: commit graph / history view.
**Markers**: none

### GIT-021 Unpushed commits are HEAD's branch minus its upstream, empty without one
**Implementation**: `src/CodeFlow.App/Git/CommitGraph.cs`
**Behaviour**: `list_unpushed_commits(path)`: if HEAD isn't a branch (detached) → `Ok(vec![])`. Otherwise finds the local branch matching HEAD's shorthand, and if it has no resolvable upstream (or the upstream ref has no target) → `Ok(vec![])`. Otherwise walks `push(head_oid); hide(upstream_oid)` topologically — exactly the commit set `git push` would send.
**Inputs / outputs**: `repo_path: string` → `IReadOnlyList<CommitInfo>`.
**Edge cases**: no `limit` parameter — this can return arbitrarily many commits if the branches have diverged a lot.
**Frontend dependency**: "unpushed commits" indicator/badge.
**Markers**: none

### GIT-022 Checkpoints snapshot the working tree into a commit outside `refs/heads`, in-memory only
**Implementation**: `src/CodeFlow.App/Git/Checkpoints.cs`
**Behaviour**: `Checkpoints`(path, kind)` is the whole undo-behind-AI-edits (and repo-wide replace, GIT-024) mechanism. `snapshot_tree(repo)`: allocates a brand-new `git2::Index.new()` (not the repo's real index), calls `repo.set_index(&mut index)` to attach it to *this in-process `Repository` handle only* (the on-disk `.git/index` is never opened, read, or written by this path — the user's staging area is provably untouched), seeds it from HEAD's tree when one exists (repo with zero commits gets an empty base), then walks `repo.statuses()` (untracked included, recursive, ignored excluded) and for every reported path either `index.add_path` (path is currently a file on disk — staged, unstaged, or untracked, doesn't matter which) or `index.remove_path` (path is gone — deleted or replaced by a directory), and finally `index.write_tree_to(repo)` to get a tree OID **without** touching the working index. `create` then builds a commit from that tree — parented on HEAD's commit when one exists, parentless otherwise — with `message = kind` (an opaque stable key like `"chat"`, `"fix-finding"`, `"replace-all"` — the exact set of keys in use is enumerated by `05-ai-engines.md`/`11-files-search-terminal.md`, not here), signed with the repo's own signature or a `("CodeFlow", "codeflow@local")` fallback when the repo has no configured identity, and writes it directly to a ref at `refs/codeflow/checkpoints/<id>` (never to `HEAD` or any `refs/heads/*` branch, so it never appears in any branch list or `git log` of a branch, and `git status` is unaffected). `<id>` is `"{unix_seconds}-{first 8 hex chars of a fresh UUIDv4}"`. After every `create`, `prune()` runs best-effort (its own errors are swallowed, never fail the checkpoint itself).
**Inputs / outputs**: `repo_path: string, kind: string` → `string` (the new checkpoint id).
**Edge cases**: two checkpoints created in the same wall-clock second get distinct ids only via the UUID suffix — `id` is not required to be time-monotonic-unique by itself. A bare/no-workdir repo → `snapshot_tree`'s caller-side `repo.workdir()` requirement is only enforced later, in `diff_paths`/`restore`, not in `create`/`snapshot_tree` itself, so `create` on a bare repo may succeed and leave a checkpoint that can never be diffed or restored — the fixture repos in this codebase are never bare in practice.
**Frontend dependency**: `05-ai-engines.md` (`src/CodeFlow.App/Ai/AiCommands.cs`'s `checkpoint_before` helper, called before chat and fix-finding AI runs) and `11-files-search-terminal.md` (`src/CodeFlow.App/Files/Search.cs`'s repo-wide replace, kind `"replace-all"`) both call `Checkpoints` before an operation that writes to the working tree; those two documents own exactly when/why a checkpoint is taken. `src/CodeFlow.App/Git/Checkpoints.cs`'s 3 commands (list/restore/delete) are the only checkpoint surface owned by this document.
**Markers**: none

### GIT-023 Checkpoint ref namespace and the prune-to-20 policy
**Implementation**: `src/CodeFlow.App/Git/Checkpoints.cs`
**Behaviour**: ref prefix, verbatim:
`
REF_PREFIX = "refs/codeflow/checkpoints/"
`
`MAX_CHECKPOINTS = 20`. `prune(repo)` globs `refs/codeflow/checkpoints/*`, pairs each ref with its commit's **commit time** (`peel_to_commit().time().seconds()` — i.e. the checkpoint's `created_at`, not any filesystem timestamp), and if there are more than 20, sorts descending by that time and deletes every ref past the 20 newest. A ref whose target can no longer be peeled to a commit is silently excluded from consideration (`filter_map`) rather than counted or deleted. `list(path)` uses the same glob, returns `CheckpointInfo` per ref (`id` = the ref's last path segment, `kind` = the commit's summary/message, `created_at` = commit time, `changed_paths` via GIT-024's diff), sorted newest-first by `created_at` — independently from and not sharing code with `prune`'s own sort.
**Inputs / outputs**: n/a for `prune` (side-effecting, no return value observed by callers); `list(repo_path: string) -> IReadOnlyList<CheckpointInfo>`.
**Edge cases**: pruning happens **only** as a side effect of `create` — deleting checkpoints via `delete_ai_checkpoint` never triggers it, and there is no scheduled/periodic prune; a repo that only ever has checkpoints deleted manually and never created past 20 will never be pruned by count (it can't exceed 20 that way regardless). Pruned refs are deleted (`reference.delete()`); their commit objects remain in the object database until git's own gc reaps them — same as `remove`/`delete_ai_checkpoint` (GIT-025).
**Frontend dependency**: `CheckpointsModal.tsx` — undo/checkpoints list UI.
**Markers**: none

### GIT-024 Restore is per-file content replacement, never HEAD/index/checkout
**Implementation**: `src/CodeFlow.App/Git/Checkpoints.cs`
**Behaviour**: `diff_paths(repo, commit)` — shared by `list` (as `changed_paths`) and `restore` — diffs the checkpoint's tree against the **live working tree** using `repo.diff_tree_to_workdir_with_index` (untracked included, recursive, typechange included), collecting deduped, sorted new-or-old paths. This is a fresh comparison against the *current* on-disk state every time it's called — `changed_paths` in a `list()` response is always "what restoring right now would touch," not a snapshot of what changed at checkpoint-creation time. `restore(path, id)` computes that same path list, and for each path: if the checkpoint's tree has an entry at that path, reads the blob and `System.IO`s it verbatim to the working-tree file (creating parent directories as needed); if the checkpoint's tree has **no** entry there (the AI run created the file after the checkpoint), the file is deleted from disk (`System.IO`, error ignored). Returns the list of paths it touched. **Nothing here reads or writes the git index, and nothing calls `checkout_tree` or moves HEAD** — a file already staged before the restore stays staged (now with stale content relative to the restored working-tree file, exactly like any other manual edit after staging), and HEAD/the current branch are completely unaffected. This is the load-bearing distinction from `reset_to_commit`/`discard_all_changes`, called out explicitly in the module doc comment: "this is an 'undo these edits' button, not a `git reset`."
**Inputs / outputs**: `repo_path, checkpoint_id: string` → `IReadOnlyList, string>` (paths touched), or `Err("checkpoint '{id}' no longer exists")` if the ref is gone.
**Edge cases**: restoring twice in a row is idempotent past the first call — the second `diff_paths` finds nothing left to differ (assuming nothing else changed the tree in between) and returns an empty list, touching nothing. A path that a user has since manually edited back to match the checkpoint is excluded from both `changed_paths` and `restore`'s touched set (the diff is content-based, not history-based).
**Frontend dependency**: `restoreAiCheckpoint` (`src/CodeFlow.App/Git/Checkpoints.cs`) → `CheckpointsModal.tsx`; the returned path list is what the UI reports as "restored: file1, file2" rather than a bare success.
**Markers**: none

### GIT-025 Deleting a checkpoint only ever removes the ref
**Implementation**: `src/CodeFlow.App/Git/Checkpoints.cs`
**Behaviour**: `remove(path, id)` finds `refs/codeflow/checkpoints/<id>` and deletes it if present; deleting an id that doesn't exist is **not an error** (`if let Ok(...)` silently no-ops). `remove_if_unchanged(path, id)` reads the checkpoint (this variant **does** error if the id is missing, via `read_checkpoint`'s `Err(format!("checkpoint '{id}' no longer exists"))`), computes `diff_paths`, and only calls `remove` (returning `true`) when that list is empty — i.e. the protected run changed nothing observable, so keeping the checkpoint around would only ever offer to "restore" zero files.
**Inputs / outputs**: `remove: (repo_path, checkpoint_id: string) -> void`; `remove_if_unchanged: (repo_path, checkpoint_id: string) -> bool` (internal — used by `src/CodeFlow.App/Ai/AiCommands.cs`'s `checkpoint_before`/after-run cleanup, not exposed as its own a registered command).
**Edge cases**: neither function ever runs `prune` or otherwise interacts with the 20-cap (GIT-023).
**Frontend dependency**: `deleteAiCheckpoint` (`src/CodeFlow.App/Git/Checkpoints.cs`) for explicit user deletion; `remove_if_unchanged` for automatic no-op-run cleanup (05-ai-engines.md).
**Markers**: none

### GIT-026 Remotes: list and dual-URL rewrite
**Implementation**: `src/CodeFlow.App/Git/Remotes.cs`
**Behaviour**: `list_remotes` iterates `repo.remotes()` (names), and for each name that still resolves (`find_remote`) reports `{name, url}` — url is `""` if libgit2 has no URL for it, never a missing field. `set_remote_url(path, name, url)` sets **both** `remote_set_url` (fetch URL) and `remote_set_pushurl(name, Some(url))` (push URL override) to the same value — so it cannot express a repo where fetch and push go to different URLs; any pre-existing distinct push URL is overwritten.
**Inputs / outputs**: `repo_path: string` → `IReadOnlyList<RemoteInfo>`; `repo_path, name, url: string` → `()`.
**Edge cases**: a remote name that `repo.remotes()` lists but that later fails `find_remote` (unlikely, but not impossible under concurrent config edits) is silently skipped rather than erroring the whole call.
**Frontend dependency**: remote settings panel.
**Markers**: none

### GIT-027 Git identity is process-global, not per-repo
**Implementation**: `src/CodeFlow.App/Git/Identity.cs`
**Behaviour**: `get_identity`/`set_identity` operate on `git2::Config.open_default()` — the global (`~/.gitconfig`)-plus-system config stack, **not** any specific repository's local config. This is what `repo.signature()` falls back to for any repo that doesn't override `user.name`/`user.email` locally. `get_git_identity`/`set_git_identity` (the corresponding commands) take no `repo_path` parameter at all. Since WS-008 this global identity is specifically the **final fallback tier** of the commit-signature precedence (GIT-028/GIT-036): a workspace's own identity, when set, wins over it for commits made through the app.
**Inputs / outputs**: `() -> GitIdentity`; `name, email: string -> void`.
**Edge cases**: unset name/email individually read back as `None`, not an error.
**Frontend dependency**: global git identity settings, not scoped to any open project.
**Markers**: none

### GIT-028 Commit signs with explicit author when given, else the workspace identity, else the repo's configured signature
**Implementation**: `src/CodeFlow.App/Git/Diff.cs` · resolution in `src/CodeFlow.App/Git/GitCommands.cs` (`ResolveAuthor`)
**Behaviour**: `commit(path, message, author_name, author_email)` writes the **current index** as a tree (whatever is staged at call time — this function does not stage anything itself), and picks the signature: `Signature.now(name, email)` (current wall-clock time) only when **both** `author_name` and `author_email` are `Some`; otherwise `repo.signature()` (the configured identity, GIT-027, with its own commit-time timestamp). The same signature is used for both author and committer. Parent is HEAD's commit if one exists (root commit otherwise), always exactly one parent — this is a plain commit, never a merge commit (that path is GIT-016/GIT-018 only).
The command handler resolves the author pair before dispatching (`ResolveAuthor`, shared with GIT-036): explicit `authorName`/`authorEmail` arguments (both present) win; otherwise the workspace identity of the project registered at `repoPath` (WS-008, `WorkspaceStore.ResolveGitIdentity` — an exact `projects.local_path` join); otherwise `(null, null)`, which lands on `Diff.CommitIndex`'s configured-signature fallback above.
**Inputs / outputs**: `repo_path, message: string, author_name: string?, author_email: string?` → `string` (new commit oid, hex).
**Edge cases**: `author_name`/`author_email` supplied one-without-the-other (e.g. name but no email) is discarded, never merged — the resolution then continues down the precedence: workspace identity if one is set, else the configured signature. Likewise a workspace row holding only half a pair (possible only by out-of-band DB edits — `update_workspace_git_identity` always writes both) is discarded by `Diff.CommitIndex`'s both-or-neither rule.
**Frontend dependency**: commit box "commit" action; AI-generated commit messages (`05-ai-engines.md`) flow through this same function. The renderer never sends `authorName`/`authorEmail` today — the workspace tier is how a non-default identity actually happens.
**Markers**: none

### GIT-036 Merge and merge-completion commits honour the resolved author, same precedence as GIT-028
**Implementation**: `src/CodeFlow.App/Git/Merge.cs` (`Branch`, `Complete`) · resolution in `src/CodeFlow.App/Git/GitCommands.cs` (`ResolveAuthor`)
**Behaviour**: `Merge.Branch` and `Merge.Complete` take an optional `authorName`/`authorEmail` pair; both-present builds the signature from it (used for author **and** committer, like GIT-028), anything less falls back to `repo.Config.BuildSignature()`. The `merge_branch` and `complete_merge` handlers resolve the pair through the same `ResolveAuthor` helper `commit` uses — these two commands expose no author arguments on the wire, so for them the resolution is workspace identity (WS-008) or the configured fallback, nothing else.
**Inputs / outputs**: unchanged on the wire — `repo_path, branch_name` / `repo_path, message`; the author pair is resolved sidecar-side, never renderer-supplied.
**Edge cases**: a fast-forward or up-to-date outcome creates no commit, so the resolved identity is simply unused. AI checkpoints (GIT-021) and stash commits deliberately stay outside this rule: they live under `refs/codeflow/*`/the stash ref, invisible in branch history, and keep their existing signatures (including `Checkpoints.cs`'s `"CodeFlow" <codeflow@local>` bot fallback).
**Frontend dependency**: merge action in the branch list / graph and the conflict panel's "complete merge" — both now sign with the active workspace's identity when one is set.
**Markers**: none

### GIT-029 Working/staged/commit diffs render full-file context, not a compact patch
**Implementation**: `src/CodeFlow.App/Git/Diff.cs`
**Behaviour**: All three diff-producing functions set `context_lines(1_000_000)` — large enough that every hunk covers the whole file with the edited lines highlighted, rather than the few-line context a compact PR-style diff would show. `get_working_diff` additionally sets `show_untracked_content(true)` (without which an untracked file appears as a bare delta with zero hunks — this flag is what makes libgit2 diff it against empty content so every line shows as added) and `recurse_untracked_dirs(true)` (without which only the containing directory is reported for a file inside a brand-new untracked directory, not the file itself).
**Inputs / outputs**: `repo_path: string` → `IReadOnlyList<FileDiffInfo>` for all three (`get_commit_diff` additionally takes `oid: string`).
**Edge cases**: `get_commit_diff` diffs against `commit.parent(0)` only — for a merge commit this is the first-parent diff (what changed relative to the branch merged into), never a combined/all-parents diff; a root commit (no parents) diffs against an empty tree implicitly (`parent_tree: None`).
**Frontend dependency**: Changes tab (working/staged), and the stash diff modal (`renderer/src/components/layout/StashDiffModal.tsx`), which shows a stash commit whole. The commit graph no longer calls `get_commit_diff` at all — it expands a commit into a file list and diffs one file at a time (GIT-035).
**Markers**: none

### GIT-035 The graph expands a commit into a content-free file list, and diffs one file at a time
**Implementation**: `src/CodeFlow.App/Git/Diff.cs` (`CommitFiles`, `CommitFile`)
**Behaviour**: `list_commit_files(path, oid)` compares the same two trees `get_commit_diff` does — the commit against its first parent, with the same `FullFile()` options so rename detection and the status labels agree — but as a `TreeChanges` rather than a `Patch`, so libgit2 never renders any patch text. It returns one `CommitFileInfo` per delta, in libgit2's own order (the same order `get_commit_diff` reports). This is what makes expanding a commit in the graph cheap: with `ContextLines = 1_000_000` (GIT-029), asking for the whole commit's diff just to learn which files it touched means downloading every touched file in full.
`get_commit_file_diff(path, oid, file_path, old_path)` is `get_commit_diff` narrowed by a pathspec: the pathspec holds `file_path` plus `old_path` when the two differ, and the result is the usual `FileDiffInfo` list — one entry, or none if the path is not part of that commit. Whole-file context is preserved (GIT-029 applies unchanged), so a picked file still renders as the entire file with its edits highlighted.
**Inputs / outputs**: `repo_path: string, oid: string` → `IReadOnlyList<CommitFileInfo>` · `repo_path, oid, file_path: string, old_path: string?` → `IReadOnlyList<FileDiffInfo>`.
**Edge cases**: **`old_path` is load-bearing for renames.** libgit2 applies the pathspec *before* `find_similar`, so filtering a renamed file by its new path alone drops the matching delete and what survives is an `added` file whose diff claims every line is new; passing both paths keeps both deltas alive for rename detection to pair up. A deleted file's key is its old path, and passing it as both arguments is harmless — identical paths are deduplicated into a one-entry pathspec. Both commands inherit `get_commit_diff`'s first-parent rule and its `object not found - no match for id ({oid})` error verbatim.
**Frontend dependency**: the commit graph (`renderer/src/components/git/GraphView.tsx`) — selecting a commit calls `list_commit_files` and expands the file list under its row; selecting a file calls `get_commit_file_diff` and is the only thing that opens the diff panel. `repoStore` keys the open file by `new_path ?? old_path` and passes `old_path` straight back to the sidecar.
**Markers**: none

### GIT-031 A diff is reshaped before it is given to a model
**Implementation**: `src/CodeFlow.App/Git/PromptDiff.cs` · `src/CodeFlow.App/Git/Diff.cs` (`RenderForPrompt`)
**Behaviour**: `RenderForPrompt` is the single funnel for the three prompt paths — change analysis
(`05-ai-engines.md`), PR review and PR description (`07-review-pipeline.md`) — and it does **not**
flatten the diff it is given. `GIT-029`'s whole-file context exists for the Changes tab; a prompt is
shaped separately, by `PromptDiff.Render(files, budgetChars)`:

- **Context is trimmed** to `PromptDiff.ContextLines` (3) either side of each changed line. Every
  omitted run is replaced by `~ N unchanged lines omitted`.
- **Each kept run carries its own `@@ -old +new @@` anchor**, built from that run's real line
  numbers. The source hunk header is not reproduced: with whole-file context it describes the file
  rather than the change, and a finding that cites a line needs a line that exists.
- **Paths with no reviewable signal are excluded** and named: lock files, `*.min.js` / `*.min.css`,
  source maps, generated markers (`.g.cs`, `.designer.cs`, `.g.dart`, `.freezed.dart`, `.pb.go`,
  `_pb2.py`, `*.generated.*`) and `node_modules` / `vendor` path segments. Deliberately **not**
  `dist` / `build` / `out`: git ignores those where they are generated, and elsewhere they hold real
  source — this repository stages into `shell/build/` itself.
- **The budget is shared between files**, cheapest first, each taking only what it needs and
  returning the rest. A file whose share falls below `MinimumFileShare` is named rather than
  half-shown; a file that is cut is cut on a line boundary and says so.
- **A leading `NOTE:` block lists everything excluded or omitted.** A complete diff carries no note.
  The room it needs is reserved against the files that will actually be named — computed by sharing
  the budget once to find them — not against every file kept. Reserving per kept file charged 120
  characters a head for a list almost none of them join: at 200 files that is a fifth of the budget
  spent to say nothing, and at a thousand it consumes the budget and omits every file for want of
  room to admit it.
- **A gap of a single unchanged line is shown rather than declared.** Declaring costs about thirty
  characters and, by splitting one run in two, a second `@@` anchor of about twenty more; a line of
  code is around forty. So one line is cheaper shown, and two are not. Absorbed before anything is
  written, which is what keeps the runs either side of it from being emitted as two.

**Inputs / outputs**: `(IReadOnlyList<FileDiffInfo>, budgetChars = 250_000) -> string`.
**Edge cases**: an added file has no unchanged lines and so is shown whole; a binary file has no
hunks and contributes its banner alone, which is still the signal that it changed; an empty diff
renders the empty string.
**Frontend dependency**: none — this is prompt payload, never displayed.
**Markers**: none

**A provider's own diff text takes the same road.** `PromptDiff.RenderText` parses a unified diff
back into `FileDiffInfo` (`src/CodeFlow.App/Git/UnifiedDiff.cs`, reusing `UnifiedPatch.Hunks` for the
per-file hunks) and renders it exactly as above. A review reached by pasted link has no clone to
diff, so the host hands the diff back as text — and when the budget moved here from `AiOperations`,
that route was left with none at all: the provider's diff went into the prompt whole. Found by this
application reviewing its own change (`F-003`). A diff whose shape the parser does not recognise is
truncated **and said**, never passed through: an unfamiliar format must degrade to less content, not
to no limit. The workspace file a link review writes keeps the diff as the provider wrote it — that
one is for a person to scroll, not for a model to read.

**`Shape` also reports what it left out.** `PromptDiff.Shape(files, budgetChars, carried)` returns
the same text plus a `DiffCoverage` — files touched, shown, excluded, omitted, truncated, and
carried over from a previous review — which is what the review's stats line is built from
(`REVIEW-038`). `Render` is the same call with no carried list and the coverage discarded. `carried`
paths join the `NOTE:` block as `unchanged since the previous review, already reviewed: {path}`.

### GIT-033 The code around each change is extracted, so the model does not go and read it
**Implementation**: `src/CodeFlow.App/Git/ChangeContext.cs`
**Behaviour**: `ChangeContext.Render(files, budgetChars = 80_000)` produces a `CODE AROUND THE
CHANGES` section that quotes, for every changed file, **the declaration each change sits in** — with
real line numbers, and `>` marking the lines the pull request added or modified. It rides after the
diff in the review payload (`AI-023`). It needs no filesystem access: `GIT-029`'s whole-file context
means every line of every changed file is already in the `FileDiffInfo` it is handed.

- **The block is found by indentation, not by a parser.** Upwards to the first line indented less
  than the change; a lone `{`, `(` or `[` is skipped over as punctuation belonging to the line above
  it, so the block starts on the line that names it in both brace conventions and in the languages
  that have neither. Then the declaration's own doc comment, attributes or decorators, when they sit
  at its indentation. Downwards while the indentation stays inside, taking the closing delimiter.
- **A change that no block contains is quoted on its own** — an import, a top-level constant, a
  namespace line. Nothing the pull request touched goes unquoted.
- **A deletion marks the line that took its place**, since it has no line of its own on the new side;
  a deletion at the very end of a file marks the last line.
- **Added and deleted files are skipped**: the diff already carries every line of those, and so are
  the paths `PromptDiff.SkipReason` excludes, for the same reasons.
- **Two caps.** A block longer than 400 lines becomes a ±20-line window around the change — that is
  the case where the indentation guess was wrong, typically a file with no structure it can read, and
  it turns "the whole file" into a wide window instead. The budget is shared between files by the
  same water-filling as `GIT-031`; a file below its minimum share is named in the preamble rather
  than half-shown.

**Inputs / outputs**: `(IReadOnlyList<FileDiffInfo>, budgetChars = 150_000) -> string`; the empty
string when there is nothing to quote.
**Edge cases**: a link review gets `""` — the host hands back a patch, not the files it came from,
so there is no surrounding code to extract at all.
**Frontend dependency**: none — prompt payload.
**Markers**: none

**Why.** Bounding the toolset removed `Bash` from a review and the agent replaced it with **nineteen
`Read`s and seven `Grep`s across twelve files** — six minutes, of which four were spent reasoning
over a context that grew with every one of them. It was not idle exploration: a diff trimmed to three
lines either side does not show the method a changed line sits in, so the model went and opened it,
once per file, guessing the range each time. The range is computed here instead — once, exactly,
before the model is asked anything.

**Why the diff still travels alongside it.** The extract is the new file as it now reads; only the
diff shows a line that was *deleted*. They overlap on the changed lines and that redundancy is
accepted, which is why this budget stays the smaller of the two: when something has to give, the
half that repeats gives first.

**Both budgets were raised once the extract had paid for itself.** They were set when a review spent
512 849 billed tokens across forty-nine round trips exploring the repository; the same review now
spends 115 702 in two. At the old figures a fifty-two-file change still cut ten files short — and one
of those cuts produced a **false finding**, from a model correctly reporting that it could not see a
method the budget had trimmed away. The room the extract bought is spent on not doing that.

**Why it is not a flatten.** It was one. The diff reaching the model carried whole unchanged files,
and `AiOperations` then cut the payload to 120 000 characters by truncation, with no marker: the
model received the first files in full and nothing from the rest, with no way to tell. Measured on
this repository's own commit `f4d0792`: 460 217 characters flattened, of which 120 000 survived — the
model never saw roughly three quarters of the change and reviewed it as though it had. The same
commit renders as 68 269 characters here, an 86 % reduction that now fits the budget whole.

### GIT-030 Branch-diff resolution prefers the remote-tracking ref over a same-named local branch
**Implementation**: `src/CodeFlow.App/Git/Diff.cs`
**Behaviour**: `resolve_branch_commit(repo, name)` — shared by `get_branch_diff`, `resolve_sha`, and `changed_files_between` (all consumed by `07-review-pipeline.md`'s PR flows, not exposed as their own commands in this file) — tries, in order: if `name` starts with `"refs/"`, that exact ref, verbatim, and nothing else. Otherwise, three candidates in order: `origin/{name}`, `refs/remotes/origin/{name}`, then bare `{name}` (a local branch) — the **first** that both revparses and peels to a commit wins. This deliberately prefers the remote-tracking branch over a possibly-stale same-named local branch, because a stale local branch is exactly what would make an up-to-date PR diff come back empty (per the source comment) — it also means it never looks at any remote other than `origin` by name.
**Inputs / outputs**: `repo_path, name: string` → `Commit`, `Err("Could not find branch '{name}' locally or on origin — try fetching this repository first.")` if none resolve.
**Edge cases**: `get_branch_diff(path, base, head)` diffs from `merge_base(base, head)` to `head`'s tree — the same "what would this PR bring in" comparison a PR review shows, computed without any network call.
**Frontend dependency**: `07-review-pipeline.md` (PR diff/review flows in `src/CodeFlow.App/Review/ReviewCommands.cs`).
**Markers**: none

### GIT-039 A branch's whole contribution is one comparison, not two diffs added together
**Implementation**: `Diff.BranchContribution` (`src/CodeFlow.App/Git/Diff.cs`)
**Behaviour**: the merge base of `baseRef` and `HEAD`, compared against
`DiffTargets.WorkingDirectory | DiffTargets.Index` — everything the branch has changed relative to
where it left the base, committed and pending alike, in a single `Compare<Patch>` call. `HEAD` rather
than a named branch: the point of this diff is the working tree, and a detached or just-branched
`HEAD` still has one.
**Inputs / outputs**: `repoPath, baseRef` → `IReadOnlyList<FileDiffInfo>`. Throws when the repository
has no commits, and when no merge base exists between the two.
**Edge cases**: the obvious implementation — `BranchDiff` concatenated with `Working` — is wrong: a
file touched in a commit of the branch *and* again uncommitted would appear twice, and a model
handed the same file twice reports the same finding twice. `GIT-030`'s `BranchDiff` is untouched by
this and keeps its specified behaviour; this is a second method, not a change to that one.
`DiffTargets.WorkingDirectory` implies `DiffModifiers.IncludeUntracked` in LibGit2Sharp 0.32.0
(verified in its source), so a new file the branch adds and has not staged is included.
**Frontend dependency**: `review_changes` with `scope: "branch"` (`14-work-items.md`, `WI-014`,
`WI-023`) — the only caller, on either side of its ticket axis.
**Markers**: none

### GIT-031 Two IPC surfaces never emit any equivalent of `CHECKOUT_CONFLICT_PREFIX`
**Implementation**: sweep across `src/CodeFlow.App/Git/`, `src/CodeFlow.App/Git/Remotes.cs`
**Behaviour**: confirmed by reading every function in scope: no file in this domain other than `src/CodeFlow.App/Git/Branches.cs` defines a `const ... PREFIX` used to tag an error string for frontend parsing (`src/CodeFlow.App/Git/Checkpoints.cs` has `REF_PREFIX`, but that is a ref-namespace constant, never placed into an error string). `merge_branch`'s conflict outcome is a structured `MergeOutcome.status == "conflicts"` field, not a string-prefixed error — conflicts are a **success** return, not an `Err`. Stash apply/pop conflicts and `commit`/`reset_to_commit` errors are all bare, unprefixed libgit2 or the standard library messages.
**Inputs / outputs**: n/a — negative finding.
**Edge cases**: n/a.
**Frontend dependency**: confirms `13-cross-language-contracts.md` only needs to carry `CHECKOUT_CONFLICT_PREFIX` from this domain.
**Markers**: none

### GIT-032 Every mutation in this domain goes through libgit2 — git hooks never fire
**Implementation**: sweep across `src/CodeFlow.App/Git/` — `commit` (`src/CodeFlow.App/Git/Diff.cs`), `merge_branch`/`complete_merge` (`src/CodeFlow.App/Git/Merge.cs`), `checkout_local_branch`/`checkout_detached`/`checkout_remote_tracking` (`src/CodeFlow.App/Git/Branches.cs`), `stash_save`/`stash_apply`/`stash_pop` (`src/CodeFlow.App/Git/Stash.cs`), `reset_to_commit` (`src/CodeFlow.App/Git/RepoStatus.cs`)
**Behaviour**: every commit, merge, checkout, stash and reset operation in this domain calls a `libgit2 function directly against the repository — there is no `Process.Start`("git")` anywhere in `git/*.rs`. Only `src/CodeFlow.App/Git/GitNetwork.cs`'s four network operations (clone/fetch/pull/push, GIT-034) shell out to the real `git` binary; everything else is pure libgit2. libgit2 is a from-scratch reimplementation of git's storage/plumbing that never invokes the `.git/hooks/*` scripts a real `git` CLI would run for these operations (`pre-commit`, `commit-msg`, `post-checkout`, `post-merge`, `pre-push`, etc. — `pre-push` in particular never fires because `push` itself is the one network op that *does* shell out, but even then it shells to `git push` in the *app's* subprocess, which does run hooks; the three operations that create local history — commit, merge, checkout — are the ones that silently skip hooks that a plain terminal `git commit`/`git merge`/`git checkout` in the same repo would run).
**Inputs / outputs**: n/a.
**Edge cases**: a repository with `core.hooksPath` or `.git/hooks/*` configured for `pre-commit`/`commit-msg`/`post-checkout`/`post-merge` will see those scripts silently never execute for any action taken through this app's UI, even though the same repo's hooks *do* fire for `git push` (shelled out, GIT-034) and would fire for anything the user does from an external terminal.
**Frontend dependency**: none directly, but this is a real behavioural difference from the CLI that repo owners relying on local hooks would notice.
**Markers**: `DIVERGENCE-GIT-b` — deliberate; verified true by exhaustive reading of every git-mutating call in scope (no `ProcessStartInfo`("git")` outside `src/CodeFlow.App/Git/GitNetwork.cs`). Preserve exactly: the .NET port's equivalent of these operations (via LibGit2Sharp or hand-rolled) must likewise not invoke local hooks for commit/merge/checkout/stash/reset, only for the shelled-out network operations.

### GIT-033 No libgit2 credential callback anywhere — network auth is entirely the OS/git's problem
**Implementation**: sweep across `src/CodeFlow.App/Git/`, `src/CodeFlow.App/Git/Remotes.cs` — confirmed no `RemoteCallbacks` or credential-callback usage anywhere in `src/CodeFlow.App/`
**Behaviour**: none of the git2-based code in this domain ever constructs a `git2::RemoteCallbacks` or calls anything under `git2::Cred`. The only functions that touch a remote server over the network — clone, fetch, fetch_refspec, pull, push — are precisely the four in `src/CodeFlow.App/Git/GitNetwork.cs`, and all four shell out to the system `git` binary (`ProcessStartInfo`("git")`) rather than using libgit2's own (much more limited, painful-to-wire-up) network stack. This is stated as the file's own module doc comment: "the user's existing SSH keys, HTTPS credential manager, and global git config are reused as-is — never through a generic shell-exec surface exposed to the frontend" (the child process only ever runs the literal argv built in this file; there is no user-controlled shell string).
**Inputs / outputs**: n/a.
**Edge cases**: any repo whose auth depends on SSH agent forwarding, a platform credential manager (Git Credential Manager, `osxkeychain`, `libsecret`), or a conditional `includeIf`-scoped `.gitconfig` continues to work exactly as it would from a terminal, because it *is* the same `git` binary the terminal would invoke, inheriting the same environment and config resolution.
**Frontend dependency**: none directly — this is why `git_clone`/`git_fetch`/`git_pull`/`git_push` never need any credential-related parameter, unlike a hypothetical libgit2-native implementation.
**Markers**: `DIVERGENCE-GIT-c` — deliberate; the .NET port must likewise shell out (`git.exe`/`git`) for these four operations rather than using LibGit2Sharp's credential-callback API, to keep SSH keys/credential managers/`includeIf` working unchanged.

### GIT-034 Network operations: argv, line-buffered progress, and the `git:done` fallback message
**Implementation**: `src/CodeFlow.App/Git/Remotes.cs`
**Behaviour**: `run_streamed(app, op, cwd, args)` spawns `git` with `args` (piped stdout+stderr), and concurrently (two the async runtime tasks) line-buffers each stream (`BufReader.lines()`) — each line, as soon as it is read, is emitted as `app.emit("git:progress", GitProgressEvent { op, line })` (stdout task) or the same event (stderr task; git's own progress output largely goes to stderr, which is why both streams are treated identically here rather than stderr being an "error" signal). Both tasks also collect every line they saw into a `IReadOnlyList<string>` for use after the process exits. Argv per operation:
- `clone(app, url, dest)` → `git clone <url> <dest>`, no `cwd` (runs in the process's own working directory).
- `fetch(app, repo_path, remote)` → `git fetch <remote>` in `repo_path`; `remote` defaults to `"origin"` when `None`.
- `fetch_refspec(app, repo_path, remote, refspec)` → `git fetch <remote> <refspec>` in `repo_path` — not exposed as its own a registered command; called directly from `src/CodeFlow.App/Review/ReviewCommands.cs` (`07-review-pipeline.md`) with `remote` hardcoded to `"origin"` there, to fetch a PR's exact head ref (works even for fork PRs).
- `pull(app, repo_path)` → `git pull --no-edit` in `repo_path` — uses whatever merge/rebase/ff default the repo's git config resolves to, but `--no-edit` accepts git's own generated message for any merge commit it creates rather than opening an interactive editor (GIT-037): the child's stdin is never redirected, so there is no TTY to open one against.
- `push(app, repo_path, set_upstream)` → if `set_upstream`, first opens the repo (via `src/CodeFlow.App/Git/`::open`, libgit2) purely to read HEAD's branch shorthand (`Err("cannot push -u from a detached HEAD")` if detached), then runs `git push -u origin <branch>` — **`origin` is hardcoded**, not read from any remote configuration, so `push(..., true)` always fails to do anything useful on a repo whose only/intended remote is named something else. If `set_upstream` is false: plain `git push`, using the branch's already-configured upstream.

  After the child exits, `GitDoneEvent { op, success: status.success(), message }` is emitted once: `message` is `"ok"` on success; on failure it is `stderr` joined by `\n` if non-empty, else `stdout` joined by `\n` if non-empty, else the literal fallback, verbatim:
  `
  format!("git {op} exited with {status}")
  `
  (`{status}` is the sidecar's `Display` for `System.Diagnostics`::ExitStatus`, platform-dependent text like `exit status: 1` on non-Windows.) The function's own `void` on failure is `format!("git {op} failed: {detail}")` — the same `detail` text, differently wrapped, so the promise-rejection string and the `git:done` failure message are related but not byte-identical (the rejection additionally prefixes `"git {op} failed: "`).
**Inputs / outputs**: see argv above; events per `01-ipc-surface.md`'s table (`git:progress` ×2 producers, `git:done` ×1).
**Edge cases**: a process that writes nothing to either stream and exits non-zero (e.g. `git` not found — though that surfaces as a `spawn` error, not this path; more realistically a silent non-zero exit from an unusual git configuration) is the only way the fallback message fires. **There is no cancellation mechanism for any of these four operations** — unlike AI runs (`cancel_ai_run`, `05-ai-engines.md`), nothing in `src/CodeFlow.App/Git/GitNetwork.cs` or `src/CodeFlow.App/Git/GitCommands.cs` exposes a way to abort an in-flight clone/fetch/pull/push, and no code in this file kills the child process or its process tree under any circumstance other than the process exiting on its own. `AMBIGUOUS-GIT-b`.
**Frontend dependency**: `onGitProgress`/`onGitDone` listeners (`renderer/src/lib/ipc/events.ts`) feed the progress log / toast shown during clone-repo, fetch, pull, and push actions.
**Markers**: `AMBIGUOUS-GIT-b` — no cancel/kill path exists for these four operations in the source; if the .NET port is expected to support cancelling an in-flight git network operation (as it does for AI runs), that is new behaviour with no the sidecar precedent to port, not a gap in this document.

### GIT-037 Pull never opens an interactive editor
**Implementation**: `src/CodeFlow.App/Git/GitNetwork.cs` (`PullAsync`)
**Behaviour**: `pull` runs `git pull --no-edit` rather than plain `git pull`. A fast-forward pull is unaffected either way — no merge commit is ever created, so there was never an editor to open. A **divergent** pull (local and remote both moved since the last common ancestor) requires git to create a merge commit, and without `--no-edit` git tries to open `$GIT_EDITOR`/`core.editor`/`$EDITOR` for the commit message. `RunStreamedAsync` never redirects the child's stdin, so in this app there is no TTY for that editor to attach to; the attempt fails, and — because the merge itself (the working-tree and index update) happens *before* the commit step — the pull would exit non-zero with the merge already applied but never committed, leaving the repository silently mid-merge (`MERGE_HEAD` set, `is_merging` true) with nothing telling the user why.
**Edge cases**: a repository with `core.editor` pointed at a script that itself never needs a TTY (e.g. `true`, or a non-interactive line editor) never hit this — only a real interactive editor invocation with no TTY does.
**Frontend dependency**: none directly; this closes the gap in what `repoStore.ts`'s `pull()` (`fetch`+merge in one shelled command) can leave behind, addressed alongside GIT-038 below.
**Test coverage**: `GitNetworkTests.cs`'s `Pulling_a_divergent_branch_completes_the_merge_without_an_editor` — pulls a genuinely divergent branch with `core.editor` pointed at a nonexistent binary (so any editor invocation would fail fast and loud rather than hang on a TTY prompt) and asserts the merge commit completes with `git:done success=true`.

### GIT-038 A background refresh's own failure is never reported as the mutation's failure
**Implementation**: `renderer/src/state/repoStore.ts` (`guarded`, `refreshing`, `withOneRetry`)
**Behaviour**: `pull()`, `fetch()` and `checkoutGuarded()` each run a mutating command (`git_pull`/`git_fetch`/a checkout) and then re-read several slices of repo state (`refreshAll()`'s seven parallel refreshers). Before this, both the mutation and every one of those follow-up reads shared one generic, unlabeled error message — a transient failure in a read-only refresh **right after** a mutation that had already fully succeeded looked identical to the mutation itself having failed. Each of `refreshStatus`/`refreshBranches`/`refreshCommits`/`refreshUnpushedCommits`/`refreshStashes`/`refreshRemotes`/`refreshMergeState` now carries its own `TranslationKey` (`refresh.status`, `refresh.branches`, …) and retries once, after a short delay (`REFRESH_RETRY_DELAY_MS`, 300ms), before giving up — absorbing a transient race reading repo state in the moments right after a mutating `git` child process exits. The retry is opt-in via the label and applies only to these read-only refreshes; a mutating command (checkout, stash apply, commit) is never retried automatically.
**Edge cases**: the on-demand, click-triggered loads that also go through `refreshing()` — a commit's file list, a file's diff (`selectCommit`/`selectCommitFile`) — pass no label and get neither the retry nor the labeled message, since they aren't racing a just-finished mutation.
**Frontend dependency**: none outward; this is renderer-internal. Reported by a user: a clean fast-forward `pull()` showed a generic error toast while the pull itself had already fully succeeded, with no known-bug entry covering it.
**Test coverage**: `repoStore.test.ts`, `describe("pull() and a transient post-pull refresh race")` — a refresh that fails once recovers silently on retry; one that fails persistently reports a toast naming the refresh, not the pull; a real `gitPull` failure still reports as the pull failing, unlabeled.

## Test coverage

| extracted case | Source | Fixture | Kind |
|---|---|---|---|
| `checkout_blocked_by_local_changes_is_tagged_for_the_ui` | `src/CodeFlow.App/Git/Branches.cs` | `git_branch.vectors.json#checkout-blocked-by-uncommitted-changes` | scenario |
| `stashing_the_local_changes_unblocks_the_same_checkout` | `src/CodeFlow.App/Git/Branches.cs` | `git_branch.vectors.json#stash-then-checkout-succeeds` | scenario |
| `discard_all_reverts_tracked_edits_and_removes_untracked_files` | `src/CodeFlow.App/Git/Diff.cs` | `git_diff.vectors.json#discard-all-reverts-tracked-removes-untracked` | scenario |
| `discard_all_keeps_staged_content` | `src/CodeFlow.App/Git/Diff.cs` | `git_diff.vectors.json#discard-all-keeps-staged-content` | scenario |
| `rename_top_stash_keeps_order` | `src/CodeFlow.App/Git/Stash.cs` | `git_stash.vectors.json#rename-top-stash-keeps-order` | scenario |
| `rename_non_top_stash_moves_it_to_top` | `src/CodeFlow.App/Git/Stash.cs` | `git_stash.vectors.json#rename-non-top-stash-moves-to-top` | scenario |
| `restores_edited_files_and_deletes_the_ones_the_run_created` | `src/CodeFlow.App/Git/Checkpoints.cs` | `git_checkpoint.vectors.json#restore-reverts-edits-and-deletes-created-files` | scenario |
| `snapshots_uncommitted_work_and_leaves_the_index_alone` | `src/CodeFlow.App/Git/Checkpoints.cs` | `git_checkpoint.vectors.json#snapshot-leaves-index-alone` | scenario |
| `a_run_that_changed_nothing_drops_its_checkpoint` | `src/CodeFlow.App/Git/Checkpoints.cs` | `git_checkpoint.vectors.json#unchanged-checkpoint-auto-drops` | scenario |

9 tests, all `scenario` kind (every one builds a real temporary git repository — via `git2::Repository.init` plus manual commits, or, for `src/CodeFlow.App/Git/Stash.cs`, by shelling out to the real `git` CLI to get authentic `git stash push`-style reflog messages). None of the 25 files in `docs/business-rules/test-vectors/README.md`'s "131 tests" count that fall under this document's scope are `vector`-kind or `behavioural`-kind — `src/CodeFlow.App/Git/RepoStatus.cs`, `src/CodeFlow.App/Git/Merge.cs`, `src/CodeFlow.App/Git/CommitGraph.cs`, `src/CodeFlow.App/Git/Remotes.cs`, `src/CodeFlow.App/Git/Identity.cs`, `src/CodeFlow.App/Git/GitNetwork.cs`, `src/CodeFlow.App/Git/GitCommands.cs`, and `src/CodeFlow.App/Git/Checkpoints.cs` carry zero ` functions.

## Markers raised

| Marker | Summary |
|---|---|
| ~~`BUG-GIT-a`~~ **CLOSED** | Rename detection was never enabled on any diff (`src/CodeFlow.App/Git/Diff.cs`, GIT-010) or status query (`src/CodeFlow.App/Git/RepoStatus.cs`, GIT-011), so the `"renamed"`/`"copied"` labels were unreachable dead branches — every rename showed as an unrelated delete+add pair. Closed with `SimilarityOptions.Renames` on the user-facing diffs and both status detection flags on; copy detection stays off (git's default) and Checkpoints' internal compare keeps `None` on purpose (its restore needs both halves of a rename). See `91-known-bugs.md`. |
| `AMBIGUOUS-GIT-a` | `checkout_remote_tracking` (GIT-006) silently reuses a pre-existing same-named local branch without checking or fixing up its upstream — intended reuse semantics vs. reject/re-point is not determined by the source. |
| `AMBIGUOUS-GIT-b` | No cancellation/process-kill path exists anywhere in `src/CodeFlow.App/Git/GitNetwork.cs` for clone/fetch/pull/push (GIT-034) — whether the .NET port should add one is a product decision with no the sidecar behaviour to port. |
| `DIVERGENCE-GIT-a` | Stash rename (GIT-014) is a deliberate drop-and-reappend reflog trick with no native git equivalent; it always reorders the renamed stash to the top of the stack. Must be preserved exactly, including the reordering. |
| `DIVERGENCE-GIT-b` | Every commit/merge/checkout/stash/reset in this domain goes through libgit2, so local git hooks (`pre-commit`, `commit-msg`, `post-checkout`, `post-merge`, etc.) never fire for those operations — verified by an exhaustive sweep finding no `ProcessStartInfo`("git")` outside `src/CodeFlow.App/Git/GitNetwork.cs`. Deliberate; preserve in the port. |
| `DIVERGENCE-GIT-c` | No libgit2 credential callback (`RemoteCallbacks`/`Cred`) exists anywhere in the tree — verified by grep across all of the shell. Network operations shell out to the system `git` binary precisely so SSH keys, credential managers, and `includeIf` config keep working unchanged. Deliberate; the .NET port must shell out too, not use LibGit2Sharp's credential API. |
