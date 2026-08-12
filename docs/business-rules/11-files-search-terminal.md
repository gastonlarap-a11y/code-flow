# 11 — Files, search, watcher, terminal

## Scope

- `src/CodeFlow.App/Files/FileOps.cs`, `PathGuards.cs`, `FileModels.cs`
- `src/CodeFlow.App/Files/Search.cs`, `GlobSet.cs`, `RepoWalk.cs`
- `src/CodeFlow.App/Files/RepoWatcher.cs`
- `src/CodeFlow.App/Terminal/TerminalRegistry.cs`, `ShellResolver.cs`
- `src/CodeFlow.App/Files/FileCommands.cs` (13 commands), `WatcherCommands.cs` (3),
  `src/CodeFlow.App/Terminal/TerminalCommands.cs` (4)

## Commands

Full parameter/return signatures live in `01-ipc-surface.md`; this is the one-line map from
command to what calling it does.

`src/CodeFlow.App/Files/FileCommands.cs`:
- `list_dir` — lists a repo-relative directory (or the repo root), `.git` hidden, dirs first.
- `read_file_text` — reads a repo-relative file as UTF-8 text.
- `write_file_text` — overwrites a repo-relative file with text.
- `write_file_bytes` — writes raw bytes to an absolute, user-chosen path (save dialog).
- `move_path` — moves/renames a repo-relative file or folder (explorer drag-and-drop).
- `create_dir` — creates a repo-relative directory, including missing parents.
- `create_file` — creates a new empty repo-relative file, refusing to overwrite.
- `open_in_default_app` — opens a repo-relative file with the OS's default handler.
- `reveal_in_file_manager` — opens an absolute directory path in Explorer/Finder.
- `open_in_vscode` — launches `code <path>`.
- `list_repo_files` — every non-ignored repo file, for the go-to-file palette.
- `search_repo` — content search across the repo's text files.
- `replace_in_repo` — find-and-replace across the repo (or one file), with a checkpoint.

`src/CodeFlow.App/Files/WatcherCommands.cs`:
- `start_watching` — starts (or restarts) the working-tree watcher for a repo path.
- `stop_watching` — stops and drops the watcher for a repo path.

`src/CodeFlow.App/Terminal/TerminalCommands.cs`:
- `open_terminal` — spawns a PTY running the platform shell in `cwd`.
- `write_terminal` — writes input bytes to a terminal session.
- `resize_terminal` — resizes a terminal session's PTY.
- `close_terminal` — kills and removes a terminal session.

## File operations (`src/CodeFlow.App/Files/FileOps.cs`)

### Two distinct path-traversal guards

`src/CodeFlow.App/Files/FileOps.cs` has **two** containment checks and they are not interchangeable — one is for paths
that are expected to already exist, the other for paths about to be created.

**`resolve_within_repo(repo_path, rel_path)`** — used for reads, writes, moves (both endpoints),
opening in the default app. Algorithm:
1. Canonicalize `repo_path` → `base`. Failure → `"invalid repo path: {e}"`.
2. Join `base` with `rel_path` → `candidate` (no existence check yet).
3. Canonicalize `candidate`; if canonicalization fails (path doesn't exist, e.g. mid-drag or a
   stale reference), **fall back to the un-canonicalized `candidate`** rather than erroring.
4. Reject unless `resolved.starts_with(&base)`. Rejection message, `VERBATIM`:
   `"path escapes the repository root"`.

This relies on canonicalization resolving `..` and symlinks; for a path that does *not* exist,
the step-3 fallback is a **lexical** normalisation (`Path.GetFullPath`), so `..` segments are
resolved away before the containment check either way — see `BUG-FILE-a` (closed) below.

**`resolve_new_path(repo_path, rel_path)`** — used only by `create_dir` and `create_file`,
i.e. paths that by definition don't exist yet, so canonicalize-then-`starts_with` can't see
through a crafted `..` segment (there is nothing on disk to canonicalize). Algorithm:
1. Trim `rel_path`. Empty after trim → `"name cannot be empty"`.
2. Parse as a `Path` and require **every** component to be `Component.Normal` (i.e. reject
   `..`, `.`, root/prefix components, and — because `Path.is_absolute()` is also checked —
   absolute paths). Any component that isn't a plain name, or an absolute path → rejection
   message, `VERBATIM`: `"invalid path: {rel_path}"` (note: original, untrimmed `rel_path` is
   interpolated here, not the trimmed `rel`).
3. Canonicalize `repo_path` → `base` (same error as above on failure).
4. Join `base` with the validated relative path. No existence or containment check is needed
   beyond step 2, because a path made only of `Normal` components cannot climb out of `base`.

Net effect: `resolve_within_repo` trusts the filesystem (via canonicalization) to collapse
`..`; `resolve_new_path` trusts component-kind inspection instead, because there is no
filesystem entry yet to canonicalize. A reimplementation that uses one guard for both purposes
will either wrongly reject legitimate creates (if it requires prior existence) or wrongly admit
a `..`-escaping create (if it uses `resolve_within_repo`'s fallback path on a nonexistent
target).

