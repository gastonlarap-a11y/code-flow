# 07 — PR review pipeline

## Scope

- `src/CodeFlow.App/Review/` — `ReviewCommands.cs`, `ReviewRun.cs`, `ReviewRunStore.cs`,
  `ReviewMemory.cs`, `MemoryFinding.cs`, `ReviewPosting.cs`

This document owns the commands that make up CodeFlow's PR-review feature — project↔host linking,
PR listing and link resolution, the review run itself, PR-description drafting, and posting
findings and decisions back to the host — plus the pure reconciliation logic that gives a
re-review its memory. It does **not** own the GitHub/Azure DevOps clients these commands call into
(`src/CodeFlow.App/Providers/` — `06-providers.md`), nor the AI engine layer the review runs on
(`src/CodeFlow.App/Ai/` — `05-ai-engines.md`), nor the `review_runs` table schema
(`src/CodeFlow.App/Storage/` — `03-storage.md`), nor command parameters and return types
(`01-ipc-surface.md`).

This is the flagship feature, and until 2026-08-01 it was the least-verified code in the tree.
That day a live run against a throwaway PR on each host (recorded in `90-ambiguities.md`)
executed every publishing command below for real; the individual verification state is on each
command and rule. Two residues remain: Azure's `set_pr_thread_status` was unreachable (it needs a
re-review after a second push, and the throwaway Azure repo allowed exactly one), and the
link-path commands ran against Azure only (the GitHub throwaway repo had already been deleted
when that leg ran). The behaviour below is transcribed from source; where a rule says
`VERIFIED-LIVE`, the live run also confirmed it.

## Commands

Parameters and return types are `01-ipc-surface.md`'s `src/CodeFlow.App/Review/ReviewCommands.cs` table. One line each,
in file order:

- `auto_link_project` — derives a project's provider link from its local git remotes, linking it
  automatically when a token is already saved.
- `ado_list_projects` — lists an Azure DevOps organization's projects (async, no state).
- `ado_list_repos` — lists an Azure DevOps project's repositories (async, no state).
- `link_project_ado` — manually writes a project's Azure org/project/repo link columns.
- `unlink_project` — clears whichever of the two host links a project has.
- `open_repo_in_browser` — opens the project's reconstructed repo web URL in the OS browser.
- `open_external_url` — opens an arbitrary http(s) URL in the OS browser.
- `list_pull_requests` — lists a linked project's pull requests from its host.
- `resolve_pr_link` — resolves a pasted PR URL into a PR plus (and linking, if needed) the local
  repo it belongs to.
- `review_pr_from_link` — reviews a PR reached by link alone, no local clone, no project.
- `pr_link_pull_request` — re-reads the PR behind a link from its host.
- `pr_link_comment_threads` — reads a link's PR's existing comment threads.
- `pr_link_decision` — reads the signed-in user's existing decision on a link's PR.
- `act_on_pr_link` — approves/requests-changes/closes the PR behind a link.
- `post_pr_link_review_comment` — posts selected findings from a repo-less review onto its PR.
- `generate_pr_description` — drafts a PR title + body from a local branch diff.
- `create_pull_request` — creates a PR on whichever host a project is linked to.
- `list_pr_comment_threads` — reads a linked project's PR's existing comment threads.
- `review_pull_request` — the main entry point: reviews a project-linked PR against its local clone.
- `post_pr_review_comment` — posts human-selected findings from a saved review run onto its PR.
- `pr_review_decision` — reads the signed-in user's existing decision on a linked project's PR.
- `act_on_pull_request` — approves/requests-changes/closes a linked project's PR, filed to Activity.

## Block map

