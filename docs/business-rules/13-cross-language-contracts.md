# 13 — Cross-language literal contracts

Values that exist **twice** — once in C#, once in TypeScript — and must stay byte-identical.
This is the one class of defect that survives a clean compile on both sides: paraphrase either
half and nothing errors, a feature just silently stops working.

Everything in this document is `VERBATIM`. It was produced by a dedicated sweep — grepping both
trees for `mirror`, `in sync` and `must match`, then opening both sides of every hit — not as a
by-product of the domain documents. Re-run that sweep whenever a literal on either side changes.

## Scope

No implementation files are owned here; every literal below is also documented in behavioural
context by the domain document that owns its file. This document exists so the *pairing* has one
home.

## Why the sweep was necessary

`AGENTS.md` states that the entire shell surface is isolated in three frontend files.
For `invoke` that is exactly true (`01-ipc-surface.md`). For *literals* it is not: every
contract below lives outside those three files, and most are self-documented with a
"mirrors …" comment — which is what makes them findable at all.

---

### XLANG-001 The PR review finding format is a three-way contract
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs` (producer) · `src/CodeFlow.App/Review/ReviewMemory.cs` (sidecar parser) · `renderer/src/lib/parseAnalysis.ts` (TypeScript parser)
**Behaviour**: `DEFAULT_PR_REVIEW_STANDARD` instructs the model to emit findings in a Spanish,
emoji-keyed markdown format. Two independent parsers consume it — one in the sidecar for review-memory
reconciliation, one in TypeScript for rendering. All three must agree or a review silently
produces zero findings.

The matching regexes are character-identical across the two languages:

`
^###\s*(🚨|⚠️|ℹ️)\s*\[([^·\]]+)·([^\]]+)\]\s*([^·]+)·\s*(F-\d+)\s*$
📍\s*Ubicaci[oó]n:\s*([^\n]+)
🎯\s*Confianza:\s*(\d+)
^📈\s*CALIDAD:\s*Fiabilidad=([A-E])\s+Seguridad=([A-E])\s+Mantenibilidad=([A-E])\s*$
`

**The severity comes from the word, not the emoji.** The header carries it twice — one of five words
inside the brackets, and one of three emoji the prompt asks be derived from it — and both parsers
used to read only the emoji and discard the word. When the model wrote
`### 🚨 [Mayor · Security Hotspot]`, against the mapping its own prompt gives it, two `Mayor`
findings were stored and rendered as `critical` and the Quality Gate went red for them. Observed on
this repository's pull request #60.

| word | severity |
|---|---|
| `Blocker`, `Crítico` / `Critico` | `critical` |
| `Mayor` | `warning` |
| `Menor`, `Info` | `info` |
| anything else | falls back to the emoji |

The emoji fallback keeps 1.7.2's drifted vocabulary (`Alta`, `Media`) parsing exactly as before.
`ReviewMemory.SeverityOf` and `parseAnalysis.ts`'s `severityOf` hold the same table and change
together; the inverse map that picks an emoji when reposting a finding (`parseAnalysis.ts:134-136`)
is unchanged and now receives a corrected severity.

**Inputs / outputs**: A finding block is `### {emoji} [{Severidad} · {Tipo}] {Categoría} · F-{NNN}`,
followed by a subtitle line, then `📍 Ubicación: {file}:{lines}` and `🎯 Confianza: {0-100}`.
The subtitle is inferred positionally — the first non-empty line after the header and before
`📍`/`💭`.
**Edge cases**: `Ubicaci[oó]n` accepts both the accented and unaccented spelling on both sides.
**Frontend dependency**: `renderer/src/lib/parseAnalysis.ts`, which every review-rendering component uses.
**Markers**: `VERBATIM` — and the reason `AGENTS.md`'s English-only rule is exempted for
prompt text. Translating any of this changes what the model emits and breaks both parsers at
once, and every stored `review_runs` row becomes unparseable.