`BUG-FILE-a`, **closed**: in 1.7.2, when `candidate` did not exist, `canonicalize()`
failed and the code fell back to the raw joined `candidate` (step 3 above). At that point the
`starts_with(&base)` check is comparing an uncanonicalized path against a canonicalized `base`.
Because `Path.join` with a relative `rel_path` containing `..` produces a path that, when
walked lexically by `starts_with`, still begins with `base`'s components before the `..` takes
effect textually (the sidecar's `Path.starts_with` is component-wise, not `..`-aware), a `rel_path`
like `foo/../../escaped` against an existing `foo` combined with a nonexistent final target
could pass containment purely lexically for the parts that exist, while the final nonexistent
leaf is never canonicalized to prove it lands outside `base`. In practice this guard is only
reachable for `read_file_text`, `write_file_text`, `move_path`'s source/dest, and
`open_in_default_app` — all of which are expected to reference **existing** entries, so the
fallback branch is exercised mainly when a caller races a delete or types a path to something
that was never created. The suspected-correct behaviour is to canonicalize the parent
directory (which does exist) and join only the final component, the way `resolve_new_path`
effectively does. Ported as-is; not fixed.

### `list_dir`

Lists a directory: `sub_path: None` lists the repo root, `Some(p)` lists `p` resolved via
`resolve_within_repo`. Skips an entry literally named `.git`. Each entry becomes a
`FileEntry { name, path, is_dir }` where `path` is the repo-relative path with `\` replaced by
`/`. Sort order: directories before files, then case-insensitive (`to_lowercase()`) name
comparison within each group.

### `read_file_text` / `write_file_text`

- `read_file_text`: resolves via `resolve_within_repo`; if the target is a directory, returns
  `"{rel_path} is a folder, not a file"` (`VERBATIM`) instead of surfacing the OS's
  "Is a directory" I/O error. Otherwise `System.IO` — any invalid-UTF-8 content
  surfaces as that call's I/O error string.
- `write_file_text`: resolves via `resolve_within_repo`, then `System.IO` — full overwrite,
  no partial-write protection beyond what the OS gives `write`.

### `write_file_bytes`

Not repo-scoped — writes to an **absolute** path chosen by the user in a native save dialog
(today: exporting a code-snapshot PNG). Rejects a non-absolute `path`:
`"expected an absolute path, got: {path}"`. Rejects a missing/non-directory parent:
`"no such folder: {}"` (parent's display path). The dialog itself is treated as the
authorization — there is no repo-containment check here by design.

### `move_path`

Moves/renames `from_rel` (repo-relative) into `dest_dir` (repo-relative; `""` means repo root),
keeping the source's file name. Order of checks:
1. Resolve `source` via `resolve_within_repo`.
2. Extract the file name; failure (e.g. source is `/` or has no final component) →
   `"cannot move {from_rel}"`.
3. Canonicalize `repo_path` → `base`.
4. Resolve `dest`: `base` itself if `dest_dir.trim()` is empty, else `resolve_within_repo`.
5. Reject if `dest` is not a directory: `"{dest_dir} is not a folder"`.
6. Reject moving a directory into itself or a descendant of itself: compares canonical paths
   (`source.is_dir() && dest.starts_with(&source)`) so a symlinked route into the subtree is
   also caught. Message: `"cannot move a folder into itself"`.
7. Compute `target = dest.join(name)`. If `target == source`, it's a no-op — returns
   `from_rel` unchanged (dropped back where it already lives is not an error).
8. If `target.exists()`, refuse rather than overwrite: `"{name} already exists here"`.
9. `System.IO`(source, target)` — surfaces the OS error string on failure.
10. Strip `base` prefix from `target` to build the repo-relative return value, replacing `\`
    with `/`. Stripping failure (should be unreachable given the checks above) →
    `"moved outside the repository"`.

### `create_dir` / `create_file`

Both resolve via `resolve_new_path` (the creation guard, not the existence guard).

- `create_dir`: rejects if `full.exists()` — `"{rel_path.trim()} already exists"` — then
  `System.IO`, so `a/b/c` creates every missing intermediate directory in one
  call.
- `create_file`: creates missing parent directories first (`create_dir_all` on `full.parent()`),
  then opens with `OpenOptions.new().write(true).create_new(true)` so an existing file is
  reported, never silently truncated. On `ErrorKind.AlreadyExists` →
  `"{rel_path.trim()} already exists"`; any other I/O error is passed through verbatim.

### Opening things with the OS

- `open_in_default_app(repo_path, rel_path)`: resolves via `resolve_within_repo`, then
  `that(full)` (the `open` library, not the the shell `opener` plugin's JS API — chosen so path
  joining goes through `Path.join` rather than frontend string concatenation, which was
  producing mixed-separator paths on Windows that the plugin's scope check rejected).
- `reveal_in_file_manager(path)`: takes an **absolute** path directly (no repo scoping), calls
  `that(path)` on a directory — the OS's default handler for a directory is the file
  manager (Explorer/Finder), so this doubles as "reveal."
- `open_in_vscode(path)`: launches `code <path>`. On Windows, `code` is a `.cmd` shim, so
  spawning it directly fails to launch — the same class of issue as `npx` elsewhere in the
  codebase (`src/CodeFlow.App/Workspaces/SkillCommands.cs`, out of scope here). The command is built as
  `cmd /C code <path>` on Windows, and `code <path>` directly otherwise. Spawn failure:
  `"failed to launch VS Code (is \`code\` on PATH?): {e}"`.

## Repository search and replace (`src/CodeFlow.App/Files/Search.cs`)

### Caps (verified against source)

| Cap | Constant | Value | What happens when hit |
|---|---|---|---|
| Max files walked | `MAX_FILES` | `20_000` | `walk()` stops descending/collecting once `out.len() >= limit` — remaining files are simply never visited or reported. No truncation flag exists for this cap; it's silent. Applies identically to `list_files`, `search`, and `replace_all` (all three call `walk` with `MAX_FILES`). |
| Max file size searched | `MAX_SEARCH_FILE_BYTES` | `1024 * 1024` (1 MiB) | `read_text_file` checks `metadata.len() <= MAX_SEARCH_FILE_BYTES` before reading; a larger file is skipped entirely (not partially read) — no error, no flag, just absent from results. |
| Max line length surfaced | `MAX_LINE_CHARS` | `400` (chars, not bytes) | `truncate_line` takes the first 400 `char`s and appends `…`. The line is still reported as a hit; only its displayed text is cut. Matching itself runs against the *untruncated* line. |
| Max hits per file | `MAX_HITS_PER_FILE` | `20` | The per-file inner loop breaks once `in_file >= MAX_HITS_PER_FILE` (or the global cap is hit) — remaining matches in that file are silently dropped, file-local, not reflected in `truncated` by itself. |
| Max hits overall | `max_results` (caller-supplied, not a constant) | frontend-supplied | `search()` checks `hits.len() >= max_results` both before scanning a new file and inside the per-line loop; if hit, it returns immediately with `truncated: true`. `SearchOutcome.truncated` is also recomputed as `hits.len() >= max_results` after the full walk completes normally, so it is `true` whenever the result count is exactly at the ceiling even if that was the last possible match. |

### Gitignore is honoured by pruning, not filtering

`walk()` (shared by `list_files`, `search`, and `replace_all`) is a depth-first, manually
recursive directory walk. For every entry it computes a "probe" path — the child's repo-relative
path, with a trailing `/` appended **if the entry is a directory** — and calls
`repo.is_path_ignored(probe)` (libgit2 via `git2`). If ignored, the loop **`continue`s without
recursing into it** when it's a directory. This means an ignored directory's contents are never
read from disk at all — the walk does not descend into `node_modules/`, `target/`, etc. This is
categorically different from, and much faster than, walking everything and filtering the
resulting list afterward: `DIVERGENCE-FILE-a` — **do not reimplement this as "walk everything,
then filter."** The trailing-slash probe matters because `.gitignore` rules ending in `/`
(directory-only rules, e.g. `build/`) only match against a path git2 also sees as a directory;
without appending it, a directory-only ignore rule would fail to match and the walk would
wrongly descend into it. `.git` itself is skipped by literal name comparison, not by gitignore.
Entries are sorted by `file_name()` before recursing, so the palette's/search's file order is
stable across calls rather than filesystem-dependent.

### Matcher construction and flag composition order

`build_matcher(query, options)` composes the final `Regex` in this exact order:
1. Start from `query`. If `options.regex` is false, escape it with `escape` (so plain
   text search is literal). If `options.regex` is true, use `query` unmodified as the regex
   body.
2. If `options.whole_word` is true, wrap the body: `\b(?:{body})\b`. Applied **after** the
   regex/literal choice in step 1, so whole-word wrapping applies equally to a literal-escaped
   string and to a user-supplied regex.
3. If `options.case_sensitive` is false (the default), prefix with the inline flag: `(?i){body}`.
   Applied **last**, after whole-word wrapping, so it governs the whole assembled pattern
   including the `\b` boundaries.
4. Compile with `Regex.new`. On failure, the error is reduced to its **last line**, trimmed:
   `format!("invalid regular expression: {}", e.to_string().lines().last().unwrap_or("").trim())`
   — the `regex` library's error `Display` is multi-line (it prints the offending pattern with a
   caret), and only the final summary line is kept.

So the composition order is: **literal-escape-or-regex → whole-word wrap → case-insensitivity
flag → compile.** Reversing whole-word and case-insensitivity would not change behaviour here
(inline `(?i)` and `\b` commute), but reversing escape-vs-regex-selection with whole-word
wrapping would (wrapping a not-yet-escaped literal containing regex metacharacters in `\b(?:…)\b`
before escaping would corrupt it) — the order as implemented always escapes/selects first.

Include/exclude globs are a **separate** filter stage, applied per-file before the matcher ever
runs against that file's content (`passes_filters`, called from both `search` and
`replace_all`):
1. `build_globs(options.include)`: splits on `,`, trims each pattern, drops empty ones; `""` or
   an all-empty list → `None` (no include filter, i.e. everything passes). A pattern containing
   `/` is used as-is (matched against the full repo-relative path); a pattern with no `/` is
   rewritten to `**/{pattern}` so it matches the file name at any depth.
2. `build_globs(options.exclude)`: same construction.
3. `passes_filters(path, include, exclude)`: if an include set exists and `path` doesn't match
   it, reject. **Then**, independent of the include result, if an exclude set exists and `path`
   matches it, reject. Exclude is checked after (and regardless of) include, so an exclude
   pattern can remove files that an include pattern let through, but never the reverse — there
   is no way for include to re-admit something exclude removed, because they're evaluated in
   that fixed order, not merged into one predicate.

Binary detection (independent of all the above, applied while reading candidate files):
`looks_binary` checks the **first 8192 bytes** of a file for any NUL byte (same heuristic
`grep` uses); a match means the file is skipped for search/replace even if it passed the size
cap and glob filters.

### Find-and-replace with undo

`replace_all(repo_path, query, replacement, options, only_path)`:
1. Empty (trimmed) query → no-op result (`replacements: 0, files: 0, checkpoint_id: None`), no
   error.
2. Builds the same matcher/include/exclude as `search`.
3. Walks the same `walk()` (capped at `MAX_FILES`, gitignore-pruned identically).
4. **Plans every edit before writing anything**: for each candidate file (optionally filtered to
   exactly `only_path`, then through `passes_filters`, then through the binary/size gate in
   `read_text_file`), counts matches with `matcher.find_iter(&text).count()`; if zero, skips.
   Otherwise computes `matcher.replace_all(&text, replacement)` (supports `$1`-style capture
   group references when `options.regex` is true, via the `regex` library's replacement syntax).
   If the replaced text differs from the original, the `(path, new_text, match_count)` triple is
   queued in `planned` — nothing is written to disk in this phase. This means a file that fails
   to *read* partway through the walk cannot leave the tree half-replaced, but note there is no
   per-file cap analogous to `MAX_HITS_PER_FILE` here — every match in every planned file is
   counted and replaced.
5. If `planned` is empty, returns the same no-op result as an empty query.
6. Otherwise takes a checkpoint **before writing anything**: `git`::`Checkpoints`(repo_path, "replace-all")`.
   The checkpoint mechanism itself (what it snapshots and how `restore` applies it) is owned by
   the git-checkpoint domain document, not this one; `src/CodeFlow.App/Files/Search.cs` only calls
   `git`::`Checkpoints` / `git`::`Checkpoints` as an opaque dependency.
   Checkpoint failure is swallowed via `.ok()` — `checkpoint_id` becomes `None` and the replace
   proceeds anyway (a project-wide replace with no checkpoint is possible if the checkpoint step
   errors; the caller only knows via a `None` id).
7. Writes every planned file (`System.IO`); a write failure aborts the loop immediately and
   returns `Err("{rel}: {e}")` — files written before the failing one are **not** rolled back
   automatically at this layer, since rollback is exactly what the just-taken checkpoint is for.
8. Returns `ReplaceOutcome { replacements: <sum of per-file counts>, files: <planned.len()>,
   checkpoint_id }`.

What undo can and cannot restore: undo is the general-purpose checkpoint mechanism (shared with
AI-run undo), not a replace-specific diff/patch. It can restore whatever that checkpoint
mechanism captures for the repo as of just before the writes in step 7; it restores the whole
snapshot, not a surgical reversal of only the replaced occurrences, and if checkpoint creation
in step 6 failed (`checkpoint_id: None`), there is nothing to restore at all — the replace still
happened.

## Working-tree watcher (`src/CodeFlow.App/Files/RepoWatcher.cs`)

`DIVERGENCE-FILE-b`: The watcher is deliberately **not** a plain debounce, and a naive
reimplementation as one is a regression, not a simplification. Source comment, `VERBATIM`:

> Leading-edge-with-trailing-catchup throttle: the first event of a burst emits immediately;
> anything else within 400ms just marks a change as pending instead of being dropped outright.
> Once the burst goes quiet, the next poll tick (at most ~200ms later, and only once 400ms has
> actually elapsed since the last emit) flushes that pending change — a plain leading-edge
> throttle (emit-then-ignore-for-400ms, nothing after) silently lost whatever event landed
> inside that window with no later event to "wake it back up", which is exactly what happened
> when e.g. Claude's Edit tool wrote several files in a row: everything but the first write
> vanished until something unrelated (switching projects and back) forced a fresh reload.
>
> `Err` results (e.g. a `ReadDirectoryChangesW` buffer overflow on Windows when too many
> changes land at once) are treated the same as a real change rather than silently ignored —
> we don't know what changed, so the safe move is to refresh.

**Numbers, verified against source** (`start_watching`, the spawned thread's loop):
- Poll interval: `rx.recv_timeout(`TimeSpan.FromMilliseconds`(200))` — the loop wakes at least every
  200 ms even with no events.
- Minimum interval between emits: `TimeSpan.FromMilliseconds`(400)` — an emit only fires when
  `pending && last_emit.elapsed() >= `TimeSpan.FromMilliseconds`(400)`.
- `last_emit` is initialized to `Stopwatch`() - `TimeSpan`(10)`, so the very first
  qualifying event emits immediately (leading edge) rather than waiting out an initial 400 ms.
- On each loop iteration: a received `Ok(event)` sets `pending = true` only if
  `!is_noise(&event)`; a received `Ok(Err(_))` (a `notify` error, e.g. the Windows buffer
  overflow) **unconditionally** sets `pending = true` regardless of `is_noise`; a timeout tick
  does nothing by itself; a channel disconnect breaks the loop (thread exits).
- After handling whatever the `recv_timeout` produced, if `pending` and the 400 ms minimum has
  elapsed, the loop clears `pending`, resets `last_emit = `Stopwatch`()`, and emits
  `"repo:fs-changed"` with payload `{ repo_path }`.

**Warning for the port**: a fixed-window/plain debounce (schedule-on-first-event,
reset-on-every-event, fire-once-after-quiet) reintroduces exactly the dropped-multi-file-write
bug the comment above describes — an event landing inside an already-scheduled window with no
further event afterward would never flush. The poll-loop-plus-pending-flag design guarantees a
flush within ~(200 + 400) ms of the *first* event in a burst regardless of whether later events
in that burst ever arrive.

**`is_noise(event)`**: `true` if **any** path in `event.paths` has a file name ending in
`.lock`, or exactly equal to `FETCH_HEAD` or `COMMIT_EDITMSG`. These are git's own
mid-operation bookkeeping files — git touches/rewrites them constantly during normal operations
(index locks, fetch/rebase/commit bookkeeping) and reacting to them would refresh the tree on
git's own internal churn rather than on user-visible file changes. Noise events do not set
`pending`; they are otherwise indistinguishable from "nothing happened" this tick. Note:
`is_noise` returning `true` for one path in a multi-path event discards the *whole* event, not
just that path — but a real `Err` result bypasses `is_noise` entirely (see above), so a noisy
path can never mask a genuine overflow signal.

**Registry / lifecycle**: `WatcherRegistry` is a `Mutex<HashMap<string, RecommendedWatcher>>`
keyed by `repo_path`. `start_watching` first calls `stop_watching` for the same key (so
re-starting a watch on an already-watched repo replaces rather than duplicates it), then creates
a `FileSystemWatcher` (native OS watcher — FSEvents/inotify/ReadDirectoryChangesW
depending on platform, chosen by the `notify` library at compile time; `AMBIGUOUS-FILE-a`: the
exact per-OS backend is a `notify` library implementation detail not pinned in this source file,
and per-platform edge cases of that backend are not something this file's tests exercise). The
watch is `RecursiveMode.Recursive` over the whole `repo_path`. `stop_watching` removes the
entry from the map; dropping the `RecommendedWatcher` value is what stops the underlying OS
watch (no explicit "stop" call is made — it relies on `Drop`).

## Terminal / PTY (`src/CodeFlow.App/Terminal/TerminalRegistry.cs`)

### Shell selection per platform

- **Non-Windows**: `CommandBuilder.new(var("SHELL").unwrap_or("/bin/bash"))` — whatever
  the user's `$SHELL` is, falling back to `/bin/bash` if unset.
- **Windows**: always Git Bash, resolved by `windows_git_bash()`:
  1. Run `git --exec-path` (requires `git` on PATH — already a hard requirement elsewhere in
     the app for clone/fetch/pull/push). Its stdout is something like
     `<root>\mingw64\libexec\git-core`.
  2. Starting from that directory, walk **up to 6 ancestor levels** (`for _ in 0..6`), checking
     at each level whether `<ancestor>\bin\bash.exe` exists; return the first match.
  3. If `git --exec-path` didn't succeed or no ancestor within 6 levels had `bin\bash.exe`, fall
     back to two hardcoded candidates, checked in order:
     `C:\Program Files\Git\bin\bash.exe`, then `C:\Program Files (x86)\Git\bin\bash.exe`.
  4. If none of that resolves, `default_shell()` returns
     `Err("Git Bash not found — install Git for Windows (https://git-scm.com/download/win)")`
     (`VERBATIM`).

  `DIVERGENCE-FILE-c`: this refusal is deliberate — the code **does not fall back to
  PowerShell or `cmd`** if Git Bash can't be found, even though a working shell (PowerShell) is
  normally available on any Windows machine. Source comment, `VERBATIM`: "Terminals are always
  Git Bash on Windows — PowerShell is not an acceptable fallback here (it was previously used
  silently when bash.exe wasn't found at one of two hardcoded paths, which is exactly the case
  `windows_git_bash` above now resolves properly). If Git for Windows truly isn't installed,
  this surfaces as a normal command error instead of silently handing back a different shell."
  User-visible effect: `open_terminal` on Windows without Git for Windows installed fails
  outright with the message above, rather than opening any terminal at all — this is an
  intentional trade (fail loud vs. a silently different, less-capable shell), not a bug.
  When Git Bash **is** found, it's launched as `bash.exe --login -i` (login + interactive).

### PTY setup, resize, write

- `open_terminal(app, registry, cwd)`: opens a PTY of fixed initial size **rows: 30, cols: 100**
  (`pixel_width`/`pixel_height`: 0), builds the platform command via `default_shell()`, sets its
  `cwd`, spawns it on the PTY's slave side. A new session id is a fresh UUIDv4 (Uuid.new_v4()`
  as a string). The session (`writer`, `master`, `child`) is stored in
  `TerminalRegistry: Mutex<HashMap<string, TerminalSession>>` keyed by that id, before the reader
  thread is spawned, so the session is always registered by the time the caller could plausibly
  write/resize/close it.
- `write_terminal(registry, id, data)`: looks up the session (`"no such terminal session"` if
  absent), writes `data.as_bytes()` to the PTY writer, then explicitly flushes.
- `resize_terminal(registry, id, cols, rows)`: looks up the session, calls
  `master.resize(PtySize { rows, cols, pixel_width: 0, pixel_height: 0 })`. Note the caller's
  parameter order is `(cols, rows)` but they're placed into the `PtySize` struct as
  `rows, cols` — both are plain `ushort`s so this is not visible as a type error; it's just a
  matter of matching field names correctly at the call site. **Cross-checked and correct**
  (this closed `AMBIGUOUS-FILE-b`): `commands.ts:213` sends the object `{ id, cols, rows }`,
  the transport binds it to the command's named parameters, and `PtySize { rows, cols, … }` is a
  field-name initialiser, so declaration order is irrelevant. Both call sites
  (`TerminalPane.tsx:51,73`) pass `term.cols, term.rows`. Original note, superseded —
  cross-check against the TS caller
  (`resizeTerminal` in `renderer/src/lib/ipc/commands.ts`, `TerminalPane.tsx`) is out of this document's
  file scope and is flagged rather than assumed.
- `close_terminal(registry, id)`: removes the session from the map (if present) and calls
  `child.kill()` on it, discarding the kill result (`let _ =`). Closing an unknown id is not an
  error — it's a no-op (`if let Some(...)`).

### Read loop, UTF-8 decoding, exit detection

A dedicated thread per session, spawned inside `open_terminal` right after registration, reads
from a cloned PTY reader in a loop with a fixed **4096-byte** buffer:
- `Ok(0)` (EOF — the child process side closed) → loop `break`s.
- `n` → the `n` bytes are decoded with lossy UTF-8 decoding (invalid/partial UTF-8
  sequences, including a multi-byte sequence split across two reads at a 4096-byte boundary,
  are replaced with the Unicode replacement character rather than buffered/reassembled — there
  is no cross-read decoding state) and emitted as `"terminal:output"` with payload
  `{ id, data }`.
- `Err(_)` → loop `break`s (any read error, without distinguishing the error kind, ends the
  read loop the same as EOF).

After the loop exits by any of the three paths above, the thread emits `"terminal:exit"` with
payload `{ id }` exactly once. This is the **only** way the frontend learns a shell process
exited — there is no separate child-exit-code watch; exit detection is purely "the PTY reader
hit EOF or an error." The child's actual exit status/code is never read or surfaced (the `child`
handle is only ever `.kill()`ed in `close_terminal`, never `.wait()`ed to collect a status).

## Rules

### FILE-001 Two path-traversal guards, one for existing paths and one for new ones
**Implementation**: `src/CodeFlow.App/Files/FileOps.cs` (`resolve_within_repo`), `src/CodeFlow.App/Files/FileOps.cs` (`resolve_new_path`)
**Behaviour**: `resolve_within_repo` canonicalizes both the repo root and the candidate path and
requires the candidate to start with the canonicalized root, falling back to the uncanonicalized
candidate if it doesn't yet exist. `resolve_new_path` instead requires every path component to
be a plain (`Normal`) name and rejects absolute paths, needing no existence check.
**Inputs / outputs**: see "Two distinct path-traversal guards" above for exact rejection
messages.
**Edge cases**: a target that doesn't exist yet, hit via `resolve_within_repo` (e.g. a stale
drag target) — lexically normalised and then containment-checked since `BUG-FILE-a`'s fix. A
blank/whitespace-only name — rejected by `resolve_new_path`
("name cannot be empty"), not by `resolve_within_repo` (which has no such check).
**Frontend dependency**: `FileTree.tsx` (drag-and-drop calls `move_path`/`create_dir`/`create_file`), `renderer/src/lib/ipc/commands.ts`.
**Markers**: `BUG-FILE-a` **closed** — the not-on-disk fallback normalises lexically before the check; same shape as the shell's `isWithinRoot` (F0.6).

