# BUSINESS_RULES — the specification index

**What CodeFlow does**, written down. This is the only specification the application has;
`src/CodeFlow.App/`, `shell/` and `renderer/` are the implementation that must satisfy it.

This file is the index. The content lives in `business-rules/`.

---

## The documents

| # | Document | Owns |
|---|---|---|
| 00 | [Conventions](business-rules/00-conventions.md) | Rule format, marker vocabulary, the authoritative counts |
| 01 | [IPC surface](business-rules/01-ipc-surface.md) | All 220 commands and all 13 events — the contract |
| 02 | [Bootstrap and platform](business-rules/02-bootstrap-platform.md) | Startup sequence, paths, tray, native menu, and the 9 non-command shell call sites |
| 03 | [Storage](business-rules/03-storage.md) | The whole storage layer: 18 tables verbatim, 20 migrations, 80 query functions |
| 04 | [Git](business-rules/04-git.md) | `src/CodeFlow.App/Git/` — 44 commands and both events |
| 05 | [AI engines](business-rules/05-ai-engines.md) | `src/CodeFlow.App/Ai/` — six engines, the run lifecycle, the nine prompts |
| 06 | [VCS providers](business-rules/06-providers.md) | GitHub and Azure DevOps clients, PR-link parsing |
| 07 | [PR review pipeline](business-rules/07-review-pipeline.md) | `src/CodeFlow.App/Review/` — the flagship feature |
| 08 | [API client](business-rules/08-api-client.md) | `src/CodeFlow.App/ApiClient/` — HTTP, WS, Socket.IO, MQTT |
| 09 | [Workspace-scoped](business-rules/09-workspace-scoped.md) | Workspaces, projects, settings, skills, activity |
| 10 | [Security](business-rules/10-security.md) | Credential store and the secret scanner |
| 11 | [Files, search, watcher, terminal](business-rules/11-files-search-terminal.md) | File operations, search, the watcher, the PTY |
| 12 | [Debugging](business-rules/12-debugging.md) | DAP and the debugger backends — **deferred, not implemented** |
| 13 | [Cross-language contracts](business-rules/13-cross-language-contracts.md) | Literals duplicated in C# and TypeScript |
| 90 | [Ambiguities](business-rules/90-ambiguities.md) | What is unsettled, and what has never run against a real system |
| 91 | [Preserved behaviours](business-rules/91-known-bugs.md) | 22 defects kept for 1.7.2 compatibility, **not fixed** |
| — | [Test vectors](business-rules/test-vectors/README.md) | 24 fixture files + 3 SQL seeds, 133 cases |

**~11 000 lines** of specification.

## Where to start

- Implementing a feature → its domain document, then `90` and `91` for what it must not assume.
- Touching the IPC surface → `01`, then `13`.
- Anything touching prompts, review output or error strings → `13` first. Those break silently.

---

## Marker vocabulary

| Marker | Count | Meaning |
|---|---:|---|
| `BUG-*` | **22** | Behaviours preserved for 1.7.2 compatibility, deliberately not fixed (4 now closed) |
| `DIVERGENCE-*` | **22** | Behaviour that looks wrong and is preserved anyway |
| `AMBIGUOUS-*` | **17 open** | Questions the code does not answer; never resolved by guessing |
| `UNVERIFIED` | 36 mentions | Code that compiles but has never run against a real external system |
| `VERBATIM` | 68 blocks | Content that must be reproduced byte-for-byte |
| `DEAD` | 1 command | `debug_is_running` |

## The findings most worth knowing

1. **`BUG-REVIEW-a` — review comments can be posted against the wrong commit.** Azure anchors to
   whatever the current latest iteration is, so a push between review and post lands findings on
   the wrong lines. GitHub's half is closed: it compares the run's analysed head against the
   current one and refuses the batch (`XLANG-014`).
2. **The prompt-template cascade has two levels, not four.** The workspace row, or — when it is
   missing *or blank* — a hardcoded constant. No global layer, no versions, no forks, no upgrade
   diff. Saving a blank string *is* the reset-to-default action.
3. **ADO PAT expiry has no dedicated detection.** Every non-2xx collapses into one generic string
   (`AMBIGUOUS-PROV-b`), so an expired credential is indistinguishable from a network failure.
4. **Tokens no longer leave the sidecar.** They live in the OS keychain, and every credential
   family is now `set`/`has`/`delete` — no command returns a secret. Up to 1.7.4, `get_ado_pat`
   and `get_github_token` returned the plaintext to the renderer; removing them cost nothing
   because one had no caller and the other used the value as a boolean (`DIVERGENCE-SEC-d`).
   The invariant that always held is still the strongest one: **no credential reaches an AI agent
   process.**
5. **Git network operations cannot be cancelled.** There is no kill, timeout or abort path for
   clone/fetch/pull/push. Adding cancellation would be new behaviour (`AMBIGUOUS-GIT-b`).

## Open questions

1. ~~Can the source review runbook be supplied?~~ **Settled.** It was consulted, and what it
   governs — the re-review reconciliation rules, the review taxonomy and the depth levels — is
   transcribed into `05-ai-engines.md`, `07-review-pipeline.md` and
   `13-cross-language-contracts.md`. `AMBIGUOUS-REVIEW-a` and `-b` are closed.
2. Should `C:\CodeFlow` be preserved? Changing it strands every existing Windows user's database
   and credentials, so removing the hardcoded path means writing a migration, not changing a
   constant.
3. Should the API client keep storing per-request auth as plaintext JSON in SQLite
   (`DIVERGENCE-STORE-a`)? Long-standing, but worth deciding rather than inheriting.
4. Should git network operations gain cancellation?