**The exemption is narrower than "the prompts are Spanish".** Since 1.9.x the review prompts'
*instructions* are English, like the rest of the codebase. What the exemption covers is everything
the model is told to **emit** and everything a parser matches on — the four regexes above, the
severity words (`Blocker`/`Crítico`/`Mayor`/`Menor`/`Info`), the type words
(`Bug`/`Vulnerabilidad`/`Code Smell`/`Security Hotspot`), the `## NIVEL DE REVISIÓN ACTIVO:` header
(`AI-022`), the `💭 Por qué` / `💡 Sugerencia` / `🛠️ Ejemplo de solución` labels, and the standing
order to answer in Spanish, which is what keeps a stored review readable by the person who asked for
it. Those stay byte for byte. The line that tells the model *why* it is reading a diff does not.

Note also that `AGENTS.md` describes PR review as returning "a single JSON object with
`summary`, `outcome` and a `findings[]` array". The implementation does not do that. This
markdown is the real contract; the brief is wrong on this point (`90-ambiguities.md`).

---

### XLANG-002 The checkout-conflict error prefix
**Implementation**: `src/CodeFlow.App/Git/Branches.cs` · `renderer/src/state/repoStore.ts`
**Behaviour**: A checkout blocked by local changes returns an error string prefixed with a
sentinel so the frontend can offer to stash instead of showing a failure banner.

`
CHECKOUT_CONFLICT:
`
(with a trailing space — `"CHECKOUT_CONFLICT: "`)

**Inputs / outputs**: `src/CodeFlow.App/Git/Branches.cs` formats `{PREFIX}{libgit2 message}`. The frontend
checks `string(e).includes(CHECKOUT_CONFLICT_PREFIX)` and rethrows anything else.
**Frontend dependency**: `src/state/repoStore.ts:130`.
**Markers**: `VERBATIM`. This is an error *string* used as a typed error. Rewording it — even
changing the trailing space — turns a recoverable conflict into an unhandled failure.

---

### XLANG-003 The AI run markers
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs` · `src/CodeFlow.App/Ai/AiRunRegistry.cs` · `renderer/src/lib/claudeError.ts` · `renderer/src/state/aiRunStore.ts`
**Behaviour**: Three sentinel prefixes classify an error string as it crosses the IPC boundary,
so the frontend can render a dedicated notice rather than a red failure banner.

`
QUOTA_EXCEEDED::     // `src/CodeFlow.App/Ai/AiOperations.cs`          ↔ src/lib/claudeError.ts:1
RUN_CANCELLED::      // `src/CodeFlow.App/Ai/AiRunRegistry.cs`     ↔ src/state/aiRunStore.ts
RUN_TIMED_OUT::      // `src/CodeFlow.App/Ai/AiRunRegistry.cs`     ↔ src/state/aiRunStore.ts
`

`RUN_TIMED_OUT::` is the run's own deadline expiring (`AiRunRegistry.DefaultRunTimeout`, ten
minutes), and is kept apart from `RUN_CANCELLED::` because the two say opposite things to the
person reading the panel: one is "you stopped this", the other is "this never finished on its own".
It carries the deadline in whole minutes after the marker, or nothing when the deadline is under a
minute (only a test's), in which case `AiErrorBanner` uses wording that names no duration.

**Inputs / outputs**: `src/CodeFlow.App/Ai/AiOperations.cs` tags a limit/billing refusal by prefixing `QUOTA_MARKER`,
and is careful not to double-prefix an already-tagged error. `claudeError.ts:22-35` then splits
the remainder into `usage` vs `billing` using its own signal list
(`"insufficient balance"`, `"insufficient credit"`, `"out of credit"`, `"payment required"`,
`"billing"`), extracts a `(\d+)\s*(hours?|hrs?|minutes?|mins?)` reset hint and the first
`https?://` URL with trailing punctuation stripped.
**Edge cases**: the frontend uses `includes`, not `startsWith`, so a marker anywhere in the
string triggers it.
**Markers**: `VERBATIM`. The billing-signal list is frontend-only and has no the sidecar counterpart —
it is not a duplicated literal, but it is a downstream dependency on the untagged remainder.