### FILE-017 A listing is right or it fails — it never guesses what an entry is
**Implementation**: `src/CodeFlow.App/Files/FileOps.cs` (`ListDir`) · `src/CodeFlow.App/Files/RepoWalk.cs` (`Walk`) ·
`renderer/src/components/editor/FileTree.tsx` (the mount and `refresh` failure paths)
**Behaviour**: both walks classify with `info is DirectoryInfo`, from the `FileSystemInfo` the
enumeration produced. Neither enumerates names and then asks `Directory.Exists` about them again.
**Inputs / outputs**: unchanged — `FileEntry.is_dir` carries the same answer whenever there is one.
**Edge cases**: the second look could be **refused while the enumeration succeeded**, and that is not
hypothetical. Selecting a repository under a TCC-protected folder — Documents, Desktop — puts macOS's
permission prompt in front of the user, and the listing already in flight is answered without the
access it needed. `Directory.Exists` returns `false` when access is denied, so **every folder was
reported as a file**; the explorer cached that, and granting the permission changed nothing because
nothing re-listed. The tree showed the repository's root files with none of its folders until the
side panel was switched away and back, which unmounts `FileTree` and lists again. In `RepoWalk` the
same answer is silent instead: the walk stops descending and every file beneath disappears from
search and from "go to file".
What the classification guarantees is not that it always knows — where the metadata is unreadable
.NET throws rather than misclassify — but that it never answers **wrongly**. Loud is the point: the
mount and refresh paths report the failure, refresh keeps the last good listing rather than replacing
it with what survived, and a test pins the refusal using a directory that is readable but not
traversable, which is the same split a permission prompt opens up. A symlink to a directory still
reports as one, pinned by its own test since that is the behaviour the change could have shifted.
**Frontend dependency**: `FileTree.tsx`, through `is_dir` and through the two failure messages.
**Markers**: `BUG-FILE-a` — **fixed**.