Verified against the source (`wc -l` confirms 1471 lines, 22 a registered command attributes). One
correction to the pre-built map handed to this document: the second
` attribute, at line 1122, sits directly above `fn
persist_review_run` (`1123`) — **not** above `post_pr_review_comment` (a registered command at
`1229`, `pub async fn` at `1230`), which carries no ` at all (six parameters plus `db` is
under Clippy's default threshold). Every other block boundary below was checked line-by-line
against the source and matched the pre-built map exactly.

**Block 1 — Project ↔ repository linking (21–287)**

| Line | Item |
|---|---|
| 21 | `LinkedRepo` (enum) |
| 28 | `linked_repo(project)` |
| 41 | `github_token(host)` |
| 47 | `GithubConnectionHost` (record) |
| 55 | `github_known_hosts(db)` |
| 76 | `build_mcp_config(mcps, workspace_id)` |
| 109 | `AutoLinkResult` (enum) |
| 130 | ⚡ `auto_link_project` |
| 188 | `pat_for_org(org)` |
| 193 | ⚡ `ado_list_projects` |
| 199 | ⚡ `ado_list_repos` |
| 205 | ⚡ `link_project_ado` |
| 219 | ⚡ `unlink_project` |
| 228 | `fn web_encode` |
| 236 | `repo_web_url(db, project_id)` |
| 264 | ⚡ `open_repo_in_browser` |
| 275 | ⚡ `open_external_url` |
| 283 | `load_project(db, project_id)` |

**Block 2 — PR listing and link resolution (290–501)**

| 290 | ⚡ `list_pull_requests` |
| 313 | `PrLinkResolution` (enum) |
| 340 | `fn same` / 344 `fn same_opt` |
| 354 | `fn find_project_for_link` |
| 391 | ⚡ `resolve_pr_link` |

**Block 3 — Review-from-link (506–827)**

| 506 | `fn link_credentials` |
| 521 | `async fn fetch_pr_and_diff` |
| 542 | `fn slugify` |
| 555 | `fn link_review_workspace` |
| 584 | `const NO_CLONE_CONTEXT` |
| 594–596 | ⚡ `review_pr_from_link` — ` at 594 |
| 665 | ⚡ `pr_link_pull_request` |
| 680 | ⚡ `pr_link_comment_threads` |
| 695 | ⚡ `pr_link_decision` |
| 714 | ⚡ `act_on_pr_link` |
| 753 | ⚡ `post_pr_link_review_comment` |

**Block 4 — PR description generation (831–928)**

| 831 | `pub struct PrDescriptionDraft` |
| 839 | `parse_pr_draft(raw)` |
| 863 | ⚡ `generate_pr_description` |
| 901 | ⚡ `create_pull_request` |

**Block 5 — Comment threads (932–949)**

| 932 | ⚡ `list_pr_comment_threads` |

**Block 6 — The core review pipeline (951–1200)**

| 951–952 | ⚡ `review_pull_request` |
| 1035 | `fn review_level_directive` — **not in this file**, `src/CodeFlow.App/Ai/AiOperations.cs`; not listed here |
| 1122–1123 | `fn persist_review_run` — ` at 1122 |

**Block 7 — Posting findings back, and decisions (1202–1471)**

| 1202 | `pub struct CommentLocation` |
| 1210 | `pub struct PostFindingItem` |
| 1229–1230 | ⚡ `post_pr_review_comment` (no `) |
| 1354 | `fn apply_post_outcome` |
| 1381–1382 | ⚡ `pr_review_decision` |
| 1396 | `pub struct PrActionOutcome` |
| 1413–1414 | ⚡ `act_on_pull_request` (to EOF, 1471) |

⚡ appears 22 times, matching `01-ipc-surface.md`'s count. `review_level_directive` listed above
lives in `src/CodeFlow.App/Ai/AiOperations.cs`, out of this file — it is mentioned only to flag that it is *not* one of this
file's items despite being read in the trace below.

## Finding parsing

`src/CodeFlow.App/Review/ReviewMemory.cs` parses a review's raw Markdown into `MemoryFinding` rows — the slim, comparable
projection that `review_runs.findings` stores. The header format it parses is `XLANG-001`'s
three-way contract (`src/CodeFlow.App/Ai/AiOperations.cs` producer, this the sidecar parser, `renderer/src/lib/parseAnalysis.ts`); this section
documents the parsing mechanics `XLANG-001` does not restate.

### Header matching (`parse_findings`, `src/CodeFlow.App/Review/ReviewMemory.cs`)

One `Regex` (`(?m)^###\s*(🚨|⚠️|ℹ️)\s*\[([^·\]]+)·([^\]]+)\]\s*([^·]+)·\s*(F-\d+)\s*$`) finds every
finding header line. `header.find_iter` separately collects each match's byte offset so the text
can be sliced into per-finding **blocks**: block *i* runs from header *i*'s start to header *i+1*'s
start (or the end of the text for the last one). Everything downstream (subtitle, location,
confidence) is extracted from within that finding's own block, not from the whole document — so a
`📍`/`🎯` field belonging to finding *N+1* can never bleed into finding *N*'s parse.

Per match: capture group 1 (emoji) maps to `severity` — `🚨`→`critical`, `⚠️`→`warning`, anything
else (only `ℹ️` in practice)→`info`; group 3 (trimmed) is `tipo`; group 4 (trimmed) is `categoria`;
group 5 (trimmed) is `id` — the literal `F-NNN` string the **model** wrote in its own output (see
`AMBIGUOUS-REVIEW-a` below for what this means for reconciliation).

**Subtitle** (`src/CodeFlow.App/Review/ReviewMemory.cs`): the first line of the block, after the header itself,
that is non-empty after trimming and does not start with `📍` or `💭` (the "why" field,
`src/CodeFlow.App/Ai/AiOperations.cs`/217`). If no such line exists, `subtitulo` is `""`.

**Location** (`loc_re = 📍\s*Ubicaci[oó]n:\s*([^\n]+)`, then `parse_location`,
`src/CodeFlow.App/Review/ReviewMemory.cs`): the captured value is trimmed, then every backtick, asterisk and
underscore is stripped (Markdown wrapping the model sometimes adds), then re-trimmed. The cleaned
string is split on the **last** `:` (`rsplit_once`): if that split exists, the left side is
non-empty after trimming, and the right side contains at least one ASCII digit, the result is
`(Some(file), Some(lines))`. Otherwise the whole cleaned string becomes `archivo` (unless empty, in
which case `None`) and `lineas` is `None`. A location like `src/app.ts:12-14` splits into
`("src/app.ts", "12-14")`; a location with no `:` or whose tail has no digit (e.g. a bare file path,
or `C:` as a Windows drive letter with no line number following) becomes a file-only location.

**Confidence** (`conf_re = 🎯\s*Confianza:\s*(\d+)`): first integer found in the block, or `None` if
the field is missing or unparsable.

Every field not derivable from the block gets a fixed default on first parse: `estado = "abierto"`,
`thread_id = None`, `introducido_en_iter = 0`, `resuelto_en_iter = None`, `motivo_descarte = None`,
`delta = None`. `introducido_en_iter = 0` here is a sentinel meaning "not yet assigned" —
`reconcile()` is what fills in a real iteration number (see below); a first-run finding gets it
force-set to `1` by `persist_review_run` before storage (`src/CodeFlow.App/Review/ReviewCommands.cs`).

### Identity (`finding_identity` / `identity`, `src/CodeFlow.App/Review/ReviewMemory.cs`)

`finding_identity(archivo, categoria)`: `file = archivo.unwrap_or_default().trim_start_matches('/').to_lowercase()`,
`cat = categoria.to_lowercase()`, result `"{file}|{cat}"`. This is a `pub fn` — it is the *same*
identity key used both by `reconcile()` (below) and, independently, by `post_pr_review_comment`'s
`index_of` (Publishing, below) to match a posted item back to its stored finding.

`identity(f: &MemoryFinding)` (private) wraps it: if the base key is exactly `"|"` (both `archivo`
and `categoria` empty — i.e. the model reported neither a location nor even a category), it falls
back to `f.subtitulo.to_lowercase()` instead. In every other case — including a finding with a
category but no file — the file+category key is used as-is.

## Reconciliation

`reconcile(prev, current, prev_iter, changed_files)` (`src/CodeFlow.App/Review/ReviewMemory.cs`) is the state
machine a re-review runs to merge a fresh parse against the previous run's stored findings. It is
called once, from `persist_review_run` (`src/CodeFlow.App/Review/ReviewCommands.cs`), only when `prior > 0` (i.e. this
PR already has at least one saved run) — the first-ever review for a PR skips it entirely (every
parsed finding just becomes `introducido_en_iter = 1`, `delta = None`).

Its own doc comment states it mirrors the reconciliation rules of the review runbook this engine was
ported from (`src/CodeFlow.App/Review/ReviewMemory.cs`) — one of four places carrying rules from it
(`13-cross-language-contracts.md`, "Where the review contract comes from"). That runbook is not in
this tree; where its reasoning is not independently recoverable from source comments, this section
records `AMBIGUOUS-REVIEW-*` rather than inventing a rationale.

`iter_actual = prev_iter + 1`. `next_id` starts at `max(max_id_num(prev), max_id_num(current)) + 1`
— the highest `F-NNN` correlative seen on **either** side, plus one, so a fresh id can never
collide with an id the model itself happened to also emit this run.

### Pass 1 — every current finding, against `prev`

For each `cur` in `current`, `key = identity(cur)`, `prev_match = prev.iter().find(|p| identity(p) == key)`
(first match only):

| Condition | Outcome | Effect on `cur`'s row |
|---|---|---|
| No `prev_match` | **new** | `id = "F-{next_id:03}"` (next_id++), `estado = "abierto"`, `introducido_en_iter = iter_actual`, `delta = Some("nuevo")`, `nuevos += 1` |
| `prev_match` found, `prev_match.estado == "resuelto"` | **reappeared → treated as brand-new** | Same as "new" above — a fresh id, fresh iter, `delta = "nuevo"`, `nuevos += 1`. The old finding's `thread_id` is **not** carried over (`cur.clone()` starts with `thread_id: None` from the fresh parse); a later post opens a brand-new thread rather than reopening the old one. |
| `prev_match` found, any other `estado` (`abierto`, `posteado`, `falso_positivo`, `ignorado`) | **persists** | `id = prev.id`, `estado = prev.estado`, `thread_id = prev.thread_id`, `introducido_en_iter = prev.introducido_en_iter` unless that was `0` (pre-tracking row), in which case `prev_iter.max(1)`, `motivo_descarte = prev.motivo_descarte`, `delta = Some("persiste")`. The matched `key` is recorded in `matched_prev`. `persisten += 1` **only if** the merged row `is_active()` (`abierto`/`posteado`) — a persisting `falso_positivo`/`ignorado` finding is not counted. |

### Pass 2 — every `prev` finding not already matched in Pass 1

A `prev` entry is skipped (not re-processed) if its `key` is in `matched_prev`, or if some row
already in `merged` shares its `id` (this second check is what lets a "reappeared" `prev` entry —
which was matched in Pass 1 but *not* added to `matched_prev`, since only the "persists" branch does
that — still get its own row re-emitted below, preserving its old `resuelto`/traceability record
alongside the brand-new reappeared row).

For every remaining `p`:

- **`p.is_active()`** (`abierto`/`posteado`):
  - `file_touched` = `true` unless `changed_files` is `Some(changed)` **and** `p.archivo` is
    `Some(file)`, in which case it is `file_in_changed(file, changed)` (below). In other words: a
    full review (`changed_files = None`) or a finding with no recorded location always counts as
    "re-analyzed"; an efficient re-review (`changed_files = Some(...)`) only re-analyzes files it
    actually diffed.
  - If `file_touched`: **resolved** — `estado = "resuelto"`, `resuelto_en_iter = Some(iter_actual)`,
    `delta = Some("resuelto")`, `resueltos += 1`.
  - Else (untouched file in an efficient re-review): **persists untouched** — `estado` unchanged,
    `delta = Some("persiste")`, `persisten += 1`.
- **Not active** (already `resuelto`, `falso_positivo`, or `ignorado`): carried forward verbatim,
  `delta = Some("persiste")`, no counter incremented — "traceability", per the source comment; a
  resolved/discarded finding is never deleted and never re-evaluated.

`file_in_changed(finding_file, changed)` (`src/CodeFlow.App/Review/ReviewMemory.cs`): normalizes both sides
(strip a leading `/`, lowercase) then matches if either is a suffix of the other (`c == a ||
c.ends_with(&a) || a.ends_with(&c)`) — tolerant of the review markdown's file path and git's own
path differing by a leading slash or by which one is more fully qualified.

Returns `(merged: IReadOnlyList<MemoryFinding>, ReviewDelta { iter_previa: prev_iter, iter_actual, nuevos,
persisten, resueltos })`.

### Traceability rendering

`resolved_history_section(findings)` (`src/CodeFlow.App/Review/ReviewMemory.cs`) — `None` if there is nothing
`resuelto` and nothing `falso_positivo`/`ignorado`. Otherwise renders, `VERBATIM` (Spanish):

`text


---

### 🕘 Historial de hallazgos resueltos (trazabilidad)

- `{categoria}` · {archivo|—} — introducido iter {introducido_en_iter} · resuelto iter {resuelto_en_iter|0}
`

— one bullet per `resuelto` finding — followed, if any discarded findings exist, by:

`text

### 🗂️ Hallazgos descartados

- `{categoria}` · {archivo|—} — {falso positivo|ignorado}{: {motivo_descarte} if non-empty}
`

`delta_banner(delta)` (`src/CodeFlow.App/Review/ReviewMemory.cs`), `VERBATIM` (Spanish):

`text
🔁 Re-revisión (iter {iter_previa} → {iter_actual}): {nuevos} nuevos · {persisten} persisten · {resueltos} resueltos

`
(a blank line follows, since the banner is meant to be prepended directly onto the review body).

### `MemoryFinding.is_active()` (`src/CodeFlow.App/Review/ReviewMemory.cs`)

`true` for `estado ∈ {"abierto", "posteado"}` — these count toward severity buckets / the Quality
Gate. `resuelto`, `falso_positivo` and `ignorado` are carried for traceability but excluded from the
active view; nothing in this codebase deletes a `MemoryFinding` row once created.

## The review run

`review_pull_request` (`src/CodeFlow.App/Review/ReviewCommands.cs`) is the main entry point — a project-backed review
against a local clone.

1. **Load & resolve.** `load_project(project_id)`; `linked_repo(&project)?` (errors `"This project
   isn't linked to a pull-request host yet"` if neither the GitHub nor the Azure link columns are
   set — GitHub wins if, contrary to the application-level convention, somehow both are).
2. **Config, in one DB lock.** `list_review_contexts`, `list_workspace_mcps`,
   `list_workspace_skills` (all scoped to `project.workspace_id`); `config` is either
   `load_ai_config_for(provider, model)` when the caller supplied a non-blank `agent_provider` +
   `agent_model` pair (an SDD/Harness agent driving this run on its own routing), or otherwise
   `load_ai_config(AiTask.Review)` (the ordinary per-task routing cascade) — see `05-ai-engines.md`
   `AI-046`/`AI-047`. `review_template = get_workspace_prompt(workspace_id, "review_standard")` —
   blank-means-default per `STORE-012`.
3. **PR lookup — a full list, not a direct get.** Dispatches to `list_pull_requests` or
   `list_pull_requests` (via `pat_for_org`/`github_token`), then `.find(|p| p.id == pr_id)`
   in the returned `Vec`, erroring `"Pull request not found"` if absent. Neither branch calls the
   provider's single-PR-by-number endpoint (`PROV-007`/`PROV-025` both have one). **Edge case**:
   GitHub's `list_pull_requests` caps at 100 results with no further pagination (`PROV-007`) — a PR
   older than the newest 100 by creation date is unreachable from this command even though
   `get_pull_request` could read it directly. Azure's own list endpoint sets no explicit
   page size (`PROV-025`), so this document cannot establish whether an equivalent cap applies
   there.
4. **Best-effort fetch.** `GitNetwork`(app.clone(), project.local_path.clone(), None)`,
   result discarded (`let _ =`) — offline or an auth hiccup does not block the review; the diff is
   built against whatever refs are already local.
5. **Head-ref resolution.** For `LinkedRepo.GitHub`: attempts a targeted fetch of
   `+refs/pull/{pr_id}/head:refs/remotes/origin/codeflow-pr-{pr_id}`; on success, `head_ref` is that
   local tracking ref (so the review reflects the PR's exact head — including a fork's head branch,
   which may not exist as a normal `origin/*` branch); on failure, falls back to `pr.source_branch`.
   For `LinkedRepo.Azure`: always `pr.source_branch` (Azure PRs are always same-remote branches, no
   fork model).
6. **`head_sha`.** `src/CodeFlow.App/Git/`::resolve_sha(project.local_path, head_ref)` — see `04-git.md`
   `GIT-030` for `resolve_branch_commit`'s exact resolution order (own ref verbatim if it starts
   with `refs/`; else `origin/{name}` → `refs/remotes/origin/{name}` → bare `{name}`, first that
   resolves wins). Defaults to `""` on error (`.unwrap_or_default()`) — a genuinely unresolvable ref
   is *not* surfaced here; it resurfaces as a hard error two steps later, at `get_branch_diff`
   (step 9), which uses `?`.
7. **"Nothing changed" short-circuit.** `prev_head = the store(project_id,
   pr_id)` — the **newest** run's `meta.head_sha` by `created_at` (not by `iter`), `None`/DB-error
   swallowed to `None`. If `prev_head == Some(head_sha)` and `head_sha` is non-empty: returns
   immediately, `Ok("🔁 Sin cambios desde la última revisión (mismo commit `{8-char-sha}`). No se
   volvió a analizar.")` (`VERBATIM`, Spanish). **No AI call, no `job_history` row, no `review_runs`
   row** — this path is a pure read plus one early return.
8. **`changed_files`.** Only computed when `prev_head` is `Some` and non-empty:
   `src/CodeFlow.App/Git/`::changed_files_between(project.local_path, prev, head_ref).ok()` — `None` on any
   error (silently degrading `reconcile()`'s later behaviour to "full review" semantics, i.e. any
   unmatched active finding is treated as resolved rather than auto-persisted).
9. **Skills sync**, best-effort, into `project.local_path` (not a workspace-level directory).
10. **Diff.** `get_branch_diff(project.local_path, pr.target_branch, head_ref)?` (propagates a
    resolution failure here, per step 6's note) → `render_diff_for_prompt`.
11. **Contexts.** Enabled review contexts, `(name, content)` pairs; if `agent_prompt` is present and
    non-blank, `("Agent", prompt)` is inserted at index 0 (the active agent's instructions frame the
    review first). Unlike `review_pr_from_link` (below), there is **no** "no local clone" warning
    context inserted here — the model has the real working tree.
12. **MCP config.** `build_mcp_config(mcps, workspace_id)` — writes (or overwrites)
    `{base_dir}/workspaces/{workspace_id}/mcp.json` from the workspace's enabled MCP servers;
    `None` if none are enabled.
13. **The AI call.** `AiRunRegistry`(app, Some(job_id), `src/CodeFlow.App/Ai/`(engine, binary,
    model, pr.title, pr.description, enabled_contexts, diff_text, config.tools,
    project.local_path, review_template, level, mcp_config_path))` — see `05-ai-engines.md`
    `AI-023` for the prompt/stdin composition, the level directive, and `stamp_footer`. `cwd` here
    is the real project clone.
14. **Cancellation.** If the result is `Err(e)` and `e` starts with ai_runs.CANCELLED_MARKER,
    it is returned as-is immediately — **no** `job_history` row, **no** `review_runs` row. A
    cancelled run leaves nothing behind.
15. **Success.** `text = persist_review_run(...)` (below); the store(job_id,
    project_id, "pr-review", "#{id} {title}", "done", Some(&text), None, {prId, prTitle, level})`,
    best-effort (`let _ =`) — a memory-write failure inside `persist_review_run` must never turn a
    good review into a reported failure, and a `job_history` write failure is likewise swallowed.
16. **Failure** (any other `Err`): `add_job_history(..., "error", None, Some(&e), ...)`,
    best-effort; the original error is still returned to the caller.

### `persist_review_run` (`src/CodeFlow.App/Review/ReviewCommands.cs`)

Called only from step 15 above. Best-effort end to end — its own doc comment states a memory-write
failure must never fail the review the user is waiting on.

1. `prior = count_review_runs(project.id, pr.id)` (`0` on error).
2. `parsed = parse_findings(&text)` — the raw model markdown, before any reconciliation.
3. If `prior > 0`: `prev = latest_review_findings(project.id, pr.id)` deserialized (empty `Vec` on
   any failure — missing row, malformed JSON), then `(findings, delta) = reconcile(prev, parsed,
   prior, changed_files)`, `delta = Some(d)`.
   Else (first review): `findings = parsed` with every `introducido_en_iter` force-set to `1`;
   `delta = None`.
4. `iter = prior + 1`.
5. **Text mutation order matters**: `text` is first **appended** with
   `resolved_history_section(&findings)` (if `Some`), *then* — only if `delta` is `Some` —
   **prepended** with `delta_banner(&delta)`. Final shape when both apply: `{banner}{original
   review body}{history section}`. This same, single `text` value becomes both the function's
   return value and what is written to `review_runs.review_md` — the user sees exactly what gets
   stored.
6. `ReviewMeta` is built with `iter`, `head_sha` (the SHA resolved in step 6 of the caller), and
   `timestamp = chrono.Local.now().to_rfc3339()` (local machine time, not UTC).
7. `meta`/`findings` serialized to JSON (`"{}"`/`"[]"` fallback on a serialize error — practically
   unreachable given the types involved, but the fallback avoids a panic).
8. the store(conn, job_id, project.id, workspace_id, pr.id, iter, level, meta_json,
   text, diff_text, findings_json)` — `id` reuses `job_id` (so the run and its `job_history` row
   share identity); insert is `ON CONFLICT(id) DO NOTHING` (`STORE-013`) — a retry with the same
   `job_id` is a silent no-op, not a second row. A write failure here is only `eprintln!`'d, never
   propagated.
9. Returns `text` regardless of whether the DB write succeeded.

## Review from a link

`review_pr_from_link` (`src/CodeFlow.App/Review/ReviewCommands.cs`) is the ad-hoc counterpart with no project and no local
clone — reachable from a pasted PR URL alone.

1. `(target, credential) = link_credentials(db, url)` (`src/CodeFlow.App/Review/ReviewCommands.cs`): parses `url` via
   `PrLink` against the known GitHub hosts, erroring `"That isn't a pull-request link
   CodeFlow can read"` if it doesn't match either grammar (`06-providers.md` `PROV-040`–`PROV-042`);
   then loads `github_token(host)` or `pat_for_org(org)` for whichever host it resolved to.
2. Config, in one DB lock, keyed off the **caller-supplied** `workspace_id` (there is no project to
   derive it from): `contexts`, `mcps`, `skills`, `config` (agent override or `load_ai_config(Review)`,
   identical cascade to the project-backed path), `review_template` (`"review_standard"`, same
   blank-means-default rule).
3. `(pr, diff_text) = fetch_pr_and_diff(target, credential)` (`src/CodeFlow.App/Review/ReviewCommands.cs`): GitHub →
   `get_pull_request` + `pull_request_diff`; Azure → `get_pull_request` (recovers the *canonical*
   project/repo names, since a link may carry GUIDs) + `pull_request_diff` against those names.
4. `cwd = link_review_workspace(target, pr, diff_text)` (`src/CodeFlow.App/Review/ReviewCommands.cs`): directory
   `{base_dir}/pr-link-reviews/{slug}`, `slug = slugify("github-{host}-{owner}-{repo}-{number}")` or
   `slugify("azure-{org}-{project}-{repo}-{number}")` — `slugify` maps every character that is not
   `[A-Za-z0-9_-]` to `-`. Writes `PULL_REQUEST.md` (title, author, source/target branch, URL,
   description — `"(sin descripción)"`, Spanish, when the PR's own description is blank) and
   `changes.diff` (the raw diff text). **Reused, overwritten** across repeated reviews of the same
   PR link — same `slug`, so no accumulation of temp directories for re-runs of one PR — but nothing
   in this file ever deletes a `pr-link-reviews/{slug}` directory for a PR link that is never
   revisited; this document cannot establish whether cleanup happens elsewhere.
5. Skills sync, best-effort, into `cwd` (the ad-hoc workspace, not a real project directory).
6. **Contexts, with the no-clone warning inserted first.** Enabled contexts are collected exactly
   as in the project-backed path, but then `("Modo de revisión", NO_CLONE_CONTEXT)` is inserted at
   index 0, and — only if `agent_prompt` is present — `("Agent", prompt)` is inserted at index 0
   *after* that, which pushes the warning down one slot. Net order: `[Agent?, "Modo de revisión",
   ...enabled contexts]` — the agent's own instructions, when present, frame the review ahead of
   the no-clone warning; otherwise the warning comes first.
   `NO_CLONE_CONTEXT` (`src/CodeFlow.App/Review/ReviewCommands.cs`), `VERBATIM` (Spanish):
   > *"Esta revisión corre SIN un clon local del repositorio. Por stdin recibes el diff completo del
   > pull request, y el directorio de trabajo solo contiene `PULL_REQUEST.md` y `changes.diff`. No
   > intentes explorar el árbol del repositorio ni abrir archivos que no estén ahí. Basa la revisión
   > en el diff: cuando un hallazgo dependa de código que no ves (una función llamada pero no
   > incluida, un contrato definido en otro archivo), decláralo explícitamente y baja la confianza en
   > consecuencia, o clasifícalo como Security Hotspot en lugar de afirmar un bug que no puedes
   > demostrar."*
   **The warning is now enforced as well as stated.** A link review is given no tools at any level
   (`REVIEW-039`) and, at `ultra`, a level directive that does not ask it to read the surrounding
   code (`AI-022`). Previously the working directory held two files while the tool grant and the
   `ultra` directive both said otherwise — three instructions reaching the model down two channels
   of one invocation, only one of which could be true.
7. `mcp_config_path = build_mcp_config(mcps, workspace_id)`.
8. `AiRunRegistry`(app, Some(job_id), `src/CodeFlow.App/Ai/`(..., cwd, review_template, level,
   mcp_config_path))` — `cwd` here is the ad-hoc `pr-link-reviews` directory, never the project
   clone (there isn't one).
9. The result — success or failure, including a cancellation — is returned **directly**. There is
   no `persist_review_run` call and no `add_job_history` call anywhere in this function: per its own
   doc comment, "a run with no project has no project to file itself under," and a run with no saved
   `review_runs` row has nothing to reconcile a later re-review against. Every invocation of
   `review_pr_from_link` for the same link re-runs the full analysis from scratch — no delta banner,
   no `F-NNN` reconciliation, no thread reuse across calls.
10. There is also **no** head-SHA "nothing changed" short-circuit here (unlike step 7 of
    `review_pull_request`) — this path has no prior run's `head_sha` to compare against, so every
    call pays the full cost of the AI run regardless of whether the PR actually changed since the
    last time it was reviewed by link.

## Publishing

Every command in this section performs (or, for the two `_decision` reads, adjoins) a write to a VCS
provider's API. As of the 2026-08-01 live run (`90-ambiguities.md`), these write paths — comment
posting, thread replies, review-event submission, reviewer votes, PR close/abandon — have been
executed against real Azure DevOps and GitHub APIs; each rule carries its own marker with the
scope of what was observed. The read-only commands (`pr_link_pull_request`,
`pr_link_comment_threads`, `pr_link_decision`, `list_pr_comment_threads`, `pr_review_decision`)
run in normal use and were never marked.

### `post_pr_review_comment` (`src/CodeFlow.App/Review/ReviewCommands.cs`) — `VERIFIED-LIVE` (2026-08-01: three-stage reconciliation observed on both hosts — new anchored threads, replies into saved threads, and on GitHub the resolve follow-up + thread close)

Posts a **human-selected subset** of one saved run's findings to its PR, reconciling against
whatever was already posted so a finding keeps exactly one thread for the PR's whole life.

**Loading the run.** `(findings, iter)` are read once from the store(run_id)`:
`(deserialize(r.findings) or empty, r.iter)` if the run exists, else `(empty, 1)`. `r.meta` — which
carries the `head_sha` this run was analyzed against (`ReviewMeta.head_sha`) — is read from the row
but **never destructured or consulted anywhere in this function**. See `BUG-REVIEW-a`.

**Selection & identity matching.** `items: IReadOnlyList<PostFindingItem>` — one entry per finding the user
picked in the UI:

`csharp
// camelCase over IPC — see src/CodeFlow.App/Providers/PostItem.cs
public sealed record CommentLocation(string File, long StartLine, long EndLine);

public sealed record PostFindingItem(
    string? File,
    string Category,
    string Content,               // full comment markdown, used when opening a new thread
    CommentLocation? Location);
`

`index_of(findings, item)` looks up `finding_identity(item.file, item.category)` against
`finding_identity(f.archivo, f.categoria)` for every stored finding, returning the **first**
`position()` match — the same identity function `reconcile()` uses (`src/CodeFlow.App/Review/ReviewMemory.cs`), so a
posted item is matched to its stored finding by **file + category**, never by `MemoryFinding.id`.
Because that identity is not guaranteed unique (`BUG-REVIEW-b`), two distinct findings sharing a
file+category would both resolve to the same `idx` here too.

`idx` feeds two derived values per item: `thread = idx.and_then(|k| findings[k].thread_id)` and
`resolved = idx.map(|k| findings[k].estado == "resuelto").unwrap_or(false)` — both read from the
in-memory `findings` snapshot as it stands *at that point in the loop* (mutated in place by
`apply_post_outcome` after each item, so two selected items that collide on identity will see the
first item's freshly-written `thread_id` when the second one is processed, and post a reply rather
than opening a second new thread for the same run).

**Per-provider posting**, one loop iteration per item:

- **Azure** (`org, ado_project, repo_id`, `pat_for_org`): no thread (`thread == None`) → open one,
  anchored (`post_pr_comment_anchored`, `PROV-030`) if `item.location` is `Some`, else a
  general thread comment (`post_pr_comment`, `PROV-031`). Existing thread (`Some(tid)`) → reply
  (`reply_pr_thread`, `PROV-032`) with, `VERBATIM` (Spanish):
  - resolved: `"✔️ _Resuelto en la iteración {iter} — {today}. Marcado como fixed._"`
  - still present: `"➡️ _Sigue presente en la iteración {iter} — {today}._"`

  and, only if the reply succeeded **and** `resolved`, also `set_pr_thread_status`(tid, 2, …)`
  (`2` = fixed, `PROV-033`) — a failed status-set is not itself collected as a failure (only the
  reply's own `Result` is checked for that).
- **GitHub** (`host, owner, repo`, `github_token`): `head_sha` is fetched fresh via
  `head_sha_for` (`PROV-010`), but **only if** at least one item in the whole batch carries
  a `location` — an optimization that skips the network call entirely for an all-general-comment
  post. No thread (`comment == None`) → open one, anchored (`post_pr_comment_anchored`,
  `PROV-011`, using that freshly-fetched `head_sha` — not the run's own recorded `head_sha`; see
  `BUG-REVIEW-a`) if `item.location` and the fetch both succeeded, else general
  (`post_pr_comment`, `PROV-012`). Existing comment (`Some(cid)`) → reply
  (`reply_pr_review_comment`, `PROV-013`) with, `VERBATIM` (Spanish, no italics, no "Marcado
  como fixed" suffix — a real wording divergence from Azure's reply text above):
  - resolved: `"✔️ Resuelto en la iteración {iter} — {today}."`
  - still present: `"➡️ Sigue presente en la iteración {iter} — {today}."`

  and, only if the reply succeeded **and** `resolved`, also
  `resolve_review_thread_for_comment` (`PROV-014`, the GraphQL path) — again not itself
  checked as a posting failure.

`{today}` in both branches is chrono.Local.now().format("%Y-%m-%d")` — the posting machine's
local date, not UTC and not the repo's own timezone.

**`apply_post_outcome`** (`src/CodeFlow.App/Review/ReviewCommands.cs`) — the shared bookkeeping both provider loops call
after each item: `Ok(Some(new_thread_id))` (a brand-new thread/comment was opened) records
`findings[k].thread_id = Some(new_thread_id)` and, **only if** the stored finding's `estado` was
still `"abierto"`, flips it to `"posteado"` (a finding that was already `resuelto` when first
posted — never posted before, but resolved by the time someone got around to posting it — keeps
`estado = "resuelto"` after this, correctly). `null` (a reply on an existing thread) touches
nothing. `Err(e)` is collected into `failures` as `"#{item_index+1}: {e}"`. If `idx` was `None` (no
stored finding matched this item's identity), the whole match arm is skipped by the `if let Some(k)
= idx` guard — the post itself still went out to the provider, but the app records no thread id for
it, so a later re-post of the same (still-unmatched) item would open yet another new thread rather
than replying.

**Optional summary comment, posted first**: if `post_summary` and `summary` is `Some`, one
general-comment post (`post_pr_comment` / `post_pr_comment`), its own failure appended as
`"summary: {e}"`.

It used to go last, after every finding, which put it at the bottom of the pull request's timeline —
under the very comments it exists to introduce, read as a postscript to its own conclusions. Order
is the only thing that changed; a failed summary is still reported under the same label and still
does not stop the findings.

Posting it first is only sound if the batch is known to be publishable, so `BUG-REVIEW-a`'s refusal
moves ahead of both, as `IPullRequestHost.EnsureUnchangedAsync`. GitHub implements it with the head
lookup it already had — one request per publish, not per item — and refuses with `STALE_REVIEW:`
before anything is written; Azure does nothing, for the reason `BUG-REVIEW-a` records, and the gap
stays exactly as open as it was. `PublishFindingsAsync` keeps its own copy of the check: the early
one is an ordering convenience, not the guarantee. The repo-less path (`REVIEW-013`) posts the
summary first too, and has no check to run ahead of it — a link review has no saved run and so no
analysed head to compare.

**Every item is attempted regardless of earlier failures** — the loop never short-circuits. Once
the whole batch is done, `findings` (with every `thread_id`/`estado` change applied in place) is
serialized back with the store(run_id, json)` — the **only** write path that
mutates an existing `review_runs` row's `findings` column after insert (`STORE-013`); `review_md`,
`diff` and `meta` are immutable once written by `add_review_run`. If `failures` is non-empty, the
command's own return is `Err("{n} comment(s) failed to post — {joined "; "}")` — the caller has no
way to tell, from the error alone, which items succeeded and which failed; the DB write already
reflects exactly the successes (partial success is not rolled back).

### `post_pr_link_review_comment` (`src/CodeFlow.App/Review/ReviewCommands.cs`) — `VERIFIED-LIVE` on Azure (2026-08-01 — fresh unanchored thread landed on an abandoned PR through the link path; the GitHub leg never ran, the throwaway repo was already deleted)

The repo-less counterpart. There is **no saved run** to reconcile against — a link review has no
`review_runs` row — so **every finding opens a brand-new thread every time**, regardless of whether
the same finding was posted on a previous call for the same link. GitHub: fetches `head_sha_for`
once (same "only if any item has a location" optimization) and posts each item anchored or general;
Azure: posts each item directly (Azure's `post_thread` resolves its own latest iteration internally,
`PROV-027`/`PROV-030`). Same "attempt every item, collect failures, one aggregate error" shape as
`post_pr_review_comment`, same `"{n} comment(s) failed to post — {joined}"` error format. No
bookkeeping write of any kind follows (there is nothing to write back to).

### `act_on_pr_link` (`src/CodeFlow.App/Review/ReviewCommands.cs`) — executed live on Azure (2026-08-01: the dispatch ran for real and the host refused the vote with 400 `TF401181` because the PR was abandoned — the error mapping worked; a 2xx through this command remains unexercised)

Approve/request-changes/close the PR behind a link. GitHub: `"approve"` →
`submit_pr_review`(APPROVE, comment)` (`PROV-016`); `"request_changes"` →
`submit_pr_review(REQUEST_CHANGES, text)` where `text` substitutes `"Cambios solicitados desde
CodeFlow."` (Spanish, `VERBATIM`) when `comment` is blank (GitHub itself requires a non-empty body
for this event); `"close"` → `close_pull_request` (`PROV-017`). Azure: `"approve"` →
`set_reviewer_vote(+10)`; `"request_changes"` → `set_reviewer_vote(-10)` (`PROV-034`); `"close"` →
`abandon_pull_request` (`PROV-036`). Any other `action` string errors `"unknown PR action: {other}"`.
After the write, the PR is re-read from the host (`get_pull_request`) and returned, so the caller
sees the state the action actually produced rather than an optimistic guess. **No Activity row is
written** here — the doc comment states this table belongs to a project, and a link review has
none; the caller is expected to file the decision in its own in-memory Activity instead.

### `create_pull_request` (`src/CodeFlow.App/Review/ReviewCommands.cs`) — `VERIFIED-LIVE` (2026-08-01: created one real PR on each host from the app; both came back mapped through the provider's own read shape)

Dispatches to `create_pull_request` (`PROV-026`) or `create_pull_request` (`PROV-009`)
per `linked_repo`. Neither underlying provider rule in `06-providers.md` itself carries an
`UNVERIFIED` marker, and since the 2026-08-01 live run this command has been executed for real
against both hosts — GitHub PR and Azure PR each created from the app, with the response consumed
through the normal mapping.

### `act_on_pull_request` (`src/CodeFlow.App/Review/ReviewCommands.cs`) — `VERIFIED-LIVE` (2026-08-01: on GitHub, approve returned the live 422 classified `SELF_APPROVAL: ` and close succeeded; on Azure, approve set the reviewer vote and close abandoned the PR; the Activity row was filed and the re-read PR state came back as the host reported it)

The project-linked twin of `act_on_pr_link`, same three-action dispatch and same substituted
Spanish default comment for a blank `request_changes` body. Differs in two ways: it re-reads the PR
from the host afterward exactly as the link version does, **and** it files an Activity row —
the store(job_id, project_id, "pr-action", "#{id} {title}", "done", Some(pr.url),
None, {prId, prTitle, action})`, generating a fresh uuid.`Guid.NewGuid()`()` for `job_id` — this write
is *not* best-effort (`?`, not `let _ =`); a DB failure here fails the whole command even though the
provider-side action already went through. Returns `PrActionOutcome { pr, activity }`.

### `pr_review_decision` / `pr_link_decision` (`src/CodeFlow.App/Review/ReviewCommands.cs`, `695–706`) — not `UNVERIFIED`

Pure reads: `viewer_decision` (`PROV-035`) / `viewer_decision` (`PROV-015`), dispatched
by `linked_repo` or by the parsed link target. No write, so not subject to §2.9.

## Rules

### REVIEW-001 Provider dispatch prefers GitHub over Azure DevOps
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: `linked_repo(project)` returns `LinkedRepo.GitHub` if `project.github_owner` **and**
`project.github_repo` are both set (regardless of whether the Azure columns are also set), else
`LinkedRepo.Azure` if all three Azure columns are set, else errors `"This project isn't linked to a
pull-request host yet"`.
**Inputs / outputs**: `&Project` → `LinkedRepo`.
**Edge cases**: a project with both hosts' columns populated (possible per `STORE-011` — nothing
prevents it) always dispatches to GitHub; the Azure link is silently ignored by every command that
calls `linked_repo`.
**Frontend dependency**: every dispatching command in this document; see `01-ipc-surface.md`.
**Markers**: none.

### REVIEW-002 `auto_link_project`: remote scan order and needs-token deferral
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: no-ops (`Linked`) if the project is already linked. Otherwise lists every git remote
of the local repo, orders `origin` first then the rest in listing order, and for each tries GitHub
detection (against `github_known_hosts`) then Azure detection. The **first** remote that both
resolves to a known provider **and** already has a saved token/PAT links the project immediately
(DB write) and returns `Linked`. A remote that resolves to a provider but has no saved credential is
remembered (first such case only) as a `NeedsToken { provider, identifier }` candidate but does not
stop the scan — a later remote that *does* have a credential still wins. If nothing in the whole
scan resolves to a provider at all, returns `NotDetected`.
**Inputs / outputs**: `project_id: string` → `AutoLinkResult` (`Linked{project}` |
`NeedsToken{provider,identifier}` | `NotDetected`).
**Edge cases**: a project whose local folder moved/was deleted has `list_remotes` fail — propagated
as a hard error (`?`), unlike `find_project_for_link` (`REVIEW-007`), which tolerates that per-project
and just skips.
**Frontend dependency**: `autoLinkProject`, see `01-ipc-surface.md`.
**Markers**: none.

### REVIEW-003 `github_known_hosts`: the Enterprise allowlist
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: always includes `github.com`; adds every host from the `github_connections` setting
(a JSON list, one row per connected Enterprise host) that doesn't already case-insensitively match
an entry already in the list. A malformed `github_connections` value is tolerated —
`from_str` failing simply skips adding anything beyond the default.
**Inputs / outputs**: `&State<Db>` → `IReadOnlyList, string>`.
**Edge cases**: a real GitHub Enterprise remote whose host was never connected in Settings is
indistinguishable, at detection time, from any other unrelated self-hosted git server.
**Frontend dependency**: none directly — feeds `auto_link_project`, `repo_web_url`, `resolve_pr_link`,
`link_credentials`.
**Markers**: none.

### REVIEW-004 `build_mcp_config`: per-review MCP JSON file
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: filters a workspace's MCP servers to `enabled`; returns `None` (no `--mcp-config`
flag) if none are enabled. Otherwise writes `{base_dir}/workspaces/{workspace_id}/mcp.json` — each
enabled server's `args` split on whitespace, `env` parsed as `KEY=value` lines (first `=` only,
both sides trimmed) — and returns the written path. Overwritten on every call; not a tempfile, kept
under the workspace's own CodeFlow folder for inspectability.
**Inputs / outputs**: `IReadOnlyList<WorkspaceMcp>, workspace_id: string` → `string?`.
**Edge cases**: an `env` line with no `=` is silently dropped (not an error).
**Frontend dependency**: none directly — feeds both `review_pull_request` and `review_pr_from_link`.
**Markers**: none.

### REVIEW-005 Repository web URL reconstruction and external-link opening
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: `repo_web_url` re-derives the repo's home page from its **live git remote** (not the
stored link columns, which may hold an Azure GUID or be briefly stale for a repo linked this
session) — GitHub: `https://{host}/{owner}/{repo}`; Azure:
`https://dev.azure.com/{org%20}/{project%20}/_git/{repo%20}` (`web_encode` replaces spaces with
`%20` only — no other character escaping). `open_repo_in_browser` opens that URL or errors `"Couldn't
determine this repository's web address from its remote"`. `open_external_url` opens any caller-given
URL but only if it starts with `http://` or `https://`, else errors `"only http(s) links can be
opened"` — a guard against a hostile/malformed string from a CLI's own output launching an arbitrary
local handler.
**Inputs / outputs**: `project_id: string` (former) / `url: string` (latter) → `void`.
**Edge cases**: `web_encode` does not percent-encode anything beyond a literal space — an org/project/
repo name containing another URL-unsafe character is not handled here (contrast `06-providers.md`'s
`encode_segment`, which this function does not use).
**Frontend dependency**: `openRepoInBrowser`, `openExternalUrl` — see `01-ipc-surface.md`.
**Markers**: none.

### REVIEW-006 `list_pull_requests` dispatch
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: loads the project, dispatches via `linked_repo` to `list_pull_requests`
(`PROV-025`) or `list_pull_requests` (`PROV-007`), after resolving the org's PAT or the
host's token.
**Inputs / outputs**: `project_id: string` → `IReadOnlyList, string>`.
**Edge cases**: inherits both providers' pagination limits (`PROV-007`'s 100-result GitHub cap;
Azure's undocumented default).
**Frontend dependency**: `listPullRequests`.
**Markers**: none.

### REVIEW-007 `resolve_pr_link` and `find_project_for_link`: pasted-URL resolution
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: parses `url` via `PrLink` (`06-providers.md` `PROV-042`); if unrecognized,
returns `Unrecognized` before any network call. If recognized but no credential is saved for its
host/org, returns `NeedsToken`. Otherwise reads the PR from the host, then `find_project_for_link`
looks for a local project: **pass 1** — a project already linked to exactly this repo (case-
insensitive comparison, `same`/`same_opt`), no writes. **Pass 2** — the first project (in whatever
order `list_all_projects` returns) whose *own git remote* detects to this repo; that project is
unconditionally `unlink_project`'d first (clearing whatever it was linked to, since `linked_repo`
prefers GitHub and a stale opposite-host pair would misroute later commands), then linked to the
newly-resolved host, then re-read from the DB so the caller gets the post-link row. A project whose
`local_path` no longer resolves (`list_remotes` fails) is skipped, not fatal, in pass 2 — this is the
error-tolerance `auto_link_project` (`REVIEW-002`) does *not* have. If pass 2 finds nothing either,
returns `NoLocalRepo { provider, repo_label, clone_url, pr }` — the PR itself is still returned so a
preview can be shown even with nothing local to attach it to.
**Inputs / outputs**: `url: string` → `PrLinkResolution`.
**Edge cases**: for Azure, pass-1's `already_linked` predicate additionally requires the candidate
project have **no** GitHub columns set — because `linked_repo` prefers GitHub, a project carrying
both would never dispatch to Azure regardless of what pass 1 found, so pass 1 deliberately defers to
pass 2 (which repairs the columns) in that case.
**Frontend dependency**: `resolvePrLink`.
**Markers**: none.

### REVIEW-008 `link_credentials` and `fetch_pr_and_diff`: the repo-less read primitives
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: `link_credentials` re-parses the link (erroring `"That isn't a pull-request link
CodeFlow can read"` if it doesn't match) and resolves the one credential its host/org needs.
`fetch_pr_and_diff` reads the PR and its unified diff purely from the host's API — GitHub via
`get_pull_request` + `pull_request_diff` (`PROV-007`/`PROV-008`); Azure via `get_pull_request`
(recovering canonical project/repo names from GUIDs, if the link carried any) +
`pull_request_diff` against those names (`PROV-025`/`PROV-028`).
**Inputs / outputs**: `&State<Db>, url: string` → `(PrLinkTarget` /
`(&PrLinkTarget, string)` → `(PullRequestSummary`.
**Edge cases**: none beyond what the underlying provider calls already carry.
**Frontend dependency**: shared by `review_pr_from_link`, `pr_link_pull_request`,
`pr_link_comment_threads`, `pr_link_decision`, `act_on_pr_link`, `post_pr_link_review_comment`.
**Markers**: none.

### REVIEW-009 `link_review_workspace`: the ad-hoc directory for a repo-less review
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: see "Review from a link" step 4 above for the full layout/content.
**Inputs / outputs**: `&PrLinkTarget, &PullRequestSummary, diff: string` → `string`
(the directory path).
**Edge cases**: never deleted by this file; reused (overwritten) only when the same PR link is
reviewed again.
**Frontend dependency**: none directly — feeds `review_pr_from_link`'s `cwd`.
**Markers**: none.

### REVIEW-010 `review_pr_from_link` end to end
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: see "Review from a link" above for the full ten-step trace.
**Inputs / outputs**: `url, job_id, level, workspace_id: string, agent_provider/agent_model/
agent_prompt: string?` → `string` (the review markdown, unaugmented — no
delta banner, no history section, since there is nothing to reconcile against).
**Edge cases**: no `head_sha`/"nothing changed" short-circuit; no `job_history`/`review_runs`
persistence at all, success or failure.
**Frontend dependency**: `reviewPrFromLink`.
**Markers**: none.

### REVIEW-011 Repo-less reads: `pr_link_pull_request`, `pr_link_comment_threads`, `pr_link_decision`
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: each calls `link_credentials` then dispatches straight to the matching provider read
— `get_pull_request`/`viewer_decision`/`list_pr_comment_threads` — with no further logic of its own.
**Inputs / outputs**: `url: string` → the provider's own return type.
**Edge cases**: none beyond `06-providers.md`'s own rules for these endpoints.
**Frontend dependency**: `prLinkPullRequest`, `prLinkCommentThreads`, `prLinkDecision`.
**Markers**: none.

### REVIEW-012 `act_on_pr_link`
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: see Publishing above.
**Inputs / outputs**: `url, action, body: string?` → `PullRequestSummary`.
**Edge cases**: an unrecognized `action` string errors before any network call.
**Frontend dependency**: `actOnPrLink`.
**Markers**: executed live on Azure only, error path observed (2026-08-01 — see the command entry).

### REVIEW-013 `post_pr_link_review_comment`
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: see Publishing above — always opens a new thread per finding, no reconciliation, every
item attempted regardless of prior failures.
**Inputs / outputs**: `url, items: IReadOnlyList<PostFindingItem>, post_summary: bool, summary: string?`
→ `void`.
**Edge cases**: a batch with zero `location`-carrying items skips the GitHub `head_sha_for` fetch
entirely.
**Frontend dependency**: `postPrLinkReviewComment`.
**Markers**: `VERIFIED-LIVE` (2026-08-01 live run — see `90-ambiguities.md`), Azure leg only.

### REVIEW-014 `parse_pr_draft`: title/body split
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: scans the model's raw output line by line; the **first** line (after overall
trimming) whose trimmed-start has the prefix `TITLE:` becomes `title` (everything after the prefix,
trimmed) and is excluded from the body; every other line — including any line before the first
`TITLE:` match, and any line after it that also starts with `TITLE:` — is appended to `body_lines`
verbatim. If no `TITLE:` line is ever found, `title = ""` and the **entire** trimmed input becomes
`body` — the caller gets a PR description with no title rather than an error.
**Inputs / outputs**: `raw: string` → `PrDescriptionDraft { title: string, body: string }` (body is
`body_lines.join("\n")`, then trimmed).
**Edge cases**: a model that writes `TITLE:` on a line by itself with the actual title on the next
line produces an empty `title` and that next line folded into `body` — the parser requires the title
text on the *same* line as the marker.
**Frontend dependency**: `generatePrDescription`'s consumer — see `01-ipc-surface.md`.
**Markers**: none.

### REVIEW-015 `generate_pr_description` command
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: loads the project; `config = load_ai_config(AiTask.PrDescription)`; `template =
get_workspace_prompt(workspace_id, "pr_description")` (`STORE-012`); diffs the two local branches
(`src/CodeFlow.App/Git/`::get_branch_diff` + `render_diff_for_prompt`, `04-git.md` `GIT-030`); calls
`src/CodeFlow.App/Ai/` (`05-ai-engines.md` `AI-020`) inside `AiRunRegistry`; parses the
result with `parse_pr_draft` (`REVIEW-014`).
**Inputs / outputs**: `project_id, source_branch, target_branch, run_id: string?` →
`PrDescriptionDraft`.
**Edge cases**: works with no push to the remote required — the diff is computed purely from local
git data.
**Frontend dependency**: `generatePrDescription`.
**Markers**: none.

### REVIEW-016 `create_pull_request`
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: see Publishing above.
**Inputs / outputs**: `project_id, title, description, source_branch, target_branch, draft: bool` →
`PullRequestSummary`.
**Edge cases**: none beyond the underlying provider calls (`PROV-009`/`PROV-026`).
**Frontend dependency**: `createPullRequest`.
**Markers**: `VERIFIED-LIVE` (2026-08-01 live run — see `90-ambiguities.md`).

### REVIEW-017 `list_pr_comment_threads`
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: loads the project, dispatches via `linked_repo` to `list_pr_comment_threads`
(`PROV-037`) or `list_pr_comment_threads` (`PROV-018`).
**Inputs / outputs**: `project_id, pr_id: long` → `IReadOnlyList, string>`.
**Edge cases**: inherits `PROV-018`'s `AMBIGUOUS-PROV-a` (GitHub reply-ordering assumption).
**Frontend dependency**: `listPrCommentThreads`.
**Markers**: none.

### REVIEW-018 `review_pull_request`: setup and PR lookup
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: see "The review run" steps 1–3 above.
**Inputs / outputs**: `project_id, pr_id: long, job_id, level: string, agent_provider/agent_model/
agent_prompt: string?` → (continues into `REVIEW-019`–`REVIEW-023`).
**Edge cases**: `"Pull request not found"` if `pr_id` isn't in the (possibly capped) list.
**Frontend dependency**: `reviewPullRequest`.
**Markers**: none.

### REVIEW-019 `review_pull_request`: fetch and head-ref resolution
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: see "The review run" steps 4–5 above.

**The pre-review fetch asks only for the refs the review reads.** It was a bare `git fetch origin` —
every branch and tag the remote has, in order to diff two of them — and it is the slowest step in
the run on any repository with history. Now it is a single `git fetch origin <refspec>…` carrying
the target branch, plus the source branch on Azure DevOps; GitHub's head still arrives through the
separate targeted `refs/pull/{n}/head` fetch, because a fork's branch does not exist on `origin` and
cannot be named in a refspec. Both stay best-effort: a review that can still run against the refs
already on disk is not blocked by a failed fetch.

This matters beyond speed. `GitNetwork` runs `git` to completion and its cancellation token
deliberately never aborts the process (`AMBIGUOUS-GIT-b`), and the ten-minute deadline on the model
(`AI-013`) covers the subprocess only — so an unbounded fetch is time no part of the system can
interrupt. Narrowing what is asked for reaches the same place without reopening that decision; if it
ever needs bounding as well, that is its own change, with the `git:done` "cancelled" shape
`AMBIGUOUS-GIT-b` says no frontend listener expects.

**Inputs / outputs**: n/a (internal state carried into the next step).
**Edge cases**: a GitHub PR from a fork resolves via the targeted `refs/pull/{n}/head` fetch, not
via any `origin/*` branch name. A branch that the narrowed fetch could not reach falls back to
whatever `GIT-030`'s candidate list finds on disk, and its "try fetching this repository first"
error if nothing does.
**Frontend dependency**: `reviewPullRequest`.
**Markers**: none.

### REVIEW-020 `review_pull_request`: head-SHA compare and the no-op re-review
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: see "The review run" steps 6–8 above — the `"🔁 Sin cambios..."` short-circuit and the
`changed_files` computation.
**Inputs / outputs**: n/a.
**Edge cases**: `resolve_sha` failing degrades to `head_sha = ""`, which can never equal a non-empty
`prev_head`, so the short-circuit simply doesn't fire (the review proceeds and fails later, at the
diff step, if the ref is genuinely unresolvable).
**Frontend dependency**: `reviewPullRequest`.
**Markers**: none.

### REVIEW-021 `review_pull_request`: diff, contexts, MCP config, the AI call, cancellation
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: see "The review run" steps 9–14 above; the AI call itself is `05-ai-engines.md`
`AI-023`.
**Inputs / outputs**: n/a.
**Edge cases**: a cancelled run (`CANCELLED_MARKER` prefix) returns immediately with no persistence
of any kind.
**Frontend dependency**: `reviewPullRequest`, `ai:output` (via `AiRunRegistry`).
**Markers**: none.

### REVIEW-022 `review_pull_request`: success/failure branches and `job_history`
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: see "The review run" steps 15–16 above.
**Inputs / outputs**: n/a.
**Edge cases**: both the memory write (`persist_review_run`) and the `job_history` write are
best-effort — neither failure changes what the caller receives.
**Frontend dependency**: `reviewPullRequest`, job-history list (`01-ipc-surface.md`).
**Markers**: none.

### REVIEW-023 `persist_review_run`
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: see "The review run" → "`persist_review_run`" above for the full nine-step trace.
**Inputs / outputs**: `(conn, job_id, project, workspace_id, pr, level, engine_label, model,
diff_text, head_sha, changed_files: IReadOnlyList<string>?, text: string)` → `string` (the augmented
text; infallible — DB errors are logged, not returned).
**Edge cases**: text mutation order — history section appended before the delta banner is
prepended — is load-bearing for the final rendered shape.
**Frontend dependency**: `reviewPullRequest`'s return value.
**Markers**: none.

### REVIEW-024 `parse_findings`: header/block segmentation and field extraction
**Implementation**: `src/CodeFlow.App/Review/ReviewMemory.cs`
**Behaviour**: see "Finding parsing" above.
**Inputs / outputs**: `review_md: string` → `IReadOnlyList<MemoryFinding>`.
**Edge cases**: a finding block with no `📍`/`🎯` field simply leaves `archivo`/`lineas`/`confianza`
as `None` — never an error, since the model's own adherence to the format is not guaranteed.
**Frontend dependency**: mirrors `renderer/src/lib/parseAnalysis.ts` — `13-cross-language-contracts.md`
`XLANG-001`.
**Markers**: `VERBATIM` (the header/location/confidence regexes, shared with `XLANG-001`).

### REVIEW-025 `parse_location`: cleaning and file/line splitting
**Implementation**: `src/CodeFlow.App/Review/ReviewMemory.cs`
**Behaviour**: see "Finding parsing" → "Location" above.
**Inputs / outputs**: `raw: string` → `(string?, string?)` (`(archivo, lineas)`).
**Edge cases**: a Windows drive-letter path (`C:\foo\bar.cs`) with no trailing line number would
split on the drive-letter colon if a digit happened to follow it in the remainder — not otherwise
guarded against, but not observed to occur given the prompt always asks for a repo-relative path.
**Frontend dependency**: mirrors `parseAnalysis.ts`'s `parseLocation` — `XLANG-001`.
**Markers**: `VERBATIM`.

### REVIEW-026 `finding_identity` / `identity`: the reconciliation and posting key
**Implementation**: `src/CodeFlow.App/Review/ReviewMemory.cs`
**Behaviour**: see "Finding parsing" → "Identity" above.
**Inputs / outputs**: `(archivo: string?, categoria: string)` → `string` key (`finding_identity`,
`pub`); `(&MemoryFinding)` → `string` key with the subtitle fallback (`identity`, private).
**Edge cases**: not injective — see `BUG-REVIEW-b`.
**Frontend dependency**: none directly; the key shape itself is not exposed over IPC.
**Markers**: none (see `BUG-REVIEW-b` for the consequence of the non-injectivity).

### REVIEW-027 `reconcile`: matching current findings against the previous run
**Implementation**: `src/CodeFlow.App/Review/ReviewMemory.cs`
**Behaviour**: see "Reconciliation" → "Pass 1" above — the three-way branch (new / reappeared-as-new
/ persists) and its exact field assignments.
**Inputs / outputs**: part of `reconcile(prev, current, prev_iter, changed_files) -> (IReadOnlyList<MemoryFinding>,
ReviewDelta)` — see `REVIEW-028` for the full signature.
**Edge cases**: a reappeared finding loses its old `thread_id` — a fresh post opens a new thread
rather than reopening the one that existed before it was marked resolved.
**Frontend dependency**: `src/types/domain.ts`'s mirrored `MemoryFinding` shape (`XLANG-010`).
**Markers**: `BUG-REVIEW-b`.

### REVIEW-028 `reconcile`: carrying forward unmatched previous findings
**Implementation**: `src/CodeFlow.App/Review/ReviewMemory.cs`
**Behaviour**: see "Reconciliation" → "Pass 2" above — the `file_touched` computation and the
resolved / persists-untouched / carried-untouched three-way split.
**Inputs / outputs**: `prev: IReadOnlyList<MemoryFinding>, current: IReadOnlyList<MemoryFinding>, prev_iter: int,
changed_files: IReadOnlyList<string>?` → `(IReadOnlyList<MemoryFinding>, ReviewDelta { iter_previa, iter_actual,
nuevos, persisten, resueltos })`.
**Edge cases**: reconciliation carries no memory of which `level` a finding was originally detected
at, or which `level` the current run used — see `AMBIGUOUS-REVIEW-b`.
**Frontend dependency**: the returned `ReviewDelta` feeds `delta_banner` (`REVIEW-030`).
**Markers**: `AMBIGUOUS-REVIEW-b`.

### REVIEW-029 `file_in_changed`: suffix-tolerant path matching
**Implementation**: `src/CodeFlow.App/Review/ReviewMemory.cs`
**Behaviour**: normalizes both the finding's file and every entry of `changed` (strip a leading `/`,
lowercase), then matches if either is an exact match or a suffix of the other.
**Inputs / outputs**: `(finding_file: string, changed: IReadOnlyList<string>)` → `bool`.
**Edge cases**: a short, generic filename (e.g. `index.ts`) present at two different paths in the
repo could match a `changed` entry that isn't actually the same file, since suffix matching doesn't
require a full path component boundary — not observed to be guarded against.
**Frontend dependency**: none directly — internal to `reconcile`.
**Markers**: none.

### REVIEW-030 `resolved_history_section` and `delta_banner`
**Implementation**: `src/CodeFlow.App/Review/ReviewMemory.cs`
**Behaviour**: see "Reconciliation" → "Traceability rendering" above for the exact, `VERBATIM`
Spanish templates.
**Inputs / outputs**: `IReadOnlyList<MemoryFinding>` → `string?` (history) / `&ReviewDelta` → `string`
(banner, unconditional).
**Edge cases**: the history section has no size cap and no pruning — a PR with many resolved
findings across a long review history grows this section, and the stored `review_md`, without bound.
This is by design per the source comment ("gives the PR its cumulative traceability"), not flagged
as a defect.
**Frontend dependency**: rendered as part of the review body the frontend already parses
(`XLANG-001`) — the banner and history text are plain prose the frontend's finding parser is
expected to skip over (it sits before/after the finding blocks, not inside one).
**Markers**: `VERBATIM`.

### REVIEW-031 `MemoryFinding.is_active` and the `estado` lifecycle
**Implementation**: `src/CodeFlow.App/Review/ReviewMemory.cs`
**Behaviour**: `estado ∈ {"abierto", "posteado", "resuelto", "falso_positivo", "ignorado"}`;
`is_active()` is `true` only for the first two. Nothing in this file (or `src/CodeFlow.App/Review/ReviewCommands.cs`) ever deletes
a `MemoryFinding` row — every state transition is additive/mutating, never a removal.
**Inputs / outputs**: n/a.
**Edge cases**: `falso_positivo`/`ignorado` are set only by a human action outside the scope of these
two files (not reachable from any command in `src/CodeFlow.App/Review/ReviewCommands.cs` — no command sets `motivo_descarte` or
either discard `estado`); this document cannot establish where that write happens.
**Frontend dependency**: `src/types/domain.ts`'s mirrored `MemoryFinding` — `XLANG-010`.
**Markers**: none.

### REVIEW-032 `post_pr_review_comment`: selection, identity lookup, and the stored run's `meta`
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: see Publishing → `post_pr_review_comment` above, "Loading the run" and "Selection &
identity matching".
**Inputs / outputs**: `project_id, pr_id: long, run_id: string, items: IReadOnlyList<PostFindingItem>,
post_summary: bool, summary: string?` → `void`.
**Edge cases**: a `run_id` that doesn't exist yields `(findings: [], iter: 1)` rather than an error —
every item then posts as a brand-new thread with no stored finding to reconcile against.
**Frontend dependency**: `postPrReviewComment`.
**Markers**: `VERIFIED-LIVE` (2026-08-01 live run — see `90-ambiguities.md`); `BUG-REVIEW-a` (the run's `meta.head_sha` is read from the DB but never
consulted).

### REVIEW-033 `post_pr_review_comment`: per-provider posting and reply wording
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: see Publishing → `post_pr_review_comment` above, "Per-provider posting" — the exact,
`VERBATIM` Spanish reply strings for both providers, and their divergent wording (Azure's italics
plus "Marcado como fixed" suffix vs. GitHub's plain text).
**Inputs / outputs**: n/a (continuation of `REVIEW-032`).
**Edge cases**: GitHub's `head_sha_for` re-fetch anchors every new comment in this batch against the
PR's **current** head, independent of the diff the finding's line numbers were computed from — see
`BUG-REVIEW-a`.
**Frontend dependency**: `postPrReviewComment`.
**Markers**: `VERIFIED-LIVE` (2026-08-01 live run — see `90-ambiguities.md`); `BUG-REVIEW-a`.

### REVIEW-034 `apply_post_outcome`: per-item bookkeeping
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: see Publishing → `post_pr_review_comment` above, "`apply_post_outcome`".
**Inputs / outputs**: `(&mut [MemoryFinding], idx: int?, outcome: long?,
i: int, &mut IReadOnlyList<string>)` → `()` (mutates `findings`/`failures` in place).
**Edge cases**: an unmatched `idx` (`None`) silently drops a successfully-posted new thread's id —
the provider now has a comment CodeFlow has no record of.
**Frontend dependency**: none directly — internal to `post_pr_review_comment`.
**Markers**: none (the consequence of an unmatched `idx` is a plain edge case, not elevated to a
marker, since it requires an item whose file+category doesn't match anything in the loaded run —
not reachable through ordinary UI use of a run's own findings list).

### REVIEW-035 `post_pr_review_comment`: write-back and partial-failure aggregation
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: after every item (and the optional summary) has been attempted, the full `findings`
slice — including every `thread_id`/`estado` change from `apply_post_outcome` — is serialized and
written with the store(run_id, json)` (`STORE-013`), unconditionally, even
if some items failed. Only then is the aggregate error, if any, returned:
`"{failures.len()} comment(s) failed to post — {failures.join("; ")}"`.
**Inputs / outputs**: n/a.
**Edge cases**: the write-back happens exactly once regardless of how many items failed — a caller
retrying the whole command after a partial failure will re-post the items that failed (their
`thread_id` was never set) but also re-select the ones that already succeeded, since nothing in the
returned error identifies which items to retry versus which already posted; the caller (frontend, out
of this document's scope) is responsible for tracking that from the `"#{n}: {e}"` prefixes in the
error string.
**Frontend dependency**: `postPrReviewComment`.
**Markers**: `VERIFIED-LIVE` (2026-08-01 live run — see `90-ambiguities.md`).

### REVIEW-036 `pr_review_decision`
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: see Publishing above.
**Inputs / outputs**: `project_id, pr_id: long` → `string`.
**Edge cases**: none beyond `PROV-015`/`PROV-035`.
**Frontend dependency**: `prReviewDecision`.
**Markers**: none.

### REVIEW-037 `act_on_pull_request`
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: see Publishing above.
**Inputs / outputs**: `project_id, pr_id: long, action: string, body: string?` →
`PrActionOutcome { pr`.
**Edge cases**: the Activity-row write is **not** best-effort here (unlike `review_pull_request`'s
`job_history` write) — a DB failure fails the whole command even though the provider-side action
already succeeded, leaving the PR changed on the host with no local record of it.
**Frontend dependency**: `actOnPullRequest`.
**Markers**: `VERIFIED-LIVE` (2026-08-01 live run — see `90-ambiguities.md`).

### REVIEW-038 The stats line under a review, and what it never reaches
**Implementation**: `src/CodeFlow.App/Review/ReviewRun.cs` (`Details`, `PersistAsync`) ·
`src/CodeFlow.App/Ai/AiText.cs` (`StampFooter`) ·
`renderer/src/lib/ui/runStats.ts` · `renderer/src/components/common/RunStats.tsx`
**Behaviour**: `ReviewRun` — not `AiOperations` — stamps the footer, **last**, onto
`{banner}{review}{history}`. Everything is one line, `·`-separated, and no segment ever contains a
`·`, because the renderer splits on it to lay the line out as chips. In order: the engine and model
that answered, the timestamp, what the run consumed (`AI-017`), then

| segment | example | source |
|---|---|---|
| level | `nivel completo` | the requested level |
| duration | `6 min 23 s` | a `Stopwatch` around the **whole operation** — fetch, diff, model, reconciliation — not the engine call |
| coverage | `diff: 52 archivos · vio 34 (24 enteros, 10 recortados) · 18 sin cambios desde la revisión anterior` | `DiffCoverage` from `GIT-031` |
| findings | `10 hallazgos: 3 nuevos, 4 persisten, 3 resueltos` | the reconciled findings and the delta |

Whole and trimmed **add up to what was seen**, and each count says what it is. The first wording —
`diff: 34 de 52 archivos, 10 recortados` — was read as "it only managed ten" by the person it was
written for, which is the only test that matters for a sentence.

The same numbers are stored structurally on `ReviewMeta` (`Usage`, `OperationMs`, `Coverage`),
because a sentence is for reading and numbers are for comparing: answering "did that get cheaper?"
used to mean opening the CLI's own session files by hand. All three are null on rows written before
they were recorded. `OperationMs` is the whole operation and is deliberately not called
`DurationMs`: `Usage.DurationMs` is the engine's figure for its own run, one level of nesting away,
and the two under one name reported different things (249 570 against 245 424 on one measured run).
A link review has no `DiffCoverage` and no reconciliation, so it stamps the level and the duration
alone, and stores nothing.

**It is never published.** Every path that composes a comment for the host builds its text from the
findings — `formatFindingAsComment`, `formatSummaryComment` — and neither reads the footer;
`ReviewPosting` never sees it.
**Inputs / outputs**: appended to the returned and stored `review_md`.
**Edge cases**: a memory write that fails still stamps, with the level and duration only — what the
run cost is known whether or not it was filed.
**Frontend dependency**: `parseAnalysis`'s end-anchored `FOOTER_RE`, which is also why the footer
must be stamped last. It was stamped inside the operation, and the resolved-findings history section
landed after it: the panel matched nothing, and the review tab had shown no stats at all.
**Markers**: none

### REVIEW-039 What a review may reach for, and what it is not asked to re-read
**Implementation**: `src/CodeFlow.App/Review/ReviewRun.cs` (`Toolset`, `Narrow`, `Routed`)
**Behaviour**: two decisions the review pipeline makes for itself, both downstream of `GIT-033`
putting the surrounding code in the prompt.

**Toolset by depth**, applied through `AiRouting.Bound` on both the ordinary route and the workspace
agent's, so neither can skip it:

| level | checkout | tools |
|---|---|---|
| `basico`, `completo` | yes | none (`--tools ""`) |
| `ultra` | yes | `Read`, `Grep`, `Glob` |
| any | no (link review) | none |

`ultra` keeps them for the one thing the extract genuinely cannot answer: following a callee into a
file the change never touched. A user who has set `{provider}_allowed_tools` keeps their own list —
this is a default, not a policy.

**Files a re-review does not read again.** From the second iteration on, files byte-identical to
what the previous review saw are dropped from both the diff and the extract, and named in the
`NOTE:` block. Their findings travel forward on their own: `REVIEW-028` keeps a finding open when its
file did not change, precisely so a re-review need not rediscover it. **The trade is real and is
declared**: a new problem in an untouched file, caused by a change in another, will not be found that
round; a review from scratch sees every file again. When the two sets have no path in common — a
rename the diffs describe differently, a base branch that moved under both — nothing is narrowed,
because reviewing nothing is never the right reading of that.
**Inputs / outputs**: internal to `ForProjectAsync` / `ForLinkAsync`.
**Edge cases**: the first review of a pull request has no previous head and narrows nothing.
**Frontend dependency**: none; the coverage segment of `REVIEW-038` is where a user sees it.
**Markers**: none

### REVIEW-040 A finding still open that this run never restated is named, not just counted
**Implementation**: `src/CodeFlow.App/Review/ReviewMemory.cs` (`PersistingSection`)
**Behaviour**: appended to a re-review's body, before the resolved history: every finding in
`abierto` or `posteado` whose identity does not appear in what the model wrote this run.

```
### 📌 Siguen abiertos de revisiones anteriores

- `resumen-huerfano` · src/CodeFlow.App/Review/ReviewPosting.cs — F-004, introducido iter 3; sin cambios en ese archivo desde entonces
```

**Why**: `REVIEW-028` keeps a finding open when its file did not change, so a re-review never has to
rediscover it — and `REVIEW-039` now stops sending that file at all. Correct, and it left the reader
with a delta banner saying `2 persisten` and nothing anywhere naming the two: not in the body, not
in the resolved history. Observed on this repository's own pull request, where the two were a race
window and a misleading comment.
**Inputs / outputs**: `(findings, restated) -> string?`; null when the body already covers every
open finding, so a first review stays clean.
**Edge cases**: matched by `FindingIdentity` — file plus category — and not by position in the
merged list, even though `Reconcile` happens to emit the restated ones first. A section depending on
that order would break silently the day it changed.
**Frontend dependency**: rendered as part of the review markdown. Like the resolved history it
lands inside the last finding's block, and like it is inert there: its `###` heading carries none of
the three severity emoji so no parser reads it as a finding, and every field regex stops at its own
marker before reaching it.
**Markers**: none

## Test coverage

Neither file in this document's scope carries a ` function — confirmed by grep
(`/`, zero hits in `src/CodeFlow.App/Review/ReviewMemory.cs` and `src/CodeFlow.App/Review/ReviewCommands.cs`). No fixtures
are produced or expected for this document, per `test-vectors/README.md`'s "not extracted" category
having no bearing here either (there's simply nothing to extract — no executable specification, not
even a manual one, exists in-tree for this pipeline). This document's rules are the closest thing to
one.

| extracted case | Source | Fixture | Kind |
|---|---|---|---|
| — | — | — | — |

Sum contributed to the global 128-test count: **0**.

## Markers raised

| Local id | Kind | Summary |
|---|---|---|
| `DIVERGENCE-REVIEW-a` | DIVERGENCE | **Was `AMBIGUOUS-REVIEW-a`, closed once the source runbook was consulted.** The model's own `F-NNN` headers were never rewritten to the stable id `reconcile()` assigns, so the id a human read could name a different finding from the one thread reuse acted on. `report-standard.md` §3.1 shows the drift was never intended: there the model writes a minimal JSON draft and an engine assigns the ids and renders the report, so one source of truth is structural. `ReviewMemory.RenumberHeaders` now aligns them, checking the positional pairing by identity and returning the text untouched if it does not hold. Only the header token moves — prose naming an id is the model's argument, not a reference this engine owns. |
| `DIVERGENCE-REVIEW-b` | DIVERGENCE | **Was `AMBIGUOUS-REVIEW-b`, closed as an operator decision.** `reconcile()` recorded no level on either side, so a re-review at a shallower depth marked findings it never looked for as `resuelto` — it could not tell "fixed" from "not examined". **The source runbook does not settle this**: it records no per-finding level either, and its report standard ("persistence always happens, at all three levels") is an instruction to the reviewing agent, not a mechanism. `MemoryFinding.Nivel` now records the depth that last saw a finding, and a shallower run marks it `fuera_de_alcance`, counted separately in the delta banner. A level stored before this field existed is unranked and behaves exactly as before. |
| `BUG-REVIEW-a` | BUG | Neither `post_pr_review_comment` nor `post_pr_link_review_comment` consults the review run's own recorded `head_sha` (present in `ReviewRunDetail.meta`, read from the DB but never destructured) before posting. GitHub anchored comments are posted against a **freshly re-fetched** head SHA (`head_sha_for`, called at post time) while carrying line numbers computed from the diff at *review* time; Azure anchored comments are posted against whatever the PR's *current* latest iteration is (`get_latest_iteration_id`, called inside `post_pr_comment_anchored`), again independent of the iteration the review actually analyzed. If the PR received a push between the review run and the post action, a finding's line numbers can land on the wrong lines of the new head/iteration, with no warning surfaced to the user anywhere in this file. Suspected-correct behaviour: compare the stored run's `head_sha` against the PR's current head before posting, and warn (or refuse) rather than silently anchoring against a mismatched commit. Ported as-is — not fixed. |
| `BUG-REVIEW-b` | BUG | `finding_identity`/`identity` (`src/CodeFlow.App/Review/ReviewMemory.cs`) is not injective: two distinct findings that share the same `{file}\|{categoria}` key (or, when both are empty, the same subtitle) are indistinguishable to `reconcile()`'s `prev.iter().find(...)` lookup. Both would independently match — and each copy — the *same* previous finding's `id`/`thread_id`/`estado`, producing two rows in the merged `findings[]` array with a duplicate stable `id`. The same collision affects `post_pr_review_comment`'s `index_of`, which uses the identical key. Suspected-correct behaviour: identity matching should be one-to-one per reconciliation pass (e.g. removing a `prev` candidate from the pool once it's matched by one `current` finding), so a second same-key finding is recognized as genuinely new rather than aliased onto the first. Ported as-is — not fixed. |