---

### XLANG-004 The AI task keys and the settings-key templates
**Implementation**: `src/CodeFlow.App/Ai/AiCommands.cs` (`AiTask.key()`) · `renderer/src/lib/aiTasks.ts`
**Behaviour**: Eight task keys form the settings namespace for per-task AI routing. The
frontend declares them independently and its own comment states they "must match the sidecar's
`AiTask.key()`".

`
chat  commit  analyze  review  pr_description  fix  conflict  inline
`

Two key templates are built from them, on both sides:

`
ai_provider_{task}          // which provider handles this task; blank = inherit the global default
{provider}_{task}_model     // per-task model override;          blank = that provider's base model
`

**Edge cases**: `fix` is marked `agenticOnly` in the frontend, which hides non-agentic providers
from that row. There is no corresponding backend guard — the constraint is frontend-only.
**Frontend dependency**: `renderer/src/lib/aiTasks.ts`, `src/components/settings/TaskRouting.tsx`.
**Markers**: `VERBATIM`. A renamed key silently orphans a user's stored routing.

---

### XLANG-005 The provider and model resolution cascade
**Implementation**: `src/CodeFlow.App/Ai/AiCommands.cs` (`provider_for`, `load_ai_config`) · `renderer/src/state/aiProviderStore.ts` (`loadRouting`)
**Behaviour**: The frontend re-implements the backend's resolution chain so the settings UI can
show which provider and model a task will actually use. Its own comment states the intent —
"mirroring the backend's fallback chain so the UI can't disagree with what actually runs".

Backend, for a given task:

| Step | Provider | Model |
|---|---|---|
| 1 | `ai_provider_{task}`, blank counts as unset | `{provider}_{task}_model`, blank counts as unset |
| 2 | the global active provider | **for `commit` only**: `engine.commit_message_model()`, used only when non-empty |
| 3 | — | `{provider}_model` |

Binary and tools resolve alongside: `{provider}_binary_path` → `engine.default_binary()`, and
`{provider}_allowed_tools` split on `,` with blanks dropped.

**Markers**: `BUG-XLANG-a`

> **BUG-XLANG-a** — The frontend cascade omits step 2. `loadRouting`
> (`src/state/aiProviderStore.ts:52-57`) resolves the model as
> `{provider}_{task}_model` → `{provider}_model` → `""`, with no equivalent of the
> `commit_message_model()` step. For the `commit` task with no per-task override, the backend
> runs `claude-haiku-4-5-20251001` (`src/CodeFlow.App/Ai/Engines/Claude.cs`) while the settings UI displays the base
> model. The divergence is limited to the `claude` provider — `src/CodeFlow.App/Ai/Engines/Codex.cs`, `src/CodeFlow.App/Ai/Engines/Gemini.cs` and
> `src/CodeFlow.App/Ai/Engines/OpenCode.cs` all define `COMMIT_MESSAGE_MODEL` as `""`, and `ollama`/`openai` return `""`
> by design because the right cheap model depends on the endpoint. Suspected-correct behaviour
> is for the UI to show the effective model. **Ported as-is** — the UI's displayed value is not
> used to drive the run, so reproducing the divergence is harmless, and "fixing" it would change
> what an existing user sees without being asked.

---

### XLANG-006 Default binary per provider
**Implementation**: `src/CodeFlow.App/Ai/Engines/Claude.cs`, `src/CodeFlow.App/Ai/Engines/Codex.cs`, `src/CodeFlow.App/Ai/Engines/Gemini.cs`, `src/CodeFlow.App/Ai/Engines/OpenCode.cs`, `src/CodeFlow.App/Ai/Engines/OpenAi.cs`, `src/CodeFlow.App/Ai/Engines/Ollama.cs` · `renderer/src/lib/aiProviders.ts`
**Behaviour**: The Settings screen shows the default binary name (or endpoint) when the user has
not set a path. Both sides currently agree exactly:

| Provider id | the sidecar `default_binary()` | TS `defaultBinary` |
|---|---|---|
| `claude` | `claude` | `claude` |
| `gemini` | `agy` | `agy` |
| `codex` | `codex` | `codex` |
| `opencode` | `opencode` | `opencode` |
| `ollama` | `http://localhost:11434` | `http://localhost:11434` |
| `openai` | `https://api.openai.com/v1` | `https://api.openai.com/v1` |

Note that the `gemini` provider id maps to the `agy` (Antigravity) binary, not to a
`gemini` executable. `AGENTS.md` describes this engine's flags incorrectly
(`90-ambiguities.md`); `05-ai-engines.md` documents what it actually does.
**Markers**: `VERBATIM`.

---

### XLANG-007 The bundled static model lists live in the frontend
**Implementation**: `renderer/src/lib/aiProviders.ts`
**Behaviour**: Model discovery has three strategies — native command, API catalogue, and a
bundled static list. The **static list is frontend data**, not backend data: there is no the sidecar
counterpart to reconcile against. It is recorded here because a reader looking for "where the
fallback model list lives" will otherwise search the C# core and find nothing.
**Markers**: none — this is a one-sided literal, listed to prevent a false search.

---

### XLANG-008 The advertised Accept-Encoding
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs` · `renderer/src/lib/api/send.ts`
**Behaviour**: The API client's request preview shows the implicit headers the backend will add.
`Accept-Encoding` is duplicated so the preview does not require a round trip.

`
gzip, br, deflate
`

**Edge cases**: the frontend only lists it when the user has not set the header themselves
(`hasHeader` filter at `send.ts:162`); the backend only adds it under the same condition
(`src/CodeFlow.App/ApiClient/HttpSend.cs`).
**Markers**: `VERBATIM`. A mismatch makes the preview lie about what is actually sent.

---

### XLANG-009 The extension-to-MIME table
**Implementation**: `src/CodeFlow.App/ApiClient/ApiCommands.cs` (`guess_mime`) · `renderer/src/components/api/BodyPanel.tsx` (`MIME_BY_EXTENSION`)
**Behaviour**: Duplicated deliberately — the frontend's own comment explains that asking the
backend would cost a whole file read, because `api_read_file_base64` is the only command that
reports a MIME and it returns the bytes with it.

| Extension(s) | MIME |
|---|---|
| `json` | `application/json` |
| `xml` | `application/xml` |
| `html`, `htm` | `text/html` |
| `csv` | `text/csv` |
| `txt`, `log`, `md` | `text/plain` |
| `pdf` | `application/pdf` |
| `png` | `image/png` |
| `jpg`, `jpeg` | `image/jpeg` |
| `gif` | `image/gif` |
| `webp` | `image/webp` |
| `zip` | `application/zip` |
| anything else | `application/octet-stream` (the sidecar only) |

**Edge cases**: the extension is lowercased before lookup on the sidecar. The TypeScript map
has no default entry — the fallback is handled by its caller, so the two are equivalent in
effect but not in shape.
**Markers**: `VERBATIM`.

---

### XLANG-010 Structural type mirroring
**Implementation**: `src/CodeFlow.App/ApiClient/ApiModels.cs` and `src/CodeFlow.App/Workspaces/WorkspaceModels.cs` (both carry explicit "mirrored one-for-one"
comments) · `src/types/api.ts` (71 exported types) · `src/types/domain.ts` (43 exported types)
**Behaviour**: The API client's ~20 wire-contract types and the `api_*` database row types are
mirrored field-for-field in TypeScript, with the field *names* forming the contract — the shell
serialises them directly. `src/types/domain.ts:173` similarly mirrors the the sidecar `MemoryFinding`.
**Edge cases**: this is a naming contract, not a value contract. Any `serde` rename attribute in
the sidecar is part of it and must be reproduced.
**Markers**: `VERBATIM` (field names). Field-by-field detail belongs to `08-api-client.md`,
`03-storage.md` and `07-review-pipeline.md`; this entry exists so the reader knows the mirroring
is declared, not incidental.

---

### XLANG-011 The API tree cascade delete is reimplemented client-side
**Implementation**: SQLite `ON DELETE CASCADE` in the `api_*` schema · `renderer/src/state/apiTreeStore.ts`
**Behaviour**: The database cascades deletes across collections → folders → requests. The
frontend re-implements the same cascade in memory so a delete does not require reloading the
whole tree. If the schema's cascade rules change, the in-memory version silently diverges and
the UI shows rows that no longer exist.
**Markers**: `VERBATIM` (the cascade *shape*, not a literal string). The renderer half is pinned
by `renderer/src/state/apiTreeStore.test.ts`, including the detach-not-close handoff to
`apiTabsStore`.

---

### XLANG-012 The refused-credential error prefix
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs` (`AzureException.RefusedPrefix`) · `renderer/src/state/prStore.ts`
**Behaviour**: An Azure DevOps call refused for the credential — `401` or `403` — returns an error
string prefixed with a sentinel, so the frontend can offer "replace the token" instead of a Retry
that will fail identically.

