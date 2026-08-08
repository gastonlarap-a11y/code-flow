# 91 — Behaviours preserved for 1.7.2 compatibility

Twenty-three defects carried over from CodeFlow 1.7.2 (`BUG-AI-b`, found in a live run after the
port, included — its code is 1.7.2's own). **They are preserved on purpose, not
overlooked.** The rule is explicit: do not silently correct one, because the renderer and every
existing 1.7.2 install may depend on the behaviour. Quietly fixing one changes the application, and
the change surfaces later as an unexplained difference nobody chose.

Each row states what the code does and what it probably should do. Fixing any of them is a decision
to take as its own change, with its own test and its own release note.

**Ten are now closed**, which is that decision being taken rather than an exception to the rule
above. The chosen ones lose data, refuse to start, leak resources, weaken transport security or a
security check, or degrade what the user sees — and each was fixed with its own test. They are
struck through below with their reasoning kept, so nobody reads a closed row as still-current
behaviour. Everything not struck through is still preserved. Two of the remaining rows —
`BUG-DBG-d` and `BUG-DBG-f` — are open for a different reason: the debugging feature they live in
is not implemented in this codebase (deferred out of v1, see `12-debugging.md`), so there is
nothing to fix yet. **The future debugging port must be born with both fixes incorporated**; they
are pre-existing defects of the reference, not behaviours to reproduce.

| Closed | Was | Fixed by |
|---|---|---|
| `BUG-STORE-a` | A crash mid-migration made **every subsequent launch fail**, permanently | One transaction, plus `INSERT OR IGNORE` so an already-broken database recovers |
| `BUG-REVIEW-a` | Findings posted onto lines that had moved, **with no warning** | GitHub compares the run's analysed head against the current one and refuses the batch (`XLANG-014`). Azure's half stays open — it anchors by iteration and has no SHA to compare |
| `BUG-REVIEW-b` | Two findings sharing `{file}\|{categoria}` got the **same id and thread** | One-to-one matching, in both `Reconcile` and the posting flow |
| `BUG-API-d` | MQTT skipped TLS signature verification — and, in the port, skipped it **even with `verify_ssl` on** | One shared `StreamTlsPolicy`, modelled on the WebSocket's verifier as the bug asked |
| ~~`BUG-STORE-b`~~ **CLOSED** | A moved project's review history **silently vanished** from its new workspace, while staying deletable from the old one | `move_project_to_workspace` updates `review_runs.workspace_id` in the same transaction, plus the `RealignReviewRunWorkspaces` migration step for databases that diverged before the fix |
| ~~`BUG-WS-a`~~ **CLOSED** | A skill folder that could not be deleted was **orphaned with no row left to find it by**, permanently blocking its name | Folder first, failure propagated (`SkillFiles.RemoveDirectory`), row second — an undeletable folder aborts before the row is touched and the remove is retryable |
| ~~`BUG-WS-b`~~ **CLOSED** | Re-installing a skill name ran npx over the shared folder and **added a duplicate row** | The same `Directory.Exists` guard its two sibling creation paths always had, run before npx |
| ~~`BUG-FILE-a`~~ **CLOSED** | A write through `../` to a file not yet on disk **landed outside the repository** | The containment fallback is a lexical `Path.GetFullPath`, so `..` resolves away before the check — same shape as the shell's `isWithinRoot` (F0.6) |
| ~~`BUG-AI-a`~~ **CLOSED** | Engine temp payload files were never deleted — **unbounded temp growth** for the life of the app | `EngineScratch`: one owner for creation, recognition, deletion in the runner's `finally` on every exit path, and an age-gated (1 h) startup sweep |
| ~~`BUG-GIT-a`~~ **CLOSED** | Every rename displayed as an **unrelated delete + add pair**; the `"renamed"` label was dead code | `SimilarityOptions.Renames` on the user-facing diffs and both status detection flags on; Checkpoints' internal compare keeps `None` on purpose |

Ids are those assigned by the owning document. Severity is this inventory's judgement of
user-visible impact, not a field from the source.

---

## Data loss or silent incorrectness

| Id | Document | Severity | Defect |
|---|---|---|---|
| ~~`BUG-REVIEW-a`~~ **CLOSED** | `07-review-pipeline.md` | **high** | Neither posting command consults the review run's own recorded `head_sha`. GitHub anchored comments are posted against a **freshly re-fetched** head SHA while carrying line numbers computed from the diff at *review* time; Azure anchors against the PR's *current* latest iteration. If the PR received a push in between, findings land on the wrong lines with **no warning anywhere**. Should compare the stored `head_sha` against the current head and warn or refuse. `UNVERIFIED` as well — this path has never run against a real API. |
| ~~`BUG-REVIEW-b`~~ **CLOSED** | `07-review-pipeline.md` | high | The finding identity key `{file}\|{categoria}` is not injective. Two findings sharing it both match the *same* previous finding in `reconcile()`, copying its `id`, `thread_id` and `estado` — producing duplicate stable ids in the merged `findings[]`. The same collision affects `post_pr_review_comment`'s `index_of`. Matching should be one-to-one per pass. |
| ~~`BUG-STORE-a`~~ **CLOSED** | `03-storage.md` | high | `migrate_api_tables_finish`'s four-table row copy runs as unwrapped, non-idempotent `INSERT`s. A crash between them leaves the migration half-applied, and **every subsequent launch then fails** on a primary-key collision instead of resuming. Should be one transaction, or use `INSERT OR IGNORE`. |
| ~~`BUG-STORE-b`~~ **CLOSED** | `03-storage.md` | medium | `review_runs.workspace_id` is a write-time denormalisation with no FK and no upkeep. `move_project_to_workspace` never updates it, so a moved project's review history silently vanishes from its new workspace while remaining deletable from its old one. |
| ~~`BUG-WS-a`~~ **CLOSED** | `09-workspace-scoped.md` | medium | `remove_workspace_skill` deleted the database row *before* the filesystem removal and swallows the latter's error, orphaning a folder that then blocks reuse of its name. Order should be reversed, or the error surfaced. |
| ~~`BUG-WS-b`~~ **CLOSED** | `09-workspace-scoped.md` | medium | `install_workspace_skill` had no existing-skill guard — unlike `create_custom_skill` and `import_skill_from_folder` — so re-installing the same name creates a duplicate row over one shared folder. |
| `BUG-AI-b` | `05-ai-engines.md` | medium | `quota_signal`'s 11-phrase substring match runs over the engine's whole output, so a **successful review whose findings merely mention one of the phrases is misclassified as a quota failure** and discarded — the run is never saved and the command errors with `QUOTA_EXCEEDED::` carrying the full, correct review text. Observed live 2026-08-01: a `completo` review whose F-002 explanation contained "rate limiting" was thrown away whole; the retry produced wording without the phrase and saved fine. Discovered during the live-run verification (`90-ambiguities.md`), recorded rather than fixed per this document's rule. |

## Security-relevant

| Id | Document | Severity | Defect |
|---|---|---|---|
| ~~`BUG-API-d`~~ **CLOSED** | `08-api-client.md` | **high** | MQTT's and gRPC's `verify_ssl: false` certificate verifiers skip TLS 1.2/1.3 signature verification entirely (unconditional `assertion()`), unlike WebSocket's equivalent, which keeps genuine signature checking. The three should behave the same way; the WebSocket one is the correct model. |
| ~~`BUG-FILE-a`~~ **CLOSED** | `11-files-search-terminal.md` | medium | `resolve_within_repo`'s containment check degraded to a lexical `starts_with` when the candidate path does not yet exist: `canonicalize()` fails and the code falls back to the raw joined path, so `..` segments are never normalised. The source comment notes the guard is defensive only — the app opens files the user picked from its own tree — which is why this is medium rather than high. |

## Protocol and standards conformance

| Id | Document | Severity | Defect |
|---|---|---|---|
| `BUG-API-a` | `08-api-client.md` | medium | The 301/302 redirect handler downgrades **any** non-GET/HEAD method to GET, not just POST as its own adjacent comment claims. Reachable only when `keep_auth_on_redirect` is set and the hop crosses hosts. |
| `BUG-API-b` | `08-api-client.md` | medium | Digest challenge detection misses a `WWW-Authenticate` header that combines several schemes in one value when Digest is not listed first. |
| `BUG-API-c` | `08-api-client.md` | low | `Set-Cookie`'s default path is hardcoded `"/"` instead of RFC 6265 §5.1.4's default-path algorithm. |
| `BUG-PROV-a` | `06-providers.md` | medium | `src/CodeFlow.App/Providers/Azure/AzureClient.cs` applies `encode_segment` to `repo_id` in three functions and **not** in nine others, which interpolate it raw. A repository whose name contains a space or reserved character therefore works or fails depending on which call you make. |
| `BUG-PROV-b` | `06-providers.md` | low | `src/CodeFlow.App/Providers/Azure/AzureClient.cs`'s `decode_path_segment` unescapes only `%20`; every other percent-escape in an org/project/repo name parsed from a git remote is left encoded — unlike `src/CodeFlow.App/Providers/PrLink.cs`, which has a full decoder. |

## Resource leaks

| Id | Document | Severity | Defect |
|---|---|---|---|
| ~~`BUG-AI-a`~~ **CLOSED** | `05-ai-engines.md` | medium | Temp payload files were written per invocation and never deleted: opencode's `--file` attachment and agy's large-brief temp file *and* its per-call directory. Unbounded growth over the life of the app. The source itself notes "Temp files aren't cleaned up yet". |
| `BUG-DBG-d` | `12-debugging.md` | medium | DAP `start()` leaks the adapter and debuggee processes if `initialize` fails or the 10 s ready-wait times out — the session never reaches the registry slot, so `stop()` cannot find it. |
| `BUG-DBG-f` | `12-debugging.md` | medium | Node `start()` leaks the node process if the WebSocket connect or any post-connect CDP call fails. Inconsistent with its own discovery-failure path, which *does* kill the child. |

## Behavioural inconsistencies

| Id | Document | Severity | Defect |
|---|---|---|---|
| ~~`BUG-GIT-a`~~ **CLOSED** | `04-git.md` | medium | Rename/copy detection was never enabled on any diff or status query, so the `"renamed"` and `"copied"` status labels the code defines are unreachable — every rename displays as an unrelated delete + add pair. Fix would be `Diff.FindSimilar` plus the `renames_*` status options. |
| `BUG-DBG-a` | `12-debugging.md` | medium | Starting one debugger backend does not stop a session already running on the other, and the frontend does not call `debug_stop` first either. DAP always wins routing, orphaning the other session. |
| `BUG-DBG-b` | `12-debugging.md` | low | DAP can emit `debug:terminated` twice for one session end (the explicit adapter event plus an unconditional after-loop emit). Node emits it once. |
| `BUG-DBG-c` | `12-debugging.md` | low | DAP drops empty `debug:output` lines; Node's raw stdout/stderr piping does not. |
| `BUG-DBG-e` | `12-debugging.md` | medium | DAP's `set_breakpoints` never clears a file whose last breakpoint was removed, because the file is dropped from the input map entirely rather than sent with an empty list. Node's `apply_breakpoints` handles this correctly. |
| `BUG-XLANG-a` | `13-cross-language-contracts.md` | low | The frontend's routing cascade omits the commit-model step, so for the `claude` provider the settings UI displays the base model while the backend actually runs `claude-haiku-4-5-20251001`. Affects display only — the UI value does not drive the run. |

---

## Reclassified

| Id | Was | Now | Why |
|---|---|---|---|
| `BUG-SEC-a` | BUG | `DIVERGENCE-SEC-c` | The premise was that Linux is a release target. It is not — `release.yml`'s build matrix is `windows-latest` and `macos-latest` only. The missing Linux `keyring` backend cannot affect a shipped binary. Full reasoning in `10-security.md` SEC-004. |

## Not bugs, deliberately

Twenty-two `DIVERGENCE-*` markers record behaviour that *looks* wrong and must be preserved
anyway — hooks never firing, the watcher not being a debounce, `C:\CodeFlow`, the Windows
terminal refusing to fall back to PowerShell, ADO votes not being unified with GitHub review
events, and the rest. They live in their owning documents rather than here, precisely so nobody
reads this file and "fixes" one of them. `00-conventions.md` explains the distinction.