### FILE-018 A virtualized row that measures zero is hidden, not empty
**Implementation**: `renderer/src/lib/ui/rowMeasurement.ts` · `renderer/src/lib/useTreeVirtualizer.ts`
**Behaviour**: `measureElement` returns the row height whenever the observed size is `0`, and the
observer's `borderBoxSize` otherwise. Both trees run on that hook, so `FileTree` and `CollectionTree`
are covered by one rule.
**Inputs / outputs**: sub-pixel sizes are preserved — these rows measure 23.5px, and rounding
accumulates into a visibly wrong scroll height over a long tree.
**Edge cases**: `App.tsx:128` keeps a view mounted and hides it with `display: none` when you switch
tabs, which is deliberate — it is what stops a tab switch from killing editor state. But a hidden
element measures nothing, so the tree stayed observed while off screen and every row reported `0`.
Those zeros reached the size cache, and coming back the offsets were computed from them: the list
collapsed and the rows at the top went missing. Since directories sort first (`FILE-002`), that read
as **"the folders disappeared"** — the same symptom as `FILE-017`, from an unrelated cause, which is
why fixing one did not close the other. Switching the *side panel* looked like the cure only because
`EditorView` unmounts its tree there rather than hiding it, so that path built a fresh virtualizer.
TanStack documents `useCachedMeasurements` for this, toggled around the hiding; it is not used,
because it would require a tree three levels down to know when an ancestor hides it and that
coordination rots the first time someone adds a view. A zero needs nobody's cooperation.
**Frontend dependency**: none beyond the two trees.
**Markers**: `BUG-FILE-b` — **fixed**.