`
CREDENTIAL_REFUSED:
`
(with a trailing space — `"CREDENTIAL_REFUSED: "`)

**Inputs / outputs**: the `list_pull_requests` handler formats `{PREFIX}{the message the client always
produced}`, so the original text survives behind the prefix. `prStore` checks
`string(e).includes(CREDENTIAL_REFUSED_PREFIX)`, strips it for display, and raises a flag the sidebar
reads.

**Applied at the command boundary, not at the throw site**, and the difference is visible: putting it
on every Azure error left `CREDENTIAL_REFUSED: ` sitting inside the review-posting failure summary,
which is a sentence a person reads. In-process callers branch on `AzureException.Unauthorized`
instead; `resolve_pr_link` uses that to answer `PrLinkResolution.Expired` and never touches the
string. Three existing tests caught the leak, and pass unchanged now.
**A second producer, same literal, same UI path**: `CredentialStoreException.RefusedPrefix`
(`src/CodeFlow.App/Security/CredentialStore.cs`). macOS binds a keychain item's ACL to the binary
that created it, and CodeFlow ships ad-hoc signed with `electronFuses.resetAdHocDarwinSignature`
rewriting that signature on every build — so an update can leave the app unable to read the tokens
its own previous build stored. `MacKeychain.Check` maps `errSecAuthFailed (-25293)`,
`errSecInteractionNotAllowed (-25308)` and `errSecUserCanceled (-128)` to this prefix plus a
sentence naming the way out. Reusing the literal rather than inventing a second sentinel is
deliberate: to the person reading the screen, a keychain refusal and a host's 401 are the same
situation — a saved credential that is not working — and `prStore` already turns this into the
"reconnect this account" state. The two constants are byte-identical and
`CredentialStoreTests.A_refused_read_is_reported_as_something_the_user_can_act_on` pins it.
**Frontend dependency**: `src/components/layout/sidebar/PullRequestsSection.tsx` (the PR-list error block).
**Markers**: `DIVERGENCE-PROV-b`, `VERBATIM`. **New in the port** — 1.7.2 has no such prefix
and no status-code branch at all (`src/CodeFlow.App/Providers/Azure/AzureClient.cs` repeats `if !status.is_success()` at six call sites). Added
because `AGENTS.md` requires PAT expiry to be "an expected state with its own UI path, never a
generic network error", and because the transport carries a string and nothing else — the same reason
`XLANG-002`, `XLANG-003` exist. Rewording it, trailing space included, turns a recoverable state back
into an unhandled failure.

---

