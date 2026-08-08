# 90 — Ambiguities and unverified paths

Everything this inventory could **not** settle from the code alone, plus the code paths that are
unambiguous but have never been executed against a real external system. Both classes exist so
nobody has to guess, and so untested code is never mistaken for proven behaviour.

Ids are those assigned by the owning document.

---

## Unverified paths — closed by the live run of 2026-08-01

Until 2026-08-01 the PR review posting, reply, thread-resolution and GitHub GraphQL paths had
never been executed against real Azure DevOps or GitHub APIs — 36 `UNVERIFIED` markers across
`06-providers.md` and `07-review-pipeline.md`. That day the app itself (dev build, real sidecar,
real credential store) ran the full write matrix against one throwaway PR per host: a disposable
private GitHub repository (created for the run, deleted after) and the disposable Azure repo
`Dev.prueba` in the operator's organization. Every result was cross-checked from **outside** the
app through each host's own API. The per-endpoint verdicts live on the markers themselves
(`VERIFIED-LIVE`); this is the record of the run.

**What ran, in order, on each host** — every step through the app's own commands:
1. `create_pull_request` — one real PR per host (GitHub `#1`; Azure `!87266`, PAT identity
   resolved to the operator's real display name).
2. A real AI review (`review_pull_request`, level `completo`, Claude Code engine) — 4 findings
   per host on the seeded diff (modified file, new file, and a pure rename).
3. First publish — 4 anchored threads (correct paths and line ranges on both hosts:
   GitHub `path`+`start_line`/`line`, Azure `threadContext.filePath`+right-file range) plus one
   unanchored summary (GitHub issue comment / Azure PR-level thread).
4. Second publish of the same findings — every reply landed **inside its saved thread**; no
   duplicate threads (the reconciliation identity held; Azure's hardcoded `parentCommentId: 1`
   held too).
5. Fix pushed to the GitHub PR branch → re-review reconciled `2 nuevos · 2 persisten · 2
   resueltos` → publishing the resolved items replied a follow-up and **resolved both GitHub
   threads via the GraphQL mutation** (`isResolved: true` read back externally).
6. Actions: GitHub approve returned the live **422 self-approval classified `SELF_APPROVAL: `**
   (`XLANG-013` verified against the real API) and close succeeded; Azure approve set reviewer
   vote `10` (readable via `viewer_decision` and externally) and close **abandoned** the PR.
7. Link path (Azure only — the GitHub repo was already deleted when this leg ran):
   `post_pr_link_review_comment` landed a fresh thread on the abandoned PR;
   `act_on_pr_link` executed and the host refused the vote with 400 `TF401181` (PR not editable
   in its state) — the error mapping behaved.

**What stayed out of reach**:
- Azure `set_pr_thread_status` — fires only when a re-review resolves a posted finding, which
  needs the PR's **remote** source branch to change (verified: a local-only commit is invisible —
  the review reads the PR's remote tip), and the throwaway Azure repo allowed exactly one push.
  It keeps its `UNVERIFIED` marker; any disposable ADO repo allowing a second push closes it.
- A 2xx `submit_pr_review` APPROVE on GitHub — structurally impossible with one account: the
  token owner authored the PR and GitHub refuses self-approval.

**Found during the run** (recorded, not fixed): `BUG-AI-b` — `quota_signal`'s substring dictionary
matched "rate limiting" *inside a finding's explanation* and discarded a complete, successful
review as `QUOTA_EXCEEDED::` (see `91-known-bugs.md` / `05-ai-engines.md` AI-014). Also observed:
`auto_link_project` stores the Azure `ado_repo_id` as the repo **name** (`Dev.prueba`), which puts
`BUG-PROV-a`'s unencoded-`repo_id` interpolation on the live path for any repo whose name needs
percent-encoding.

---

## Open ambiguities

**Thirteen still open**, of the seventeen Phase 1 raised. Each is a question the source does not
answer; none was resolved by guessing. Four have moved to the resolved table below: `AMBIGUOUS-WS-a`
was settled by reading during the port, `AMBIGUOUS-PROV-b` closed as a deliberate divergence, and
`AMBIGUOUS-REVIEW-a` / `AMBIGUOUS-REVIEW-b` closed once the `WF-PR-REVIEWER` document was supplied.

(This line read "Sixteen still open" for a while after those closures — a count kept by hand that the
closures did not update. Anyone recounting should trust the tables, not this sentence.)

### Product decisions for the operator

| Id | Document | Question |
|---|---|---|
| `AMBIGUOUS-GIT-b` | `04-git.md` | There is **no cancellation path at all** for clone/fetch/pull/push — no kill, no timeout, no abort. Should the port add one? There is no the sidecar behaviour to port, so this is a product decision, not a translation. |
| `AMBIGUOUS-STORE-a` | `03-storage.md` | The `DEFAULT ''` on four `api_*.workspace_id` columns can never satisfy its own `REFERENCES workspaces(id)` constraint and no `INSERT` relies on it. Preserve, drop, or replace? |
| `AMBIGUOUS-FILE-c` | `11-files-search-terminal.md` | Is replacement-character artefacting acceptable when a UTF-8 sequence straddles a 4096-byte PTY read boundary, or does the frontend emulator mask it? |
| `AMBIGUOUS-DBG-a` | `12-debugging.md` | Is DAP's asymmetry — swallow breakpoint errors at start, propagate them at `set_breakpoints` — deliberate? |

### Behaviour that depends on an external system

These cannot be settled from this tree at all; they need a live adapter, server or platform.

| Id | Document | Question |
|---|---|---|
| `AMBIGUOUS-PROV-a` | `06-providers.md` | GitHub thread grouping assumes the API returns each reply after its root comment. Not enforced; **observed to hold** in the 2026-08-01 live run (six threads, up to three comments each) — an observation, not a guarantee, so it stays open. |
| ~~`AMBIGUOUS-PROV-b`~~ | `06-providers.md` | **Closed by a deliberate divergence** — see `DIVERGENCE-PROV-b` below. |
| `AMBIGUOUS-PROV-c` | `06-providers.md` | ADO's `list_pull_requests` sets no `$top`; the server's effective default page size is unknown. The 2026-08-01 live run could not settle it — the throwaway repo held one PR. |
| `AMBIGUOUS-DBG-b` | `12-debugging.md` | Do all targeted DAP adapters tolerate forward-slash-normalised paths on Windows? |
| `AMBIGUOUS-DBG-c` | `12-debugging.md` | Do all targeted DAP adapters reliably send `continued`, so `debug:resumed` fires? |
| `AMBIGUOUS-FILE-a` | `11-files-search-terminal.md` | Which native backend `FileSystemWatcher` selects per OS is a library compile-time choice, not pinned here. |
| ~~`AMBIGUOUS-WS-a`~~ | `09-workspace-scoped.md` | **Resolved during the port** — see below. |
| `AMBIGUOUS-API-a` | `08-api-client.md` | Socket.IO `ACK` packets are parsed and logged but never correlated back to the `emit` that requested them; no pending-ack registry exists. Was correlation intended? |
| `AMBIGUOUS-API-b` | `08-api-client.md` | gRPC reflection's transitive-dependency chase gives up silently after `MAX_DEPENDENCY_ROUNDS = 8` without naming what is still missing. |
| `AMBIGUOUS-AI-a` | `05-ai-engines.md` | opencode's `fix_tools()` list is marked `TODO(verify)` in source and has no runtime effect — opencode has no allow-list flag to receive it. Are these its real tool names? |
| `AMBIGUOUS-GIT-a` | `04-git.md` | `checkout_remote_tracking` silently reuses a pre-existing same-named local branch without fixing up its upstream. Reuse, reject, or re-point? |

### ~~Blocked on a document that is not available~~ — the document was supplied

Four places cite rules as coming from an external `WF-PR-REVIEWER` document —
`src/CodeFlow.App/Review/ReviewMemory.cs` (`re-review.md`), `src/CodeFlow.App/Ai/AiOperations.cs` and `src/CodeFlow.App/Ai/AiOperations.cs` (the review taxonomy and
level rules), and `src/state/prStore.ts:37` (the depth levels). `90-ambiguities.md` recorded it as
living on a machine that is not available.

**It was supplied**, and it is at `/Users/gaston/Documents/Git/WF-PR-REVIEWER`. It is not a
document but a working tool — Python and PowerShell under `tools/`, with `AGENTS.md` as its
canonical runbook and the rules under `.claude/skills/pr-review/references/`. The two files that
bear on these entries are `report-standard.md` (taxonomy, levels, id assignment) and `re-review.md`
(reconciliation). CodeFlow 1.7.2 repository's own `memory/wf-pr-reviewer-port.md` records that its
methodology was already ported into the the sidecar code once, which is why the prompts under
`Ai/Prompts/` match it.

**It is deliberately not copied into this repository.** Its `reviews/` directory holds real review
history for client projects; that is not this repository's data to carry.

| Id | Document | Resolution |
|---|---|---|
| `AMBIGUOUS-REVIEW-a` | `07-review-pipeline.md` | **Answered: not intentional.** `report-standard.md` §3.1 has the model write a minimal JSON draft and an *engine* assign the `F-NNN` ids and render the report — one source of truth by construction, so the drift cannot arise there. It arrived when the port kept the model's free-written markdown and dropped the render step. Closed as `DIVERGENCE-REVIEW-a`: `ReviewMemory.RenumberHeaders` rewrites the header ids to the reconciled ones, checking the pairing rather than assuming it, and leaving the text untouched if it does not hold. |
| `AMBIGUOUS-REVIEW-b` | `07-review-pipeline.md` | **Not answered by the document — closed as a product decision.** WF-PR-REVIEWER records no level per finding either; its `report-standard.md` §2 says persistence "always happens, at all three levels", which is an instruction to the reviewing agent, not a mechanism. So there was nothing to copy. The operator decided to close it: `MemoryFinding.Nivel` records the depth that last saw a finding, and a shallower re-review marks it `fuera_de_alcance` instead of `resuelto`. **This is a decision, not something 1.7.2 sanctions**, and it is recorded that way on purpose. |

---

## Resolved during the merge pass

Kept so the ids are not reused and so the reasoning is not repeated.

| Id | Document | Resolution |
|---|---|---|
| `AMBIGUOUS-PROV-b` | `06-providers.md` | **Closed by a deliberate divergence, `DIVERGENCE-PROV-b`.** CodeFlow 1.7.2 collapses every non-2xx from every Azure endpoint into one message shape — `src/CodeFlow.App/Providers/Azure/AzureClient.cs` repeats `if !status.is_success()` at six call sites and branches nowhere — so an expired PAT reads exactly like a missing repository, and with an unknown organisation the message is Azure's HTML error page interpolated whole. `AGENTS.md` asks for the opposite in so many words, and organisation policy caps PAT lifetime, so it is a state every user reaches. `401`/`403` now set `AzureException.Unauthorized` and carry the `CREDENTIAL_REFUSED: ` sentinel prefix (`XLANG-012`); `resolve_pr_link` answers `PrLinkResolution.Expired` and the sidebar offers Settings instead of a Retry that would fail identically. **Nothing else changed** — a 404 is still a 404, asserted by its own test. The operator asked for this; it is not a silent correction. |
| `AMBIGUOUS-WS-a` | `09-workspace-scoped.md` | **Settled, not decided.** The question was whether `move_project_to_workspace` can succeed with a nonexistent `workspace_id`, which depends on `PRAGMA foreign_keys` state that `src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`/`src/CodeFlow.App/Activity/ActivityLogStore.cs` do not show. It is set in `Migrations.Run`, on the one connection the process holds for its whole life, so it is on at call time and the `UPDATE` is rejected. The port reproduces that by setting the same pragma in `Database.Open`; `ProjectStoreTests` asserts the rejection so the behaviour cannot drift silently. No validation was added — the foreign key is the check. |
| `AMBIGUOUS-FILE-b` | `11-files-search-terminal.md` | **Not an ambiguity.** The terminal resize argument order is correct end to end: `commands.ts:213` sends `{ id, cols, rows }`, the transport binds by name, and `PtySize { rows, cols, … }` is a field-name initialiser, so declaration order cannot matter. Both call sites (`TerminalPane.tsx:51,73`) pass `term.cols, term.rows`. |
| `BUG-SEC-a` | `10-security.md` | **Reclassified** to `DIVERGENCE-SEC-c`. The premise — that Linux is a release target — is wrong: `release.yml`'s build matrix is `windows-latest` and `macos-latest` only, and the `ubuntu-latest` job produces no bundle. The missing Linux `keyring` backend is a build-time constraint, not a defect in any shipped binary. |

---

## Decided after parity, from using the app

Nothing here was an open question during the port. These are things 1.7.2 does that only
showed themselves as problems once the built app was in a user's hands, and they are recorded with
the same weight as an ambiguity so nobody later reads the code and "restores" the old behaviour.

| Id | Document | Decision |
|---|---|---|
| `DIVERGENCE-PROV-c` | `06-providers.md` | **GitHub's self-approval 422 is told apart from every other error.** The operator approved a pull request from the app and got GitHub's raw JSON envelope in a toast — `{"message":"Unprocessable Entity","errors":["Review Can not approve your own pull request"],…}` — because `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs` branches on no status code at all and the port reproduced that faithfully. It is not an edge case: a solo maintainer meets it on every pull request they open, and unlike a network error there is no retry and no credential that changes the outcome. The 422 now sets `GitHubException.SelfApproval` and carries the `SELF_APPROVAL: ` sentinel at the two act-on-a-pull-request boundaries (`XLANG-013`); the panel disables Approve when the PR's author matches a connected login, so the error became the backstop rather than the path. **Nothing else changed** — a 401 is still an undifferentiated 401 and a 422 that is not this one is still a raw 422, each asserted by its own test. The cost is a dependency on a sentence GitHub owns and can reword; that is stated in `XLANG-013` rather than hidden. |