### FILE-002 `list_dir` sorts directories first, then case-insensitively by name
**Implementation**: `src/CodeFlow.App/Files/FileOps.cs`
**Behaviour**: Reads one directory level (root or `sub_path`), drops `.git`, builds
repo-relative `/`-separated paths, sorts directories before files and within each group by
`name.to_lowercase()`.
**Inputs / outputs**: `FileEntry { name, path, is_dir }`.
**Edge cases**: `sub_path: None` lists the repo root itself.
**Frontend dependency**: `FileTree.tsx`.
**Markers**: none.

### FILE-003 `move_path` refuses self-containment and destination collisions
**Implementation**: `src/CodeFlow.App/Files/FileOps.cs`
**Behaviour**: Rejects moving a directory into itself or a descendant (canonical-path
`starts_with` check, catches symlinked routes too), rejects an existing name at the
destination rather than overwriting, and treats a move back to its current location as a
no-op success.
**Inputs / outputs**: rejection strings `"cannot move a folder into itself"`,
`"{name} already exists here"`, `"{dest_dir} is not a folder"`; success returns the new
repo-relative path.
**Edge cases**: moving to `""` targets the repo root.
**Frontend dependency**: `FileTree.tsx` drag-and-drop.
**Markers**: none.

### FILE-004 `create_file` never truncates an existing file
**Implementation**: `src/CodeFlow.App/Files/FileOps.cs`
**Behaviour**: Uses `OpenOptions.create_new(true)` so an existing file at the target path is
reported as an error, never silently emptied; missing parent directories are created first.
**Inputs / outputs**: `"{rel_path.trim()} already exists"` on collision.
**Edge cases**: nested path with no existing parents (`a/b/c.ts`) creates all of them.
**Frontend dependency**: `FileTree.tsx`.
**Markers**: none.