### XLANG-013 The self-approval error prefix, and the GitHub sentence behind it
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs` (`GitHubException.SelfApprovalPrefix`) · `renderer/src/state/prStore.ts`
**Behaviour**: GitHub answers `422` when the reviewer is the pull request's own author. That call
returns an error string prefixed with a sentinel, so the frontend can say what happened instead of
showing the API's JSON error envelope.

`
SELF_APPROVAL:
`
(with a trailing space — `"SELF_APPROVAL: "`)

**Inputs / outputs**: `act_on_pull_request` and `act_on_pr_link` format `{PREFIX}{the message the
client always produced}`. `prStore.actOnPr` checks `string(e).includes(SELF_APPROVAL_PREFIX)` and
**replaces** the message with a translated sentence rather than stripping the prefix off it — unlike
`XLANG-012`, where the original text ("the host said 401") still told the user something. Here it is
GitHub's error envelope, which tells them nothing they can act on.

**This entry owns a second literal, and it is not ours**:

`
Can not approve your own pull request
`

That is GitHub's wording, missing space included — `"Can not"`, not `"Cannot"` — and it appears in the
`errors[]` array of the 422 body. The status alone cannot identify this case: `422` is GitHub's answer
to every validation failure, including a `REQUEST_CHANGES` with an empty body, so the sentence has to
be matched too. It is matched case-insensitively, which absorbs the cheapest way it could drift.
**GitHub can reword it without telling anyone**, and if they do this degrades to the raw 422 that
every other validation failure already shows — a graceful failure, but a silent one, so this entry is
where to look when the friendly message stops appearing.

**Applied at the command boundary, not at the throw site**, for the reason `XLANG-012` gives at
length. In-process callers branch on `GitHubException.SelfApproval` instead.
**Frontend dependency**: `src/components/ai/AiPanel.tsx` (the Approve button, disabled when
`isOwnGithubAuthor` matches the PR author against the saved connections' logins — so in normal use
this error is never reached, and the prefix is the backstop for the cases the check cannot see, such
as a login saved before usernames were recorded).
**Markers**: `DIVERGENCE-PROV-c`, `VERBATIM`. **New in the port** — 1.7.2 has no status-code
branch in `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs` either, so a self-approval, an expired token and a missing repository all read
alike there. Added because the operator met the raw JSON in a toast and asked for it by name: it is a
state every solo maintainer reaches on every pull request they open, and no retry or credential
changes it.

---

### XLANG-014 The stale-review error prefix
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubHost.cs` (`StaleReviewPrefix`) · `renderer/src/state/prStore.ts`
**Behaviour**: A findings batch whose anchors were computed against a commit that is no longer the
pull request's head is refused, and the error carries a sentinel so the frontend can say "review it
again" instead of offering a Retry that will refuse identically.

`
STALE_REVIEW:
`
(with a trailing space — `"STALE_REVIEW: "`)

**Inputs / outputs**: `GitHubHost.PublishFindingsAsync` throws `{PREFIX}{a sentence naming both
abbreviated SHAs}`. `prStore.postReview` checks for the prefix, strips it, and shows the message as
written — unlike `XLANG-013`, the text here is worth reading: it names what changed and what to do.

**Markers**: `BUG-REVIEW-a` (closed). **New in the port.** CodeFlow 1.7.2 posts the batch regardless,
so findings land on whatever now occupies those line numbers with nothing marking them misplaced.
Refusing rather than warning is deliberate: a comment on the wrong line reads as a reviewer who did
not understand the code, and removing it means deleting each one by hand.

**Only GitHub raises it.** Azure DevOps anchors by iteration id rather than by commit and has no SHA
to compare, so its half of `BUG-REVIEW-a` is still open and is marked as such in `AzureHost`.

---

### XLANG-015 The nothing-to-analyse error prefix
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs` (`NothingToAnalyzePrefix`) · `src/CodeFlow.App/Ai/AiTurn.cs` · `renderer/src/lib/analyzeRefusal.ts` · `renderer/src/state/jobsStore.ts`
**Behaviour**: A pre-commit analysis of a clean working tree is refused before the model is invoked,
and the error carries a sentinel so the frontend shows an empty state rather than a failure banner.

`
NOTHING_TO_ANALYZE:
`
(with a trailing space — `"NOTHING_TO_ANALYZE: "`)

**Inputs / outputs**: `AiOperations.AnalyzeChangesAsync` throws
`{PREFIX}No hay cambios sin commitear para analizar` — the sentence `AI-024` already documented,
with the marker in front. `AiTurn.AnalyzeWorkingChangesAsync` skips the `job_history` write for that
prefix exactly as it does for `AiRunRegistry.CancelledMarker`. `analyzeRefusal.isRefusal` checks
`startsWith` and `AnalyzeSection` renders `analyze.nothingToAnalyze` instead of `AiErrorBanner`.

**The prefix has to survive the transport to be checkable at all.** `jobsStore.run` files the
error's own `message`; it used to file `String(error)`, which prepends `"Error: "` and moved the
marker off the start of the string. `startsWith` then failed and the raw sentinel was rendered on
screen as a red banner — while the identical refusal reloaded from `job_history`, stored without
that prefix, still read as the empty state. Every sentinel in this document that is matched with
`startsWith` depends on that one normalisation; `RUN_CANCELLED::` and `QUOTA_EXCEEDED::` survived it
only because they are matched with `includes`.

**Two guards, on purpose.** The renderer checks `uncommittedCount(status)` first and never starts a
job at all, which is what keeps the entry out of Activity — the job row is created on that side. The
sidecar's refusal covers the tree committed between that check and the call.

**Why the row went away.** `AI-024` used to file the refusal as an ordinary failed run, and that was
right while reaching it needed a deliberate click. The analyze tab now starts a run when it is
merely *opened*, so on a clean tree the old behaviour left a permanent red row for a request nobody
made. History is for things that happened.

**Markers**: `VERBATIM` (the prefix). Electron's own
`Error invoking remote method 'codeflow:invoke'` wrapper used to reach the screen along with it;
that is stripped at the bridge (`renderer/src/lib/bridge/host.ts`) and is not part of this contract.

---

### XLANG-016 The acceptance-criteria verdict block
**Implementation**: `src/CodeFlow.App/Ai/Prompts/DEFAULT_TICKET_REVIEW_STANDARD.txt` · `src/CodeFlow.App/Tickets/TicketVerdict.cs` · `renderer/src/lib/parseTicketVerdict.ts`
**Behaviour**: a ticket review closes with two sections whose headers and field labels are payload,
not prose. Two parsers match on them, one per language, and the prompt is what makes the model emit
them.

```
## VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN

### AC-{n}: {texto del criterio}
Veredicto: cumple | no cumple | parcial | no verificable
Evidencia: {ruta}:{líneas} — {por qué} | sin evidencia en el diff
🎯 Confianza: {0-100}/100

## VEREDICTO DE COBERTURA

Cobertura: completa | incompleta | no verificable
Faltante: …
Fuera de alcance: …
Resumen: …
```

**Inputs / outputs**: `TicketVerdict.Parse` returns the criteria table and the coverage block, or
`null`; `parseTicketVerdict` does the same in the renderer. Both are **tolerant**: a missing or
malformed section loses the verdict, never the answer, and the review renders as an ordinary
analysis.

**It cannot collide with `XLANG-001`, twice over.** `ReviewMemory.ParseFindings` and
`parseAnalysis.ts` only recognise a `###` header carrying one of three emoji, a bracketed severity
and an `F-NNN` id — `### AC-1:` has none of them, so it reads as prose to both. On top of that,
`TicketVerdict.Split` / `splitTicketReview` cut the text at the criteria heading and hand the finding
parsers the head only, which also keeps the criteria table out of `parseAnalysis`'s `summary`
fallback on an answer with no findings.
`TicketVerdictTests.ParseFindings_reads_the_same_findings_with_or_without_the_verdict_section`
asserts the first defence on the *unsplit* text, so it proves the second one is belt and braces.