### FILE-005 `write_file_bytes` is the one file op that is not repo-scoped
**Implementation**: `src/CodeFlow.App/Files/FileOps.cs`
**Behaviour**: Writes raw bytes to an absolute, caller-supplied path with no containment check
against any repo — only checks the path is absolute and its parent directory exists. The
native save dialog is the authorization.
**Inputs / outputs**: `"expected an absolute path, got: {path}"`, `"no such folder: {}"`.
**Edge cases**: none beyond the two checks above.
**Frontend dependency**: `CodeSnapModal.tsx` (exporting a code-snapshot PNG).
**Markers**: `DIVERGENCE-FILE-d` — deliberately unscoped by design (see docstring), not an
oversight; do not add repo containment to this one command when porting.

### FILE-006 `open_in_vscode` shims through `cmd /C` on Windows
**Implementation**: `src/CodeFlow.App/Files/FileOps.cs`
**Behaviour**: On Windows, spawns `cmd /C code <path>` because `code` is a `.cmd` shim that
fails to launch when spawned directly. On other platforms, spawns `code <path>` directly.
**Inputs / outputs**: spawn failure → `"failed to launch VS Code (is \`code\` on PATH?): {e}"`.
**Edge cases**: none.
**Frontend dependency**: `layout/sidebar/ProjectRow.tsx` (the row's "open in VS Code" action).
**Markers**: none.

### FILE-007 List/search/replace all share one gitignore-pruning walk with a hard file cap
**Implementation**: `src/CodeFlow.App/Files/Search.cs` (`walk`, `list_files`)
**Behaviour**: A single recursive `walk()` function backs `list_files`, `search`, and
`replace_all`. It prunes ignored directories during descent (never reads their contents from
disk) rather than filtering a full listing afterward, and stops collecting entirely once
`MAX_FILES` (20 000) entries have been gathered, silently — no truncation flag exists for this
particular cap.
**Inputs / outputs**: sorted (`file_name()`), repo-relative, `.git` excluded by literal name.
**Edge cases**: a bare (non-bare) repo required — `workdir()` returning `None` (bare repo) →
`"bare repository"`.
**Frontend dependency**: `FilePalette.tsx` (`list_repo_files`), `SearchPanel.tsx`/`AnchorsPanel.tsx`/`goToDefinition.ts` (`search_repo`).
**Markers**: `DIVERGENCE-FILE-a`.

### FILE-008 Search caps: 20 000 files, 1 MiB/file, 400 chars/line, 20 hits/file
**Implementation**: `src/CodeFlow.App/Files/Search.cs`, `src/CodeFlow.App/Files/Search.cs`
**Behaviour**: See the caps table above for the exact effect of each. Only the caller-supplied
`max_results` ceiling sets `SearchOutcome.truncated`. `MAX_FILES` and `MAX_SEARCH_FILE_BYTES`
are fully silent (files simply absent from results, no per-file or per-result signal).
`MAX_LINE_CHARS` has a visible-but-local signal — the trailing `…` on the returned line text —
but no boolean flag. `MAX_HITS_PER_FILE` is silent (extra matches in an already-20-hit file are
simply not reported, with no indication that file had more).
**Inputs / outputs**: `SearchOutcome { hits: IReadOnlyList<SearchHit>, truncated: bool }`.
**Edge cases**: an empty (post-trim) query returns an empty, non-truncated result without
touching the filesystem.
**Frontend dependency**: `SearchPanel.tsx`.
**Markers**: none (numbers verified against source; see table above).

### FILE-009 Matcher composition: escape/regex, then whole-word, then case-insensitivity
**Implementation**: `src/CodeFlow.App/Files/Search.cs`
**Behaviour**: Exact order — literal-escape (unless `regex` is set) → wrap in `\b(?:…)\b` if
`whole_word` → prefix `(?i)` unless `case_sensitive`. Regex compile errors are reduced to their
last line, trimmed, prefixed `"invalid regular expression: "`.
**Inputs / outputs**: see "Matcher construction and flag composition order" above.
**Edge cases**: an unterminated/invalid regex with `regex: true` reports the error instead of
panicking (covered by `an_unfinished_regex_reports_itself_instead_of_panicking`).
**Frontend dependency**: `SearchPanel.tsx`.
**Markers**: none.

### FILE-010 Include/exclude globs are two independent stages, exclude always wins
**Implementation**: `src/CodeFlow.App/Files/Search.cs`
**Behaviour**: Comma-separated glob lists; a pattern without `/` matches by file name at any
depth (`**/{pattern}` rewrite). `passes_filters` checks include first, then exclude
independently — exclude can remove what include admitted, never the reverse.
**Inputs / outputs**: empty/all-blank pattern list → no filter (`None`, i.e. everything passes
that stage).
**Edge cases**: a pattern containing `/` matches the full repo-relative path instead of just
the file name.
**Frontend dependency**: `SearchPanel.tsx`.
**Markers**: none.

### FILE-011 Replace plans every edit before writing any, then checkpoints before writing
**Implementation**: `src/CodeFlow.App/Files/Search.cs`
**Behaviour**: Computes and holds every `(path, new_text, count)` in memory first; only after
planning is complete does it take a checkpoint (`Checkpoints`, best-effort — failure
is swallowed to `None`) and then write files one by one. A write failure mid-loop aborts with
`Err`, leaving already-written files as-is (rollback is what the checkpoint is for, not an
in-process undo).
**Inputs / outputs**: `ReplaceOutcome { replacements, files, checkpoint_id: string? }`.
`$1`-style capture group references work when `options.regex` is true.
**Edge cases**: empty query or nothing matched → no-op result, no checkpoint taken. `only_path`
scopes the whole operation to one repo-relative file.
**Frontend dependency**: `SearchPanel.tsx` (`replaceInRepo`).
**Markers**: none beyond the checkpoint being an opaque external dependency (owned by the git
checkpoint document).

### FILE-012 The watcher is a 200 ms poll loop with a 400 ms leading-edge-plus-catchup throttle
**Implementation**: `src/CodeFlow.App/Files/RepoWatcher.cs`
**Behaviour**: Not a plain debounce — see "Working-tree watcher" above for the full mechanism,
numbers, and the verbatim source comment explaining why.
**Inputs / outputs**: emits `"repo:fs-changed"` with `{ repo_path }`.
**Edge cases**: a `notify` error (e.g. Windows `ReadDirectoryChangesW` buffer overflow) is
treated as "something changed," bypassing `is_noise`.
**Frontend dependency**: `App.tsx` (`startWatching`/`stopWatching`), `renderer/src/lib/ipc/events.ts`
(`repo:fs-changed` listener).
**Markers**: `DIVERGENCE-FILE-b`.

### FILE-013 `is_noise` filters git's own bookkeeping files out of watch events
**Implementation**: `src/CodeFlow.App/Files/RepoWatcher.cs`
**Behaviour**: An event is noise if any of its paths has a file name ending in `.lock`, or
exactly `FETCH_HEAD` or `COMMIT_EDITMSG`. Noise events never set `pending`; real `Err` results
from `notify` always do, regardless of `is_noise`.
**Inputs / outputs**: boolean gate on whether an event contributes to the pending-change flag.
**Edge cases**: one noisy path in a multi-path event discards the whole event (from
contributing to `pending`), but only for `Ok` results — errors are never routed through
`is_noise`.
**Frontend dependency**: none directly (internal to the watcher thread).
**Markers**: none.

### FILE-014 Windows terminals are Git Bash, resolved via `git --exec-path` plus a 6-level ancestor walk, with no shell fallback
**Implementation**: `src/CodeFlow.App/Terminal/TerminalRegistry.cs`
**Behaviour**: See "Shell selection per platform" above for the full resolution order and the
deliberate refusal to fall back to PowerShell/`cmd`.
**Inputs / outputs**: `"Git Bash not found — install Git for Windows (https://git-scm.com/download/win)"` on total resolution failure.
**Edge cases**: `git --exec-path` succeeds but no ancestor within 6 levels has `bin\bash.exe` →
falls through to the two hardcoded `Program Files` candidates before failing.
**Frontend dependency**: `state/terminalStore.ts` (`openTerminal`).
**Markers**: `DIVERGENCE-FILE-c`.

### FILE-015 Terminal exit is detected only by the PTY reader loop ending, never by child exit status
**Implementation**: `src/CodeFlow.App/Terminal/TerminalRegistry.cs`
**Behaviour**: The per-session reader thread emits `"terminal:exit"` exactly once, after its
read loop ends via EOF (`Ok(0)`), any read error, or (implicitly) UTF-8 decode never being able
to block that loop since `from_utf8_lossy` cannot fail. The child's exit code is never
retrieved (`child.kill()` in `close_terminal` is the only interaction with `child` besides
spawn).
**Inputs / outputs**: `"terminal:exit"` payload `{ id }`; output data is emitted per read as
`"terminal:output"` `{ id, data }`, decoded with lossy UTF-8 decoding per 4096-byte chunk
with no cross-chunk decoding state.
**Edge cases**: a multi-byte UTF-8 character split across a 4096-byte read boundary is decoded
as replacement character(s) in each half rather than reassembled.
**Frontend dependency**: `TerminalPane.tsx`, `state/terminalStore.ts`, `renderer/src/lib/ipc/events.ts`.
**Markers**: `AMBIGUOUS-FILE-c` — whether the observed replacement-character artefacting on a
split multi-byte character is acceptable/expected product behaviour, or something the frontend
happens to paper over (e.g. via a terminal emulator library that itself buffers), is not
determinable from this file alone.

### FILE-016 `resize_terminal`'s parameter names and the `PtySize` field assignment
**Implementation**: `src/CodeFlow.App/Terminal/TerminalRegistry.cs`
**Behaviour**: The command signature is `resize_terminal(id, cols, rows)`; the implementation
builds `PtySize { rows, cols, pixel_width: 0, pixel_height: 0 }`, i.e. assigns the `cols`
parameter to the `PtySize.cols` field and `rows` to `PtySize.rows` — field names line up with
parameter names, this is not a swap bug internally. Flagged only because confirming the
frontend supplies `(cols, rows)` in that order requires reading outside this document's file
scope.
**Inputs / outputs**: `void`.
**Edge cases**: none within this file.
**Frontend dependency**: `TerminalPane.tsx` (`resizeTerminal`).
**Markers**: none. `AMBIGUOUS-FILE-b` was raised here and **resolved** during the merge pass —
the argument order is correct end to end. See `90-ambiguities.md`.

## Test coverage

| extracted case | Source | Fixture | Kind |
|---|---|---|---|
| `creates_nested_file_and_dir` | `src/CodeFlow.App/Files/FileOps.cs` | `fsops.vectors.json#creates-nested-file-and-dir` | scenario |
| `moves_within_the_repo_and_refuses_the_destructive_cases` | `src/CodeFlow.App/Files/FileOps.cs` | `fsops.vectors.json#move-refuses-destructive-cases` | scenario |
| `rejects_duplicates_empty_names_and_traversal` | `src/CodeFlow.App/Files/FileOps.cs` | `fsops.vectors.json#rejects-duplicates-empty-traversal` | scenario |
| `lists_source_files_and_skips_ignored_ones` | `src/CodeFlow.App/Files/Search.cs` | `search.vectors.json#lists-and-prunes-gitignored` | scenario |
| `finds_matches_case_insensitively_by_default` | `src/CodeFlow.App/Files/Search.cs` | `search.vectors.json#case-insensitive-default` | scenario |
| `case_sensitive_search_respects_case` | `src/CodeFlow.App/Files/Search.cs` | `search.vectors.json#case-sensitive-respects-case` | scenario |
| `reports_truncation_instead_of_pretending_it_found_everything` | `src/CodeFlow.App/Files/Search.cs` | `search.vectors.json#truncation-flag` | scenario |
| `whole_word_stops_matching_inside_longer_words` | `src/CodeFlow.App/Files/Search.cs` | `search.vectors.json#whole-word-boundary` | scenario |
| `regex_mode_treats_the_query_as_a_pattern` | `src/CodeFlow.App/Files/Search.cs` | `search.vectors.json#regex-mode-vs-literal` | scenario |
| `an_unfinished_regex_reports_itself_instead_of_panicking` | `src/CodeFlow.App/Files/Search.cs` | `search.vectors.json#invalid-regex-error` | scenario |
| `include_and_exclude_globs_narrow_the_scan` | `src/CodeFlow.App/Files/Search.cs` | `search.vectors.json#include-exclude-globs` | scenario |
| `replace_rewrites_matches_and_leaves_an_undo_behind` | `src/CodeFlow.App/Files/Search.cs` | `search.vectors.json#replace-and-checkpoint-undo` | scenario |
| `replace_can_be_scoped_to_one_file_and_can_use_capture_groups` | `src/CodeFlow.App/Files/Search.cs` | `search.vectors.json#replace-scoped-with-capture-groups` | scenario |

All 13 tests are `kind: "scenario"` — every one builds a temp directory and a real (`git2`)
repository on disk before calling the function under test, so none qualify as a pure-function
`"vector"`. `src/CodeFlow.App/Files/RepoWatcher.cs` and `src/CodeFlow.App/Terminal/TerminalRegistry.cs` carry **zero** ` functions (confirmed by
inspection: no ` module in either file) — their behaviour is captured above as
prose plus the acceptance checklist below, and neither contributes rows to the 131-test sum
beyond what's listed here.

### Watcher/terminal acceptance checklist (behavioural, no extracted cases exist)

- Starting a watch on a repo, then writing one file, emits exactly one `repo:fs-changed` for
  that repo within roughly 200-600 ms (poll interval + throttle), not before ~zero ms and not
  requiring a second unrelated write to "wake up" a dropped event.
- Writing N files in quick succession (all within one 400 ms throttle window) still results in
  at least one `repo:fs-changed` after the burst — never zero.
- Writing only to `*.lock`, `FETCH_HEAD`, or `COMMIT_EDITMSG` produces no emission.
- Calling `start_watching` twice for the same `repo_path` does not leave two active watchers
  (second call stops the first before starting a new one).
- `stop_watching` on a repo with no active watcher is not an error.
- `open_terminal` on macOS/Linux launches `$SHELL` (or `/bin/bash`) in the given `cwd` and its
  stdout/stderr reach the frontend via `terminal:output` events.
- `open_terminal` on Windows launches Git Bash (`bash.exe --login -i`) when Git for Windows is
  installed, and fails with the exact "Git Bash not found" message (never silently substituting
  PowerShell/`cmd`) when it is not.
- Killing the shell process (e.g. typing `exit`) results in exactly one `terminal:exit` for
  that session id.
- `write_terminal` to a closed/unknown session id returns `"no such terminal session"` rather
  than panicking.