**Four verdicts, and the unreadable one is `no verificable`.** Both normalisers map anything they
cannot read onto `no verificable` rather than onto `cumple`: the prompt's own standing order is to
prefer a false alarm to approving incomplete work, and a verdict that would not parse is not
evidence that a criterion was met.

**Markers**: `VERBATIM` (both headers, the four verdict words, the three coverage words and the
labels `Veredicto:` / `Evidencia:` / `Cobertura:` / `Faltante:` / `Fuera de alcance:` / `Resumen:`).
The accents are part of them; both parsers accept a dropped one on the two `##` headers only, the
same allowance `parseAnalysis.ts` makes for `Ubicacion`.

---

### XLANG-017 The ticket-review refusal prefixes
**Implementation**: `src/CodeFlow.App/Tickets/TicketReview.cs` (`NotLinkedPrefix`, `SyncFailedPrefix`) · `renderer/src/lib/analyzeRefusal.ts`
**Behaviour**: two sentinels in the family of `NOTHING_TO_ANALYZE: `, both with a trailing space.

`TICKET_NOT_LINKED: ` — the branch has no ticket. A **state**, not a failure: the section shows how
to link one, and the row does not stand in for the branch's last real review
(`analyzeRefusal.isTicketRefusal`).

`TICKET_SYNC_FAILED: ` — the work item could not be read **and** nothing usable was cached. This one
is a genuine failure and is reported as one. It is raised only when both halves are true: a fetch
that fails over a cache holding the work item runs the review anyway and says how old the copy is,
because refusing would also withhold the finding half of the answer, which never needed the network.

**Inputs / outputs**: `TicketReview.RunAsync` throws `AiRunFailedException` with the prefix in front
of a Spanish sentence. `isTicketRefusal` matches `TICKET_NOT_LINKED: ` and `NOTHING_TO_ANALYZE: `
with `startsWith` and deliberately **not** `TICKET_SYNC_FAILED: ` — hiding a sync failure behind a
calm empty state is how a review silently stops running.

**Markers**: `VERBATIM` (both prefixes). They depend on the same `jobsStore.run` normalisation
`XLANG-015` documents.

---

## Where the review contract comes from

Four places carry rules taken from the review runbook this engine was ported from —
`src/CodeFlow.App/Review/ReviewMemory.cs` (the re-review reconciliation rules), `src/CodeFlow.App/Ai/AiOperations.cs` (the SonarQube-style
taxonomy, A-E ratings and Quality Gate behind `DEFAULT_PR_REVIEW_STANDARD`), `src/CodeFlow.App/Ai/AiOperations.cs` (the
review-level rules governing which severities survive) and `src/state/prStore.ts:37` (the review
depth levels, default `completo`).

That runbook was consulted while porting and is not part of this repository. What matters here is
already settled: the taxonomy and the three level names in `Ai/Prompts/` were transcribed from it,
which makes those literals **authoritative rather than inferred** — they are a contract to preserve,
not a guess to revisit. Everything needed to keep them correct is in this document.

It is **not** copied into this repository: its `reviews/` directory holds real review history for
client projects.

What it settled is recorded in `90-ambiguities.md`: `AMBIGUOUS-REVIEW-a` is answered (the id drift
was never intended — the document's engine assigns ids and renders the report, so it cannot happen
there), and `AMBIGUOUS-REVIEW-b` is **not** answered by it, because it records no per-finding level
either. That one was closed as an operator decision instead.

## Markers raised

| Local id | Kind | Summary |
|---|---|---|
| `BUG-XLANG-a` | BUG | The frontend's routing cascade omits the commit-model step, so the settings UI shows a different model than the one the `claude` provider actually runs for commit messages. |