- `close_terminal` on an already-closed/unknown id succeeds as a no-op.

## Markers raised

| Marker | Where | Summary |
|---|---|---|
| ~~`BUG-FILE-a`~~ **CLOSED** | `src/CodeFlow.App/Files/PathGuards.cs` | The containment check degraded to comparing the raw joined path when the candidate did not exist yet, so `..` segments survived. Closed: the fallback is a lexical `Path.GetFullPath`, and a write through `../` to a brand-new file is refused before touching disk. See `91-known-bugs.md`. |
| `AMBIGUOUS-FILE-a` | `src/CodeFlow.App/Files/RepoWatcher.cs` (`FileSystemWatcher`) | The exact per-OS native watcher backend is a `notify` library compile-time choice not pinned in this source file. |
| ~~`AMBIGUOUS-FILE-b`~~ | `src/CodeFlow.App/Terminal/TerminalRegistry.cs` (`resize_terminal`) | **Resolved in the merge pass** — the `(cols, rows)` order is correct end to end; `PtySize` is initialised by field name, so declaration order cannot matter. Kept here so the id is not reused. |
| `AMBIGUOUS-FILE-c` | `src/CodeFlow.App/Terminal/TerminalRegistry.cs` | Whether replacement-character artefacting on a UTF-8 sequence split across a 4096-byte read boundary is acceptable product behaviour, or masked by a frontend terminal emulator library, is undeterminable from this file. |
| `DIVERGENCE-FILE-a` | `src/CodeFlow.App/Files/Search.cs` | Gitignore is honoured by pruning directories during the walk, not filtering results afterward — do not reimplement as walk-then-filter. |
| `DIVERGENCE-FILE-b` | `src/CodeFlow.App/Files/RepoWatcher.cs` | The watcher's poll-loop-plus-pending-flag design is deliberately not a plain debounce; a naive debounce reintroduces a fixed dropped-multi-file-write bug. |
| `DIVERGENCE-FILE-c` | `src/CodeFlow.App/Terminal/TerminalRegistry.cs` | Windows terminals deliberately refuse to fall back to PowerShell/`cmd` when Git Bash can't be resolved. |
| `DIVERGENCE-FILE-d` | `src/CodeFlow.App/Files/FileOps.cs` | `write_file_bytes` is deliberately not repo-scoped; the save dialog is the authorization. |
