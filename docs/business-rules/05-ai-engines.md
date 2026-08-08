# 05 — AI engines

## Scope

- `src/CodeFlow.App/Ai/` — `AiOperations.cs`, `AiEngineRunner.cs`, `AiRunRegistry.cs`, `AiRunContext.cs`,
  `BinaryDiscovery.cs`, `QuotaSignals.cs`, `Prompts.cs`, `AiRouting.cs`, `ModelDiscovery.cs`
- `src/CodeFlow.App/Ai/Engines/` — `Claude.cs`, `Codex.cs`, `Gemini.cs`, `OpenCode.cs`, `OpenAi.cs`, `Ollama.cs`
- `src/CodeFlow.App/Ai/AiCommands.cs` — the provider-neutral command layer

`AiOperations.cs` is the provider-neutral core: the run plumbing, binary/PATH discovery, quota
detection and the nine prompt templates. `AiRunRegistry.cs` is the live-output and cancellation
plumbing shared by every AI run. Each file under `Engines/` owns exactly one `IAiEngine`
implementation: how that CLI/API is invoked and how its output is interpreted. `AiCommands.cs`
resolves the active engine from settings and dispatches, so switching Claude ⇆ Gemini ⇆ Codex ⇆
opencode ⇆ OpenAI ⇆ Ollama is a settings change, not a code change. The PR-review command itself
(`review_pull_request`) lives in `src/CodeFlow.App/Review/ReviewCommands.cs`, owned by
`07-review-pipeline.md`.

## Commands

Contract (parameters, return types, injected state) is `01-ipc-surface.md`'s
`src/CodeFlow.App/Ai/AiCommands.cs` table. One line each, in registration order:

- `generate_commit_message` — drafts a commit message from the staged diff on the `Commit` task's engine/model.
- `cancel_ai_run` — signals cancellation for a tracked run id; `false` means it had already finished.
- `list_ai_models` — lists a provider's actually-available models (CLI subcommand, cache file, or HTTP catalogue).
- `check_ai_provider` — Settings "available / not found" probe for one provider.
- `resolve_conflict_with_ai` — proposes a merged file from a conflict's base/ours/theirs versions.
- `default_commit_template` — returns `DEFAULT_COMMIT_TEMPLATE` verbatim.
- `default_review_template` — returns `DEFAULT_REVIEW_PROMPT` verbatim.
- `default_analyze_template` — returns `DEFAULT_ANALYZE_TEMPLATE` verbatim.
- `default_pr_description_template` — returns `DEFAULT_PR_DESCRIPTION_TEMPLATE` verbatim.
- `default_resolve_conflict_template` — returns `DEFAULT_RESOLVE_CONFLICT_TEMPLATE` verbatim.
- `analyze_working_changes` — scans the working-directory diff (unstaged + untracked) for issues before commit; the job id doubles as the run id.
- `resolve_finding_with_ai` — applies one review/analysis finding's fix directly to the working tree.
- `send_chat_message` — one turn of the open-ended repo chat; resumes the engine's session when possible.
- `inline_edit_with_ai` — rewrites an editor selection per a natural-language instruction; text in, text out, no tools.

There is no `default_chat_system_prompt`/`default_inline_edit_prompt`/`default_pr_review_standard`
command in this file — `DEFAULT_CHAT_SYSTEM_PROMPT` and `FIX_FINDING_SYSTEM_PROMPT` are
private (`const`, not `pub const`) and only ever appended server-side via
`AiInvocation.system_prompt`; `DEFAULT_INLINE_EDIT_PROMPT` is `pub` but likewise has no
`default_*_template` command exposing it — `inline_edit` (`src/CodeFlow.App/Ai/AiOperations.cs`) always sends it,
with no per-workspace override path in this file. `DEFAULT_PR_REVIEW_STANDARD` has no
command here either; its seeding into a workspace's editable review-standard prompt is
outside this file's scope (see `07-review-pipeline.md`).

## The engine abstraction

Every AI feature is expressed as one `AiInvocation` (`src/CodeFlow.App/Ai/AiOperations.cs`) — a provider-neutral
description of a single headless call:

- `prompt: string` — the "ask", passed as an argument (most engines' `-p`/positional).
- `system_prompt: string?` — extra instructions appended for this run.
- `model: string` — model id to force; empty means "let the CLI/API pick its own default".
- `allowed_tools: IReadOnlyList<string>` — raw, provider-specific tool names.
- `cwd: string?` — working directory.
- `mcp_config_path: string?` — path to a `--mcp-config`-style JSON file.
- `stdin_content: string` — the *data* (diff, PR context, finding, conflict versions, …).
- `resume_session_id: string?` — session/conversation id to resume.
- `auto_approve_edits: bool` — semantic "auto-approve file create/edit tools"; every engine
  maps it to its own permission concept, because headless runs have no TTY to answer an
  interactive permission prompt.

`AiInvocation.new(prompt, stdin_content)` (`src/CodeFlow.App/Ai/AiOperations.cs`) builds the minimal form with every
optional field off; callers set only what they need. The prompt/stdin split — "the ask on
argv, the data on stdin" — is deliberate and load-bearing: it is why the same provider-neutral
templates in `src/CodeFlow.App/Ai/AiOperations.cs` work unmodified across every engine's argv-length and shell-escaping
constraints, and why `stdin_payload()` exists as a per-engine override point. Three engines
override it: `CodexEngine` (`src/CodeFlow.App/Ai/Engines/Codex.cs`) sends the whole brief, because
`codex exec` genuinely reads stdin as "additional context"; `Gemini` and `OpenCode` return **empty**,
because their CLIs never read stdin and their brief already carries the input. An empty payload is
the declaration "this CLI does not read stdin", and AI-054 depends on it being accurate.

`Transport` (`src/CodeFlow.App/Ai/AiOperations.cs`) is how an engine actually reaches its model:

- `Subprocess` — the default; a headless CLI child process (Claude, Codex, Gemini/agy, opencode).
- `Ollama` — a local HTTP server, no credential.
- `OpenAiCompatible { api_key: string }` — any `/v1/chat/completions`-shaped endpoint.

`run()`, `list_models()`, `probe()` and `engine_version()` all branch on `transport()` before
doing anything subprocess-specific (binary resolution, `PATH`, stdio pipes); the two `Http`-ish
variants short-circuit into `ollama`/`openai` functions and never reach
`build_command`/`interpret` (both engines' implementations of those two trait methods exist
only to satisfy the trait and are asserted unreachable in their doc comments — `src/CodeFlow.App/Ai/Engines/Ollama.cs`,
`src/CodeFlow.App/Ai/Engines/OpenAi.cs`).

`engine_for(provider: string)` (`src/CodeFlow.App/Ai/AiOperations.cs`) is the single dispatch point from a stored
provider id string to a `Box<dyn AiEngine>`: `"gemini"` → `GeminiEngine`, `"opencode"` →
`OpenCodeEngine`, `"codex"` → `CodexEngine`, `"ollama"` or `"local"` → `OllamaEngine`,
`"openai"` → `OpenAiEngine` (constructed with its API key read from the OS keyring right
here, via `CredentialStore.AiApiKey`("openai")`, so the key rides along on the transport and
no operation signature needs an `api_key` parameter), and **everything else — including an
empty or unrecognised string — falls back to `ClaudeEngine`**. This fallback is what
guarantees a corrupt or missing `ai_provider` setting never leaves the app with no working
engine.

## Binary discovery

Every subprocess engine resolves its binary the same way, in `src/CodeFlow.App/Ai/AiOperations.cs`, before `run()`,
`capture()` (auxiliary CLI calls like `--version`/model listing) or `probe()` spawn anything:

1. **`install_dirs()`** (`src/CodeFlow.App/Ai/AiOperations.cs` non-Windows, 489-508 Windows) — the directories the AI
   CLI installers are known to drop binaries in, so a bare `claude`/`gemini`/`opencode` resolves
   even when the app inherited a minimal environment (a macOS GUI app launched from Finder gets
   launchd's minimal `PATH`; a Windows app already running when a CLI was installed keeps the
   stale pre-install `PATH`):
   - **Non-Windows**: `~/.local/bin`, `~/.claude/local`, `~/.opencode/bin`, `~/.bun/bin`,
     `~/Library/pnpm`, `~/.npm-global/bin`, then the fixed `/opt/homebrew/bin` and
     `/usr/local/bin`.
   - **Windows**: `%USERPROFILE%\.local\bin`, `%USERPROFILE%\.claude\local`,
     `%USERPROFILE%\.opencode\bin`, `%APPDATA%\npm` (npm's global bin — the platform directory helper()` is
     Roaming AppData), `%LOCALAPPDATA%\agy\bin` (Antigravity CLI), and
     `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin` (the Codex desktop app ships its CLI here and
     deliberately does **not** put it on `PATH`; omitting this entry means a working Codex
     install still probes as "not found").
2. **`search_dirs()`** (`src/CodeFlow.App/Ai/AiOperations.cs`) — `install_dirs()` followed by every entry already in
   the process's own `PATH` (the standard library), in that order.
3. **`apply_path()`** (`src/CodeFlow.App/Ai/AiOperations.cs`) — joins `search_dirs()` back into a single `PATH` string
   and sets it as the *child process's* `PATH` env var (`cmd.env("PATH", joined)`), so the
   spawned CLI's own subprocesses (git, node, …) see the same augmented search space.
4. **`resolve_binary()`** — turns a bare command name into what actually gets executed.
   - **Non-Windows** (`src/CodeFlow.App/Ai/AiOperations.cs`): a no-op; the child's augmented `PATH` finds it and Unix
     has no executable-extension quirk.
   - **Windows** (`src/CodeFlow.App/Ai/AiOperations.cs`): `CreateProcess` (what `ProcessStartInfo` uses) only
     auto-appends `.exe`, so a Node CLI installed as a `<name>.cmd` shim (how `opencode` and
     `agy`/Gemini land via npm) is invisible to a bare `ProcessStartInfo`("opencode")` and can't be
     executed directly anyway (it's a batch file). For each directory in `search_dirs()`, in
     order, the three extensions `exe`, `cmd`, `bat` are tried **in that priority** — so within
     one directory a real `.exe` (e.g. Claude's native installer) always wins over a `.cmd`/`.bat`
     shim there, but a directory earlier in the search order wins over a later directory
     regardless of which extension it has. The first `<dir>/<name>.<ext>` that `is_file()` wins;
     a name that already contains a path separator or has an extension is trusted as-is and
     skips this whole resolution. Resolving to the full `.cmd` path (rather than the bare name)
     is what lets `Command` route it through `cmd.exe` with correct argument escaping on the sidecar
     ≥1.77.
5. **`find_on_path()`** (`src/CodeFlow.App/Ai/AiOperations.cs`) — the same resolution logic exposed for `probe()`
   (Settings' "available / not found" badge): an absolute path or one containing a separator is
   checked as-is via `is_file()`; a bare name is searched across `search_dirs()` trying
   `exe`/`cmd`/`bat`/`""` on Windows or just `""` elsewhere. `None` means launching it would fail.

**Manual override**: an explicit `binary_path` Settings value (`{provider}_binary_path`,
resolved in `load_ai_config`/`load_ai_config_for`, `src/CodeFlow.App/Ai/AiCommands.cs`) bypasses
all of the above — it is used exactly as stored, with no directory search and no extension
resolution, exactly as `resolve_binary`'s "already has a path separator or extension" escape
hatch implies. When unset (or blank — a stored empty string counts as unset), the engine's
`default_binary()` is used instead.

## Per-engine matrix

| Engine id | Binary (default) | argv shape | stdin | Output parsing | Error vs auth vs quota | Session resume | Model discovery | Agentic |
|---|---|---|---|---|---|---|---|---|
| `claude` | `claude` | `-p <prompt> [--append-system-prompt sp] [--model m] --output-format stream-json --verbose --setting-sources user [--tools t,… --allowedTools t,…] [--permission-mode acceptEdits] [--mcp-config path --strict-mcp-config] [--resume id]` | `inv.stdin_content` piped (CLI reads it) | Last `{"type":"result",…}` line of the stream (whole-buffer JSON fallback) | `is_error` field / stdout parsed even when exit≠0 (stderr is empty on this CLI's own failures) / `quota_signal` on the result text | Native `--resume <session_id>` | None (no listing subcommand, no cache) → frontend curated list | yes |
| `codex` | `codex` | `exec [resume id] "<POINTER>" [--model m] --sandbox {read-only\|workspace-write} -c approval_policy="never" [--cd dir]` | Full brief (system+ask+INPUT) via `stdin_payload` override | stdout = final agent message only | Non-zero exit / `quota_signal` on stderr then stdout | Rollout id scraped from stderr's `session id:` preamble line; `codex exec resume <id>` | `cached_models()` reads `$CODEX_HOME/models_cache.json` (no subcommand spawned) | yes |
| `gemini` (drives `agy`, the Antigravity CLI) | `agy` | `-p "<inline brief>"` or `-p "<pointer to temp file>" --add-dir <dir>` `[--model m] [--dangerously-skip-permissions] [--continue]` | nothing: `-p` doesn't read stdin, so `stdin_payload` is empty and the input travels inside the brief (AI-054) | stdout only | Non-zero exit / `quota_signal` on stderr then stdout | No native per-conversation resume; `SESSION_SENTINEL` + `--continue` (resumes agy's globally-last conversation) | `list_models_args` = `agy models` | yes |
| `opencode` | `opencode` | `run "<pointer>" --format json [--model m] [--auto] [--dir cwd] [--session id] --file <payload path>` | nothing: not read by the CLI, so `stdin_payload` is empty and the full brief goes to the attached `--file` (AI-054) | `--format json` event stream: `text` events joined, `error` event wins | `error` event beats exit status / non-zero exit / `quota_signal` | Real `ses_…` id read from every event's `sessionID`; `--session <id>` | `list_models_args` = `opencode models` | yes |
| `openai` (and any OpenAI-compatible endpoint) | `https://api.openai.com/v1` (endpoint, not a binary) | HTTP `POST {base}/chat/completions` — `Transport.OpenAiCompatible`, no argv | n/a (HTTP body: `messages` array) | `choices[0].message.content` | HTTP status: 401/403 → key rejected, 429 → quota (`quota_signal` matches the wording), 404 → unknown model, else raw | None — every request stands alone | HTTP `GET {base}/models`, filtered by `is_chat_model`, alphabetised | no |
| `ollama`/`local` | `http://localhost:11434` (endpoint) | HTTP `POST {base}/api/chat` — `Transport.Ollama`, no argv | n/a (HTTP body: `messages` array) | `message.content` | HTTP status: 404 → model not pulled, else raw `Ollama devolvió {status}: {detail}`; no quota concept (no billing) | No server-side session; a synthetic `ollama-<uuid>` id is minted (or the caller's own reused) purely so turns group in the activity log | HTTP `GET {base}/api/tags` | no |

All six share `QUOTA_MARKER`/`quota_signal` (see below) and, for the four subprocess engines,
the exact same non-zero-exit fallback shape: `"{binary} exited with an error ({status_label}): {detail}"`,
where `detail` is the first non-empty of stderr/stdout, or a fixed "no output" sentinel
(`"sin salida en stdout ni stderr"`, Spanish, verbatim) when both streams are empty.

### Claude (`src/CodeFlow.App/Ai/Engines/Claude.cs`)

`ClaudeEngine.build_command` (`src/CodeFlow.App/Ai/Engines/Claude.cs`) always requests `--output-format stream-json
--verbose` — the *only* combination the CLI accepts alongside `-p`, chosen specifically because
it emits one JSON event per line as the run happens, which is what the app streams into the run
log (`ai:output`); plain `json` would print nothing until the process exits.
`--append-system-prompt` carries `system_prompt`; `--allowedTools` joins `allowed_tools` with
commas; `auto_approve_edits` maps to `--permission-mode acceptEdits`.

`interpret_output` (`src/CodeFlow.App/Ai/Engines/Claude.cs`) reads the **last** `{"type":"result",…}` line of stdout
(`result_payload`, `src/CodeFlow.App/Ai/Engines/Claude.cs` — scans lines in reverse, falls back to parsing the whole
buffer as one `ClaudeCliResult` for a CLI that ignored the flag or a non-streaming build). It
deliberately parses stdout **before** looking at the exit status: under `--output-format json`
the CLI reports its own failures (expired OAuth, unknown model, …) as
`{"is_error":true,"result":"<reason>"}` on stdout while leaving **stderr empty** and exiting
non-zero — branching on the exit status first would report stderr and lose the only copy of the
reason, which is the exact defect the module's tests pin down
(`surfaces_the_reason_json_carries_when_stderr_is_empty`). `model_used()` (`src/CodeFlow.App/Ai/Engines/Claude.cs`)
reports the model only when `modelUsage` has exactly one key — a run that fanned out across
several models has no single honest answer and reports `None`, so the caller falls back to the
configured setting.

`COMMIT_MESSAGE_MODEL` = `"claude-haiku-4-5-20251001"` (`src/CodeFlow.App/Ai/Engines/Claude.cs`) — commit-message
generation always runs on Haiku regardless of the configured review model, because it is
"a small, mechanical task that doesn't need a bigger model" (source comment).

### Codex (`src/CodeFlow.App/Ai/Engines/Codex.cs`)

Billing distinguishes `src/CodeFlow.App/Ai/Engines/Codex.cs` from `src/CodeFlow.App/Ai/Engines/OpenAi.cs`: this engine drives the `codex` CLI,
authenticated with a **ChatGPT subscription** (flat fee), while `src/CodeFlow.App/Ai/Engines/OpenAi.cs` pays per token
against `/v1/chat/completions` with an API key.

`codex exec` runs one task to completion headlessly, streaming progress to **stderr** and
writing only the final agent message to **stdout**. `CodexEngine.stdin_payload` (`src/CodeFlow.App/Ai/Engines/Codex.cs`)
overrides the default: it composes the whole brief (system prompt, then the fixed `POINTER`
sentence, then `----- INPUT -----` and `inv.stdin_content`) and sends *that* down stdin, because
`codex exec`'s single positional argument is a short, single-line, ASCII pointer
(`"Follow the instructions in the input piped on stdin and reply with only the requested
output."`, `src/CodeFlow.App/Ai/Engines/Codex.cs`) — kept shim-safe the same way opencode's pointer is, even though
Codex itself is a native binary; multi-line prompt templates simply never touch argv here.

`build_command` (`src/CodeFlow.App/Ai/Engines/Codex.cs`): `resume <id>` is a subcommand of `exec` and goes *before*
the prompt argument; `--sandbox` is `workspace-write` when `auto_approve_edits` else `read-only`
(the documented successor to the deprecated `--full-auto`; `danger-full-access` is never used);
`-c approval_policy="never"` is set via the `-c` config-key form rather than the (now-removed)
`--ask-for-approval` flag, because `codex exec` errors on that flag as of 0.145+ while the
config key works on every version; `--cd <dir>` sets the sandbox's workspace root in addition
to `current_dir`, because the sandbox scope and the process's actual working directory are two
separate things to Codex.

**Session id**: scraped from the stderr preamble's `session id: <uuid>` line
(`session_id_from_preamble`, `src/CodeFlow.App/Ai/Engines/Codex.cs` — matches either `session id:`/`session_id:`,
any case, leniently, because the banner is human-readable prose and not a committed format). A
preamble that omits the line yields `None`, which costs continuity (the next turn opens a fresh
session and re-sends project context) but never resumes an unrelated rollout — a deliberate
failure mode the module docs call out explicitly, contrasting it with an earlier version that
reported a fixed sentinel and silently suppressed context on every resumed turn.

**Model discovery**: `cached_models()` (`src/CodeFlow.App/Ai/Engines/Codex.cs`) reads
`$CODEX_HOME/models_cache.json` (falling back to `~/.codex`) — a catalogue the CLI refreshes on
its own — keeps only `visibility == "list"` entries, and sorts ascending by `priority`. No
subcommand is ever spawned for this; an empty or missing catalogue is folded to `None` so the
frontend's curated list applies instead of an empty picker.

### Gemini / Antigravity (`src/CodeFlow.App/Ai/Engines/Gemini.cs`)

**The module is not the `gemini` CLI.** Google retired the standalone `gemini` CLI for
consumer/subscription accounts (mid-2026) and replaced it with the **Antigravity CLI**,
invoked as `agy`, which runs Gemini 3.x plus a few Claude/GPT-OSS models against a Google
account login. `GeminiEngine` drives `agy`; the provider stays labelled "Gemini" in the UI
because that is the login/brand the user picks — `DIVERGENCE-AI-a`.

`agy -p "<prompt>"` runs one prompt non-interactively; `-p` does **not** read stdin and there is
no `--system-prompt`/`--file` flag, so the whole brief (system + ask + data, same composition
order as every other engine) must ride as the `-p` argument itself. Two delivery paths, chosen
by size against `INLINE_LIMIT = 12_000` chars (`write_brief_file_if_large`, `src/CodeFlow.App/Ai/Engines/Gemini.cs`):

- **small** (≤ 12,000 chars) — passed inline as `-p`. `agy.exe` is a native binary, so
  multi-line arguments are fine here (unlike the npm `.cmd` shims opencode/gemini installs get).
- **large** — a review diff alone can reach 120,000 chars, past the ~32k Windows argv limit.
  The brief is written to a per-call temp directory (`codeflow-agy-<uuid>/brief.txt`), the
  directory is added with `--add-dir`, and a short pointer `-p` message tells agy to read it.
  Reading it headlessly needs `--dangerously-skip-permissions` (`needs_read_permission`,
  `src/CodeFlow.App/Ai/Engines/Gemini.cs`) since there is no way to answer an approval prompt.

`--dangerously-skip-permissions` is also set whenever `auto_approve_edits` is on (chat, "fix
with AI") — agy has no granular tool-allowlist flag, so permissions are all-or-nothing
(`fix_tools()` returns an empty `Vec`).

**Session continuity is the weakest of the six engines, by CLI limitation, not by choice.**
`agy` cannot resume a specific conversation by id from `--print` mode: `--conversation <id>`
exists and does resume a specific conversation, but nothing gives a headless caller that id — it
is printed on neither stdout nor stderr, there is no `--json`/`--output-format`, and the
undocumented `~/.gemini/antigravity-cli/cache/last_conversations.json` maps a *workspace*, not a
conversation, to its most recent id (so two chats on the same project would still collide, just
as `--continue` already does). This is tracked upstream as
`google-antigravity/antigravity-cli#7`, open at the time of writing. Given that, the engine
deliberately keeps `--continue` (resumes agy's own idea of "the last run") and reports a fixed
`SESSION_SENTINEL = "agy-last"` (`src/CodeFlow.App/Ai/Engines/Gemini.cs`) as its "session id" — a string that identifies
nothing; its only job is to keep the app's chat state at "there is a session" so the next turn
passes *something*. **Two conversations open on the same project can silently resume each
other's context.** `DIVERGENCE-AI-b` — this is a known, deliberately accepted limitation, not a
bug to fix here; when upstream issue #7 lands, `--conversation <id>` replaces `--continue`.

`interpret_output` (`src/CodeFlow.App/Ai/Engines/Gemini.cs`): stdout only (any banner/status goes to stderr and is
ignored on success); `session_id` on a successful run is always `Some(SESSION_SENTINEL)`.

**Model discovery**: `list_models_args()` = `["models"]` — `agy models` prints one id per line.

### opencode (`src/CodeFlow.App/Ai/Engines/OpenCode.cs`)

Provider-agnostic by design: opencode is not tied to one vendor's login, so a model is
addressed as `provider/model` (e.g. `anthropic/claude-sonnet-4-5`) — whatever the user
configured *inside* opencode itself.

Three structural constraints shape `build_command` (`src/CodeFlow.App/Ai/Engines/OpenCode.cs`), all noted verified
against `opencode run --help` and a live run on opencode 1.18.7:

1. `opencode run` does not read piped stdin at all.
2. There is no `--system-prompt` flag — system instructions must travel with the prompt.
3. On Windows, an npm-installed `opencode.cmd` runs through `cmd.exe`, which rejects any
   argument containing a newline ("batch file arguments are invalid") — and the prompt
   templates are multi-line.

So the entire brief (system → ask → `----- INPUT -----` → data) is written to a uniquely-named
temp file (`write_payload_file`, `src/CodeFlow.App/Ai/Engines/OpenCode.cs`: `codeflow-opencode-<uuid>.txt`, deleted by
the runner once the invocation ends — `BUG-AI-a`, closed) and attached with `--file`, preceded by a short, single-line, ASCII
pointer positional argument that **must** come before `--file` (a variadic flag that would
otherwise swallow the pointer as another attachment). `--format json` is always set — not
optional — because it is the only way to recover the real session id (below). `--auto` maps
`auto_approve_edits`; `--session <id>` resumes a specific conversation and **hard-fails** with
`Session not found` (exit 1) on an id opencode no longer has, deliberately, since "a loud error
beats the wrong context" (source comment).

**Event stream** (`src/CodeFlow.App/Ai/Engines/OpenCode.cs`): every line of stdout is
`{type, timestamp, sessionID, ...data}`. `parse_events` reads two kinds — `text` (a completed
assistant text part, `part.text`, trimmed and joined with `\n` across however many arrive) and
`error` (`error.name`/`error.data.message`, combined as `"{name}: {message}"` when both are
present) — and takes the `sessionID` off the *first* event that carries one, including an
`error` event, so even a failed run can still report which session it failed in. `None` from
`parse_events` (no parseable event at all) means the caller falls back to plain-text stdout,
which is what keeps a build that ignored `--format json` working, just without a session id.

`interpret_output` (`src/CodeFlow.App/Ai/Engines/OpenCode.cs`) judges an `error` event **before** the exit status —
mirroring Claude's stdout-before-status rule — because opencode can emit one on an otherwise
zero-exit run (`an_error_event_beats_a_clean_exit_status`, a verbatim expired-Copilot-token
payload from a live run). `stale_session_hint` (`src/CodeFlow.App/Ai/Engines/OpenCode.cs`) rewrites a bare
`"Session not found"` (matched case-insensitively, anywhere in the failure detail) into a
Spanish, actionable message telling the user to start a new conversation — because the app
keeps re-sending the now-dead session id on every turn, and the raw CLI message gives no hint
why the conversation looks permanently broken.

**Model discovery**: `list_models_args()` = `["models"]` — every configured `provider/model`,
one per line.

**`fix_tools()`** (`src/CodeFlow.App/Ai/Engines/OpenCode.cs`) returns `["read","edit","write","bash","grep","glob"]`
but the source itself flags them `TODO(verify)` and notes they are **not actually passed** —
`opencode run` has no tool-allowlist flag, so write access for "fix with AI" comes entirely
from `--auto`, and `inv.allowed_tools` is set (via `apply_finding_fix`) but never read by this
engine's `build_command`. `AMBIGUOUS-AI-a`.

### OpenAI-compatible (`src/CodeFlow.App/Ai/Engines/OpenAi.cs`)

Deliberately generic: `/v1/chat/completions` is what Azure OpenAI, OpenRouter, Groq, DeepSeek,
Together, Fireworks and a local vLLM all also implement, and the base URL (default
`https://api.openai.com/v1`, `src/CodeFlow.App/Ai/Engines/OpenAi.cs`) is a free-text Settings field — pointing it at a
compatible provider is the entire configuration step. `/v1/chat/completions` is used over
OpenAI's newer Responses API specifically *because* it is the one every other provider
implements.

`complete()` (`src/CodeFlow.App/Ai/Engines/OpenAi.cs`) composes `system_prompt` (if any) → a `user` message built from
`prompt` + `\n\n` + `stdin_content` (when non-empty) → `POST {base}/chat/completions` with
`stream: false`. Two preconditions fail fast with actionable Spanish messages before any
request is sent: an empty API key ("Falta la API key. Añádela en Ajustes › Asistente de IA ›
Proveedores.") and an empty model ("Selecciona un modelo en Ajustes (por ejemplo gpt-5).").

**Error vs auth vs quota**, by HTTP status (`src/CodeFlow.App/Ai/Engines/OpenAi.cs`): `401`/`403` → "La API key fue
rechazada"; `429` → `"Rate limit / quota exceeded: {detail}"` — this exact wording is what
`QuotaSignals` matches on to raise `QUOTA_MARKER`, since `src/CodeFlow.App/Ai/Engines/OpenAi.cs` never calls
`quota_signal` itself (`mark_quota` in `src/CodeFlow.App/Ai/AiOperations.cs` applies it uniformly to every `Http`-transport
result, `src/CodeFlow.App/Ai/AiOperations.cs`); `404` → `"El modelo '{model}' no existe en este endpoint"`; anything else →
the raw status and `error_detail` (which pulls `error.message` out of an OpenAI-shaped JSON
body, falling back to the raw text for endpoints that don't follow the convention).

**Model discovery**: `fetch_models()` (`src/CodeFlow.App/Ai/Engines/OpenAi.cs`) calls `GET {base}/models`, keeps only
`is_chat_model` ids (an exclude-list of 16 known non-chat family substrings — `embedding`,
`tts`, `whisper`, `transcribe`, `dall-e`, `moderation`, `audio`, `realtime`, `image`, `sora`,
`similarity`, `-search-`, `-edit-`, `davinci`, `babbage`, `curie` — chosen over an allow-list so
a brand-new chat model id works the day it ships), and sorts alphabetically. `list_models()`
degrades to an empty `Vec` (not an error) when the key is blank or the fetch fails, so the
Settings status badge — not the picker — is what reports an unreachable/unauthorized endpoint.

Non-agentic (`agentic() -> false`) and `resumes_sessions() -> false`: a plain completion
endpoint has no tool loop and no server-side conversation state, so "fix with AI" and MCP are
hidden in the UI and every chat turn re-sends full context.

### Ollama (`src/CodeFlow.App/Ai/Engines/Ollama.cs`)

The only engine talking to a **local** server (`http://localhost:11434` by default,
`src/CodeFlow.App/Ai/Engines/Ollama.cs`) with **no credential**. `complete()` (`src/CodeFlow.App/Ai/Engines/Ollama.cs`) requires an explicit,
non-empty model on every request — Ollama has no "let the server pick" concept, so a blank
model is rejected up front with an actionable Spanish message rather than reaching the server.
Message composition mirrors `src/CodeFlow.App/Ai/Engines/OpenAi.cs` exactly (system → user = prompt + stdin).

**Error vs auth vs quota**: `404` → `"El modelo '{model}' no está disponible en Ollama.
Descárgalo con \`ollama pull {model}\`."`; a connection failure explains itself
(`"¿Está corriendo \`ollama serve\`?"`); anything else → `"Ollama devolvió {status}: {detail}"`.
There is no quota concept for a local server with no billing, so `src/CodeFlow.App/Ai/Engines/Ollama.cs` never needs
`quota_signal` (and `mark_quota` in `src/CodeFlow.App/Ai/AiOperations.cs`'s `run()` still runs over its result for
consistency, but will simply never match).

**Session id is synthetic**: Ollama holds no server-side conversation (`resumes_sessions() ->
false`, so `chat_with_repo` re-sends the system prompt and project context on *every* turn for
this engine — the one exception to "only on the first turn"). The app still needs an id purely
so a conversation's turns group together in the activity log (entries without one are dropped
there): the caller's `resume_session_id` is reused when present, else a fresh `ollama-<uuid>` is
minted (`src/CodeFlow.App/Ai/Engines/Ollama.cs`).

Non-agentic for the same reason as OpenAI-compatible: a plain completion model has no write
tools.

## Task routing

Eight `AiTask` variants (`src/CodeFlow.App/Ai/AiCommands.cs`), each selecting both a provider and a
model independently, so one repo can draft commits on a local Ollama model, review PRs on Opus,
and fix findings through opencode:

| Task | `key()` | Purpose | Notes |
|---|---|---|---|
| `Commit` | `commit` | Commit-message generation | Falls back to the engine's dedicated fast model, not its base model |
| `Analyze` | `analyze` | Pre-commit "Analyze changes" | |
| `Review` | `review` | Pull-request review | Actual call site is `src/CodeFlow.App/Review/ReviewCommands.cs` (`07-review-pipeline.md`) |
| `PrDescription` | `pr_description` | PR description drafting | Actual call site is `src/CodeFlow.App/Review/ReviewCommands.cs` |
| `Chat` | `chat` | Open-ended repo chat | |
| `Fix` | `fix` | "Fix with AI" on a finding | The only task that always needs an agentic, write-capable engine |
| `Conflict` | `conflict` | AI merge-conflict resolution | Text-only (no tool use) — can route to a local model `Fix` can't use |
| `Inline` | `inline` | Editor inline edit (Ctrl+I) | Text-only, runs while typing — a fast local model is the point |

### Provider resolution (`provider_for`, `src/CodeFlow.App/Ai/AiCommands.cs`)

For task `t`: read setting `ai_provider_{t.key()}`; if present and non-blank, that provider
wins. Otherwise fall back to the global `ai_provider` setting (`active_provider`,
`src/CodeFlow.App/Ai/AiCommands.cs`), which itself falls back to `"claude"` when unset or blank. A
stored-but-blank per-task override counts as unset (clearing the row in the UI means "inherit").

### Full cascade (`load_ai_config`, `src/CodeFlow.App/Ai/AiCommands.cs`)

1. **Provider** — as above.
2. **Engine** — `src/CodeFlow.App/Ai/`(provider)`.
3. **Binary** — setting `{provider}_binary_path`; blank/unset falls back to `engine.default_binary()`.
4. **Allowed tools** — setting `{provider}_allowed_tools`, a comma-separated string, split and
   trimmed; empty entries dropped. (Ignored entirely by engines whose CLI has no allow-list flag
   — Codex, Gemini/agy, opencode today — see each engine's subsection.)

   **For `analyze` and `review` only, an unset setting falls back to `Read,Grep,Glob`.** Those two
   are handed the change they are meant to judge, so every command they run on top of it re-derives
   what they already have; a `chat` turn is a conversation with the repository and a `fix` is an
   edit to it, and bounding either would take away something the user asked for. The settings screen
   shows those three ticked as recommended without saving them unless a checkbox is touched, so an
   install where nobody opened that row ran unrestricted. Measured on real reviews of this
   repository: the agent called `Bash` eleven and seventeen times against two or three `Read`s, and
   read over two million cached tokens to judge a diff it had already been given. A **blank** stored
   value is not the same as an unset one and still means "no tools": clearing every checkbox is a
   choice, and answering it with a default would overrule it.

   **The list is passed as `--tools` as well as `--allowedTools`, and the difference is the whole
   point.** `--allowedTools` names what runs *without asking*; `--tools` names what *exists*. A first
   attempt at this passed only the former and changed nothing measurable — `Bash` stayed available
   and went on being called seventeen times in a review that then took nine minutes. Verified against
   the real CLI: with `--tools Read,Grep,Glob` the agent answers that it does not have `Bash` in the
   session. Both flags carry the same list, because a headless run has nobody to approve anything.
5. **Model** — per-task override → task-specific fallback:
   - setting `{provider}_{t.key()}_model`; if present and non-blank, wins outright.
   - else, **only for `AiTask.Commit`**: `engine.commit_message_model()` if it returns a
     non-empty string (Claude's Haiku id), else the base model.
   - else (every other task, and Commit when the engine has no dedicated fast model): the base
     model, setting `{provider}_model` (blank counts as empty, i.e. "let the engine pick" — which
     fails outright for Ollama, whose `complete()` requires a non-empty model).

`load_ai_config_for(conn, provider, model)` (`src/CodeFlow.App/Ai/AiCommands.cs`) is the bypass
used when an SDD/Harness agent supplies its own explicit provider **and** model for a turn: it
skips steps 1 and 5 above entirely (binary and tools are still read from that provider's saved
settings) — used by `analyze_working_changes` and `send_chat_message` whenever both
`agent_provider` and `agent_model` are present and non-blank.

These settings-key strings — `ai_provider`, `ai_provider_{task}`, `{provider}_binary_path`,
`{provider}_allowed_tools`, `{provider}_{task}_model`, `{provider}_model` — are exactly what
`src/state/aiProviderStore.ts` re-reads to mirror this same cascade client-side (`modelKey`,
`taskProviderKey`, `taskModelKey`, `loadRouting()`); the two must agree on every key name.

### Shared prompt templates (`shared_template`, `src/CodeFlow.App/Ai/AiCommands.cs`)

Prompt *templates* (not engine config) are provider-independent by design — a user's
customized commit/review/analyze/conflict template applies identically whichever engine is
active. Reading one: try the new unprefixed key (e.g. `commit_template`) first; if blank/unset,
fall back to the legacy key (e.g. `claude_commit_template`), preserving a pre-existing
customization with no migration step. Both blank/unset means "use the engine's built-in
default" (the corresponding `DEFAULT_*` constant). Used for `commit_template`/
`claude_commit_template`, `resolve_conflict_template`/`claude_resolve_conflict_template`, and
`analyze_template`/`claude_analyze_template` — all three called from `src/CodeFlow.App/Ai/AiCommands.cs`.
(The review template's own key/legacy-key pair is read from `src/CodeFlow.App/Review/ReviewCommands.cs`, out of this
document's scope.)

## Run lifecycle

`src/CodeFlow.App/Ai/AiRunRegistry.cs` makes a run observable and cancellable without threading an extra parameter
through every operation in `src/CodeFlow.App/Ai/AiOperations.cs`: the command layer wraps its call in `AiRunRegistry`
(or `scoped_with_trace`), and everything underneath picks the run context up from a
the async runtime!` (`CURRENT`, `src/CodeFlow.App/Ai/AiRunRegistry.cs`).

**Identity.** The `run_id` is minted by the *frontend*, before it invokes, so it can subscribe
to this run's output and hold a cancel handle while the command is still in flight. For flows
that already carry a job id (PR review, change analysis), that id doubles as the run id — the
job-list row the UI already renders is exactly what shows live output and the stop button.
`scoped`/`scoped_with_trace` (`src/CodeFlow.App/Ai/AiRunRegistry.cs`) register the id in a global
`HashMap<string, `Channel`<bool>>` (`registry()`) **before** the future runs — not at spawn
time — so a cancel that arrives during the (potentially slow) DB reads and diff-building before
the subprocess even starts is still observed; the id is removed once the future resolves. A
`None`/blank id means "not tracked": the future runs with no events and no cancel handle, which
is how internal auxiliary calls (model listing, provider probes) stay out of the UI's run list.

**`run()` (`src/CodeFlow.App/Ai/AiOperations.cs`)** — the shared subprocess path (HTTP transports bypass this whole
function, see above):

1. Resolve `binary` → `program` (per Binary discovery) and `PATH` via `search_dirs`.
2. `engine.build_command(program, inv)`, with `stdin`/`stdout`/`stderr` all piped.
3. Spawn the child.
4. **Feed stdin from a separate task**, concurrently with waiting for output, for two reasons:
   an engine that ignores stdin (opencode delivers its payload via `--file`) would otherwise
   deadlock once the OS pipe buffer fills (nothing drains it, so an inline `write_all().await`
   never completes and `wait_with_output` is never reached) — here the write just fails with
   `BrokenPipe` when the child exits, ignored; and an engine that *does* read stdin still needs
   EOF to start producing output, which dropping the handle at the end of the task sends.
5. **Drain stdout/stderr concurrently with the wait**, via `pump()` (below) — reading them only
   after exit would deadlock any CLI whose output outgrows the OS pipe buffer.
6. `Task.WhenAny` between `child.wait()` and cancellation: on cancel, `kill_tree` the child,
   await the pump/writer tasks so they don't outlive the run and emit into a finished one, and
   return `Err(CANCELLED_MARKER)`.
7. On normal exit, strip ANSI from both captured streams and call `engine.interpret(success,
   status_label, stdout, stderr)`.

**`pump()` (`src/CodeFlow.App/Ai/AiOperations.cs`)** reads one pipe in 8 KiB chunks, accumulating raw **bytes** (not a
`string`) so a multi-byte UTF-8 character split across two `read()` calls never decodes into
replacement-character corruption before an engine gets to parse it. Complete lines (split on the
byte buffer, for the same reason) are streamed to the frontend as they arrive via
`AiRunRegistry`. A CLI drawing a progress bar with bare `\r` and no newline would otherwise
grow the pending buffer unboundedly and show the user nothing — past 8,192 pending bytes with no
newline, the partial buffer is flushed as one line anyway.

**`ai:output` carries a formatted activity log, not the answer.** Every line pumped from stdout
or stderr while the process runs is emitted as `ai:output { runId, stream, line }`
(`emit_line`, `src/CodeFlow.App/Ai/AiRunRegistry.cs`) — for Claude this is the `stream-json` event-by-event
narration (tool calls, intermediate turns); for the others it is whatever raw progress text the
CLI writes. **The actual answer is never one of these lines.** It is extracted only after the
process exits, from the *terminal* event/buffer, by each engine's `interpret()` — Claude's is
the very last `{"type":"result",…}` line; opencode's is the concatenation of `text` events, also
only assembled after stdout is fully drained; Codex/Gemini's is simply "whatever is in stdout at
exit". There is no incremental "answer so far" anywhere in this pipeline — a UI that treated
`ai:output` lines as partial answer text would be wrong. `emit_line` also: trims each line, drops
blank lines outright (CLIs pad generously and the log reads better without gaps), truncates any
single line past 2,000 chars (`MAX_LINE_CHARS`) with a trailing `…`, and — when the run is
`scoped_with_trace` — appends it to a capped ring buffer (`MAX_TRACE_LINES = 300`, oldest
dropped first) that is what `send_chat_message` persists alongside a turn so a reopened
conversation can still show *how* the answer was reached.

**Cancellation** (`src/CodeFlow.App/Ai/AiRunRegistry.cs`): `subscribe(run_id)` hands back a `Channel`<bool>`
for a tracked run; `cancel(run_id)` sends `true` on its `Sender` and returns `false` if no such
live run exists (already finished, or never existed — the frontend treats that as "nothing to
do", not an error, since the race between clicking stop and the reply arriving is normal).
`cancelled()` resolves once flipped `true`; a run with no cancel channel (`None`) waits forever,
which is exactly what a `select!` arm wants — it simply never wins.

**`kill_tree()` (`src/CodeFlow.App/Ai/AiRunRegistry.cs`)**: on Windows, `taskkill /PID <pid> /T /F` — a plain
`child.kill()` only signals the immediate process, which on Windows is usually a `.cmd` shim
whose real work happens in a node grandchild; killing only the shim would leave the model call
running (and billing) in the background. On Unix the spawned process *is* the CLI, so a direct
`child.kill()` is correct and used as the fallback/only path there.

**Checkpoints** (`src/CodeFlow.App/Ai/AiCommands.cs`): every AI flow that can write to the working
tree (`resolve_finding_with_ai`, `send_chat_message`) snapshots it first via
`Checkpoints` — best-effort; a repo that can't be snapshotted (no HEAD yet, an
unreadable index) must not block the action itself, it just loses the undo button. A checkpoint
whose run changed nothing on disk is discarded afterward (`checkpoint_after` →
`remove_if_unchanged`) rather than left as clutter in the undo list. `resolve_finding_with_ai`
runs `checkpoint_after` unconditionally — including on failure/cancellation — because "an agent
killed mid-edit is exactly when a half-applied fix needs undoing".

**`send_chat_message`** (`src/CodeFlow.App/Ai/AiCommands.cs`), the fullest example of the lifecycle:
resolves contexts/MCPs/skills and the chat's `AiConfig` (agent override or normal `Chat` task
routing); validates `session_id` against the *previous* turn's recorded provider
(`session_for_provider`, below) before use; times the engine call alone (not the surrounding DB
reads); runs with a checkpoint; reads `engine_version` only *after* the run completes (cached
per binary, so only the very first turn of an app session pays for the extra process spawn); and
records the turn to the activity log — **except** a run the user cancelled (`CANCELLED_MARKER`
prefix) is never persisted, on success or failure, because "it has no answer, and filing it
would leave a permanent failed turn in the transcript for something they did on purpose". A
`conversation_id` the frontend didn't supply gets a throwaway `conv-<uuid>` minted server-side
rather than being silently dropped.

**`session_for_provider`** (`src/CodeFlow.App/Ai/AiCommands.cs`): drops a resume token minted by a
*different* engine than the one about to run. Session tokens are not portable across providers
— each namespaces its id differently (a Claude UUID, an opencode `ses_…`, a Codex rollout UUID,
agy's fixed sentinel) — and replaying one into the wrong engine either fails outright
(`claude --resume ses_abc`) or, worse, silently resumes something unrelated. Given a recorded
`conversation_id`, it looks up the *previous* turn's provider (the store);
if that differs from the provider about to run, the session id is dropped (`None`), which makes
the turn open a fresh engine session and re-send project context. Anything the lookup can't
determine (no recorded provider, a failed read, no `conversation_id` at all) keeps the token —
discarding a working session is judged the worse failure. This only guards *cross*-provider
reuse; two conversations on the *same* provider are already kept apart by each engine resuming a
specific id rather than "the last run" (agy is the sole exception, and a documented one).

**`analyze_working_changes`** (`src/CodeFlow.App/Ai/AiCommands.cs`) mirrors `send_chat_message`'s
shape for a one-shot flow: the `job_id` argument doubles as the run id; on completion (unless
cancelled) a job-history row is written (`"done"`/`text` or `"error"`/message).

## Prompt constants

Nine templates, each an embedded resource under `src/CodeFlow.App/Ai/Prompts/` loaded by
`src/CodeFlow.App/Ai/Prompts.cs` — kept as files rather than C# literals so nothing passes through
escaping and no transcription can alter their indentation. All are sent identically regardless of
which engine is active (they live in the provider-neutral module on purpose). Seven are public;
two — `DEFAULT_CHAT_SYSTEM_PROMPT` and `FIX_FINDING_SYSTEM_PROMPT` — are `internal`, appended
only via `AiInvocation.system_prompt` inside `src/CodeFlow.App/Ai/AiOperations.cs` itself, with no `default_*_template`
command exposing them for a Settings override. All nine are `VERBATIM` — transcribed
byte-for-byte below, in their original language (eight are Spanish; only
`DEFAULT_COMMIT_TEMPLATE` is English), never translated, never reflowed. Fence uses four
backticks because the prompt bodies themselves contain literal triple-backtick fences as
instructions to the model.

### `DEFAULT_COMMIT_TEMPLATE`
**Implementation**: `src/CodeFlow.App/Ai/Prompts/DEFAULT_COMMIT_TEMPLATE.txt` · `VERBATIM`

`text
Write the git commit message for the staged diff piped on stdin.

FORMAT
<type>(<scope>): <emoji> <summary>

- Conventional Commits requires the message to START with the type, so the emoji goes after the colon, never before the type.
- <scope> names the area touched (a module, folder or feature). Drop it, parentheses included, when the change spans too much to name one thing.
- <summary> is imperative mood ("add", not "added" or "adds"), no trailing period. Keep the whole line under 72 characters.

TYPE AND EMOJI
feat     ✨   a new capability
fix      🐛   a bug fix
docs     📝   documentation only
style    🎨   formatting or whitespace, no behaviour change
refactor ♻️   restructuring, no behaviour change
perf     ⚡️   a performance improvement
test     ✅   tests only
build    📦️  build system, packaging or dependency manifests
ci       👷   CI configuration
chore    🔧   maintenance that fits none of the above
revert   ⏪️   reverting an earlier commit

These four replace the type's own emoji when they apply:
💥   a breaking change — also mark the type with "!", e.g. feat(api)!:
🚑️   a critical production hotfix
🔒️   a security fix
⬆️   a dependency upgrade

BODY
Add one only when the change touches several things worth explaining: leave a blank line after the summary, then 2 to 4 "- " bullets saying WHY the change was made, not restating what the diff already shows. A change that does one thing stays a single line with no body.

Respond with ONLY the commit message text — no quotes, no markdown, no explanation.
`

### `DEFAULT_REVIEW_PROMPT`
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs` · `VERBATIM`

Fallback body of `review_pull_request` when no per-workspace template is set; also returned
verbatim by the `default_review_template` command.

`text
Eres un revisor de código senior revisando un pull request. Se te entrega el título, la descripción, el contexto del proyecto y el diff por stdin.

Antes que nada, en la primera línea de tu respuesta, califica el cambio completo con EXACTAMENTE este formato:

📈 CALIDAD: Fiabilidad={A-E} Seguridad={A-E} Mantenibilidad={A-E}

Criterio de las notas (A = mejor, E = peor), evaluando SOLO lo que toca este diff:
- Fiabilidad: A si no hay bugs/riesgos lógicos, B si hay solo hallazgos menores, C si hay advertencias, D si hay un hallazgo crítico, E si hay varios.
- Seguridad: igual criterio pero solo con hallazgos de seguridad.
- Mantenibilidad: igual criterio pero con estilo/complejidad/duplicación.

Luego, para cada problema real que encuentres (bugs, riesgos de seguridad, rendimiento, integración, estilo importante — no inventes hallazgos triviales si el código está bien), responde en Markdown con EXACTAMENTE este formato, uno por hallazgo, en este orden:

### {emoji} [{Severidad} · {Tipo}] {Categoría corta} · F-{número correlativo de 3 dígitos}

{Un subtítulo de una línea, algo más largo que el título, describiendo el problema puntual}

📍 Ubicación: {ruta relativa exacta del archivo desde la raíz del repo}:{línea inicio}-{línea fin}

💭 Por qué: {explicación concreta del problema, citando archivo y línea/función relevante}

💡 Sugerencia: {qué cambiar exactamente para resolverlo}

🛠️ Ejemplo de solución:
`{lenguaje}
{fragmento de código mostrando la solución concreta}
`

🎯 Confianza: {0-100}/100

---

Reglas:
- Responde SIEMPRE en español — el subtítulo, el "Por qué", la "Sugerencia" y cualquier otro texto libre deben estar en español, sin importar el idioma del título, la descripción del PR, el diff, o los comentarios/nombres en el código.
- Usa 🚨 para Crítico, ⚠️ para Advertencia/Mayor, ℹ️ para Menor/Sugerencia.
- Numera los hallazgos F-001, F-002, etc. en el orden en que aparecen en el diff.
- La línea "📍 Ubicación" es obligatoria en cada hallazgo y debe usar la ruta real del archivo tal como aparece en el diff (encabezado `+++ b/...`) y el rango de línea real del lado nuevo del diff — esto se usa para anclar el comentario a esa línea exacta en el PR, así que no la omitas ni la inventes. Escríbela en TEXTO PLANO, sin backticks ni ningún otro formato Markdown (a diferencia del resto de la respuesta, donde sí puedes usar backticks para código) — el valor se parsea literalmente para ubicar el comentario.
- Sé específico y cita archivos/líneas reales del diff — no generalices.
- No repitas el diff completo ni resumas cambios que no son problemáticos.
- Si no encuentras ningún problema real, dilo brevemente en un par de líneas con ✅ después de la línea de CALIDAD, sin inventar hallazgos ni usar la plantilla anterior.
`

### `DEFAULT_PR_REVIEW_STANDARD`
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs` · `VERBATIM`

Ported from the transversal `WF-PR-REVIEWER` runbook (SonarQube-style taxonomy, A–E ratings,
Quality Gate, six review lenses); seeded per-workspace and editable from Settings, project-agnostic
by design (repository-specific rules live in each workspace's review contexts/MD files, folded in
as "PROJECT REVIEW CONTEXT" rather than replacing this standard). Its **OUTPUT FORMAT** section —
the leading `📈 CALIDAD:` line; the `### {emoji} [{Severidad} · {Tipo}] {Categoría} · F-NNN`
finding header, `{emoji}` ∈ 🚨/⚠️/ℹ️; and the `📍 Ubicación` / `💭 Por qué` / `💡 Sugerencia` /
`🛠️` / `🎯 Confianza` fields — is a byte-level contract two separate frontend parsers depend on to
anchor comments to exact PR diff lines; this document does not describe those parsers (that
belongs to whichever document owns `parseAnalysis.ts` and the PR-comment poster), only that
changing this format silently breaks both. Not invoked anywhere in the nine files this document
owns — its call site (seeding/serving a workspace's editable review standard) is in `src/CodeFlow.App/Review/ReviewCommands.cs`/
`src/CodeFlow.App/Workspaces/WorkspaceCommands.cs`, out of scope here.

`text
Eres un revisor de código senior. Revisas un pull request y produces una revisión rigurosa, accionable y en ESPAÑOL. Por stdin recibes el título del PR, su descripción, el contexto de revisión del proyecto y el diff.

Este es el ESTÁNDAR DE REVISIÓN base (transversal a cualquier repositorio). Trata el "PROJECT REVIEW CONTEXT" que venga por stdin como reglas adicionales del proyecto que complementan —nunca reemplazan— este estándar.

## Lentes de revisión (revisa el diff bajo cada una)
1. Correctness — bugs de lógica, off-by-one, null/undefined, flujo de control roto, race conditions.
2. Seguridad — inyección, authn/authz, secretos en código, deserialización insegura, SSRF, path traversal, cripto débil. Marca como "Security Hotspot" el código sensible que requiere ojo humano pero no es una vuln demostrable.
3. Rendimiento — N+1, trabajo en hot paths, asincronía/concurrencia mal usada, complejidad innecesaria.
4. Contrato / integridad de datos — breaking changes de API/DTO/esquema, migraciones, validación en bordes de confianza.
5. Tests — cobertura ausente de lo que se agrega/modifica, tests tautológicos, asserts débiles.
6. Mantenibilidad — dead code, duplicación, naming confuso, complejidad, restos de debug.

## Taxonomía (estilo SonarQube)
- Tipo: uno de `Bug` (Fiabilidad) · `Vulnerabilidad` (Seguridad) · `Security Hotspot` (Seguridad) · `Code Smell` (Mantenibilidad).
- Severidad (5): `Blocker` (data loss, auth bypass, caída en prod, fuga de secretos, breaking change sin migración) · `Crítico` (bug que seguro dispara en uso normal, falta de validación en un borde real, regresión de perf en hot path) · `Mayor` (bug de edge-case, manejo de errores ausente, cambio de contrato que necesita mitigación) · `Menor` (higiene: limpieza de recursos, ruido de logs, retries) · `Info` (nitpick subjetivo).
- Confianza (0–100): 0 falso positivo/pre-existente · 25 quizá real, sin verificar · 50 real pero raro/nitpick · 75 real e impactante, muy probable en prod · 100 cierto, demostrable directo del diff.

## Qué descartar (en todos los casos)
- Lo pre-existente: mismo código ya presente en la rama destino (solo revisas lo que el diff agrega/cambia).
- Lo ya discutido en comentarios/threads del PR.
- Tipos/lint/formato: lo cubre CI. No inventes hallazgos triviales si el código está bien.

## Ratings A–E (por dimensión, según el PEOR hallazgo de esa dimensión)
A sin hallazgos · B peor=Menor · C peor=Mayor · D peor=Crítico · E peor=Blocker.
Fiabilidad ← Bugs · Seguridad ← Vulnerabilidades + Security Hotspots · Mantenibilidad ← Code Smells.

## Quality Gate
PASSED si NO hay ningún hallazgo `Blocker` ni `Crítico`; FAILED en caso contrario. Es binario (solo PASSED/FAILED). Los `Menor`/`Info` no cambian el gate.

────────────────────────────────────────────────────────
## FORMATO DE SALIDA (obligatorio y exacto)

En la PRIMERA línea de tu respuesta, califica el cambio completo con EXACTAMENTE este formato (evaluando SOLO lo que toca el diff):

📈 CALIDAD: Fiabilidad={A-E} Seguridad={A-E} Mantenibilidad={A-E}

Luego, para cada hallazgo real, responde en Markdown con EXACTAMENTE este bloque, uno por hallazgo, en este orden:

### {emoji} [{Severidad} · {Tipo}] {Categoría corta} · F-{número correlativo de 3 dígitos}

{Un subtítulo de una línea, algo más largo que el título, describiendo el problema puntual}

📍 Ubicación: {ruta relativa exacta del archivo desde la raíz del repo}:{línea inicio}-{línea fin}

💭 Por qué: {explicación concreta del problema, citando archivo y línea/función relevante; ≤ 80 palabras}

💡 Sugerencia: {qué cambiar exactamente para resolverlo}

🛠️ Ejemplo de solución:
`{lenguaje}
{fragmento de código mostrando la solución concreta}
`

🎯 Confianza: {0-100}/100

---

Reglas del formato:
- Responde SIEMPRE en español (subtítulo, "Por qué", "Sugerencia" y todo texto libre), sin importar el idioma del PR, del diff o del código.
- `{emoji}` mapea desde la severidad y debe ser EXACTAMENTE uno de estos tres: usa 🚨 para `Blocker` y `Crítico`, ⚠️ para `Mayor`, ℹ️ para `Menor` e `Info`. El `{Severidad}` dentro de los corchetes SÍ lleva el nivel fino (Blocker/Crítico/Mayor/Menor/Info).
- `{Tipo}` es uno de `Bug`/`Vulnerabilidad`/`Code Smell`/`Security Hotspot`.
- `{Categoría corta}` es un slug específico en kebab-case (p. ej. `null-dereference`, `sql-injection`, `dead-code`) — NUNCA repitas ahí la dimensión ni el tipo.
- Numera F-001, F-002, … en el orden en que los hallazgos aparecen en el diff.
- La línea "📍 Ubicación" es OBLIGATORIA en cada hallazgo y debe usar la ruta real del archivo tal como aparece en el diff (encabezado `+++ b/...`) y el rango de línea real del lado nuevo del diff — se usa para anclar el comentario a esa línea exacta en el PR. Escríbela en TEXTO PLANO, sin backticks ni Markdown (a diferencia del resto, donde sí puedes usar backticks). No la omitas ni la inventes.
- Sé específico y cita archivos/líneas reales del diff — no generalices ni repitas el diff completo.
- Ordena los hallazgos por severidad (Blocker→Info) y, dentro de cada severidad, por confianza descendente.
- Si NO encuentras ningún problema real, dilo en un par de líneas con ✅ justo después de la línea de CALIDAD, sin inventar hallazgos ni usar la plantilla de arriba.
`

### `DEFAULT_ANALYZE_TEMPLATE`
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs` · `VERBATIM`

`text
Eres un revisor de código senior. Se te entrega por stdin el contexto del proyecto y el diff de cambios que TODAVÍA NO SE HAN COMMITEADO (working directory), justo antes de que el usuario los comitee.

Analiza el diff buscando específicamente:
- Vulnerabilidades de seguridad (inyección, secretos hardcodeados, validación de entrada faltante, uso inseguro de APIs, etc.)
- Bugs y errores lógicos
- Problemas de rendimiento
- Código que rompe las convenciones o reglas del proyecto (si se entrega contexto)

Antes que nada, en la primera línea de tu respuesta, califica el cambio completo con EXACTAMENTE este formato:

📈 CALIDAD: Fiabilidad={A-E} Seguridad={A-E} Mantenibilidad={A-E}

Criterio de las notas (A = mejor, E = peor), evaluando SOLO lo que toca este diff:
- Fiabilidad: A si no hay bugs/riesgos lógicos, B si hay solo hallazgos menores, C si hay advertencias, D si hay un hallazgo crítico, E si hay varios.
- Seguridad: igual criterio pero solo con hallazgos de seguridad.
- Mantenibilidad: igual criterio pero con estilo/complejidad/duplicación.

Luego, para cada problema real que encuentres, responde en Markdown con EXACTAMENTE este formato, uno por hallazgo, en este orden:

### {emoji} [{Severidad} · {Tipo}] {Categoría corta} · F-{número correlativo de 3 dígitos}

{Un subtítulo de una línea, algo más largo que el título, describiendo el problema puntual}

📍 Ubicación: {ruta relativa exacta del archivo desde la raíz del repo}:{línea inicio}-{línea fin}

💭 Por qué: {explicación concreta del problema, citando archivo y línea/función relevante}

💡 Sugerencia: {qué cambiar exactamente para resolverlo}

🛠️ Ejemplo de solución:
`{lenguaje}
{fragmento de código mostrando la solución concreta}
`

🎯 Confianza: {0-100}/100

---

Reglas:
- Responde SIEMPRE en español, sin importar el idioma del código, nombres o comentarios.
- Usa 🚨 para Crítico, ⚠️ para Advertencia/Mayor, ℹ️ para Menor/Sugerencia.
- Numera los hallazgos F-001, F-002, etc. en el orden en que aparecen en el diff.
- La línea "📍 Ubicación" es obligatoria en cada hallazgo, con la ruta real del archivo y el rango de línea real del lado nuevo del diff, en TEXTO PLANO sin backticks ni ningún otro formato Markdown — el valor se parsea literalmente.
- Sé específico y cita archivos/líneas reales del diff — no generalices.
- No repitas el diff completo ni resumas cambios que no son problemáticos.
- Si no encuentras ningún problema real, dilo brevemente en un par de líneas con ✅ después de la línea de CALIDAD, sin inventar hallazgos ni usar la plantilla anterior.
`

### `DEFAULT_CHAT_SYSTEM_PROMPT`
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs` · `VERBATIM` · private `const` (no `pub`, no command)

`text
Eres el asistente de IA integrado en CodeFlow, un cliente Git de escritorio. Estás conversando con el usuario sobre el repositorio que tiene abierto — usa las herramientas disponibles (leer archivos, buscar código, revisar el estado de git, etc.) cuando haga falta para responder con precisión en lugar de adivinar. Responde en el mismo idioma en el que te escribe el usuario. Sé conciso y directo: esto es una conversación, no un reporte formal — no uses el formato de hallazgos estructurados que usarías en una revisión de PR a menos que el usuario lo pida explícitamente.
`

### `FIX_FINDING_SYSTEM_PROMPT`
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs` · `VERBATIM` · private `const` (no `pub`, no command)

`text
Eres un desarrollador senior aplicando una corrección de code review directamente en el repositorio abierto. Se te entrega por stdin el hallazgo específico a corregir: su ubicación (archivo y línea), por qué es un problema, y la sugerencia de solución.

Instrucciones:
- Abre el archivo indicado y aplica el fix exactamente en esa ubicación — no toques otros archivos ni código no relacionado con este hallazgo puntual.
- Sigue el estilo y las convenciones ya usadas en ese archivo/proyecto.
- NO hagas commit ni ejecutes git — limítate a modificar el/los archivo(s) en el working directory; el usuario decide cuándo comitear.
- Si al mirar el código el problema ya no existe (cambió desde que se generó el hallazgo), no modifiques nada y dilo brevemente.
- Responde en una o dos líneas en español confirmando qué cambiaste (o que no hiciste cambios y por qué) — no repitas el diff ni el hallazgo completo.
`

### `DEFAULT_PR_DESCRIPTION_TEMPLATE`
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs` · `VERBATIM`

`text
Eres un desarrollador experimentado redactando la descripción de un pull request. Se te entrega la rama origen, la rama destino y el diff por stdin.

Devuelve EXACTAMENTE este formato, empezando por la línea del título:

TITLE: {un título conciso en imperativo, estilo Conventional Commits, máximo 72 caracteres}

## Resumen
{1-3 frases explicando qué hace este PR y por qué}

## Cambios
- {un punto por cada cambio relevante del diff}

## Notas
{riesgos, pendientes o consideraciones para el revisor; escribe "Ninguna" si no hay}

Reglas:
- Responde SIEMPRE en español.
- Básate ÚNICAMENTE en el diff; no inventes cambios que no aparezcan.
- La primera línea DEBE empezar con "TITLE: " seguido del título.
- No incluyas el diff crudo ni bloques de código salvo que sean imprescindibles.
- Responde solo con la descripción, sin texto adicional antes o después.
`

### `DEFAULT_RESOLVE_CONFLICT_TEMPLATE`
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs` · `VERBATIM`

`text
Eres un ingeniero de software resolviendo un conflicto de merge de git. Se te entregan por stdin tres versiones de un mismo archivo: BASE (el ancestro común), OURS (la rama actual) y THEIRS (la rama entrante).

Tu tarea: producir el contenido final del archivo integrando de forma coherente los cambios de ambos lados (OURS y THEIRS) y preservando la intención de cada uno. Usa BASE para entender qué cambió cada lado respecto al original.

Reglas ESTRICTAS de salida:
- Responde ÚNICAMENTE con el contenido COMPLETO del archivo ya resuelto.
- NO incluyas marcadores de conflicto (<<<<<<<, =======, >>>>>>>).
- NO envuelvas la respuesta en bloques de código markdown (`), ni añadas explicaciones, comentarios ni texto antes o después del contenido.
- Conserva el estilo, la indentación y el formato del archivo.
- Si ambos lados hacen cambios compatibles, inclúyelos ambos; si son incompatibles, elige la integración más razonable sin perder funcionalidad.
`

### `DEFAULT_INLINE_EDIT_PROMPT`
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs` · `VERBATIM`

`text
Eres un programador editando un fragmento de código dentro de un archivo. Por stdin recibes el archivo completo como contexto, el fragmento seleccionado y la instrucción del usuario.

Tu tarea: devolver el fragmento seleccionado reescrito según la instrucción.

Reglas ESTRICTAS de salida:
- Responde ÚNICAMENTE con el código que reemplaza al fragmento seleccionado.
- NO devuelvas el archivo completo, solo el fragmento reescrito.
- NO uses bloques de código markdown (`) ni añadas explicaciones antes o después.
- Conserva la indentación, el estilo y el lenguaje del archivo.
- Si la instrucción no se puede aplicar, devuelve el fragmento original sin cambios.
`

## Rules

### AI-001 Provider id resolves to an engine, with a safe fallback
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: `engine_for(provider)` matches `"gemini"|"opencode"|"codex"|"ollama"/"local"|"openai"` to their engine; every other value, including empty or unrecognised strings, constructs `ClaudeEngine`. The `openai` arm reads the API key from the OS keyring (`CredentialStore.AiApiKey`("openai")`) right here and bakes it into the returned engine's `Transport`.
**Inputs / outputs**: `provider: string` → `Box<dyn AiEngine>`.
**Edge cases**: A corrupt/missing `ai_provider` setting, or a provider id from an app version that dropped support for it, both resolve to Claude rather than panicking or erroring.
**Frontend dependency**: `src/state/aiProviderStore.ts`'s `AI_PROVIDERS`/`DEFAULT_AI_PROVIDER` must stay in agreement with this match, or the UI can show a provider selected that the backend silently treats as Claude.
**Markers**: none

### AI-002 AiInvocation is the one provider-neutral call shape
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: Every operation builds one `AiInvocation`: `prompt` is the ask (goes on argv for most engines), `stdin_content` is the data payload (diff, PR context, finding, conflict sides). `AiInvocation.new` defaults every optional field off.
**Inputs / outputs**: struct fields listed under "The engine abstraction" above.
**Edge cases**: An engine whose CLI can't safely receive multi-line argv (opencode's `.cmd` shim, Codex's stdin-based contract) overrides `stdin_payload()` to fold everything into the stdin stream instead; every other engine still gets `inv.stdin_content` piped to it even when its CLI ignores stdin outright — harmless because of `run()`'s concurrent stdin-feed design.
**Frontend dependency**: none directly — the shape is internal.
**Markers**: none

### AI-003 Transport routes every top-level operation around the subprocess path
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`, `752-758`, `793-795`, `src/CodeFlow.App/Ai/Engines/Ollama.cs`, `src/CodeFlow.App/Ai/Engines/OpenAi.cs`
**Behaviour**: `run()`, `list_models()`, and `engine_version()` all match on `engine.transport()` before touching binary resolution/PATH/stdio. `Ollama`/`OpenAiCompatible` hand off to `ollama`/`openai` functions directly; their `build_command`/`interpret` trait implementations exist only to satisfy the trait and are never called.
**Inputs / outputs**: n/a (dispatch only).
**Edge cases**: `probe()` also branches per-transport for the Settings availability badge — HTTP engines are probed by asking their endpoint for models/tags, subprocess engines by `find_on_path`.
**Frontend dependency**: none directly.
**Markers**: none

### AI-004 Three engines override `stdin_payload`: Codex sends more, Gemini and opencode send nothing
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`, `src/CodeFlow.App/Ai/Engines/Codex.cs`, `src/CodeFlow.App/Ai/Engines/Gemini.cs`, `src/CodeFlow.App/Ai/Engines/OpenCode.cs`
**Behaviour**: The trait default returns `inv.stdin_content` verbatim, which is what Claude gets. `CodexEngine.stdin_payload` composes `system_prompt + "\n\n" + prompt + "\n\n----- INPUT -----\n\n" + stdin_content` — because `codex exec`'s one positional argument is a fixed pointer sentence and the real instructions must arrive on stdin. `Gemini` and `OpenCode` return the empty string: their CLIs never read stdin, and their brief (inline argv/`--add-dir` file, and `--file` respectively) already carries `stdin_content`.
**Inputs / outputs**: `stdin_payload(&self, inv: &AiInvocation) -> string`.
**Edge cases**: those two used to keep the default, so every run sent the payload twice — once in the brief and once into a pipe nobody drained, which therefore broke on every single run. Normalising that breakage is what let a real one go unreported on the engines that *do* read stdin; an empty payload is now the declaration AI-054 reads.
**Frontend dependency**: none.
**Markers**: none

### AI-005 Binary search order: install dirs, then inherited PATH
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: `install_dirs()` (OS-specific fixed list) is prepended to the process's own `PATH` (`search_dirs()`), in that order, and used both to build the child's `PATH` (`apply_path`) and to resolve a bare binary name (`resolve_binary`/`find_on_path`).
**Inputs / outputs**: none → `IReadOnlyList<PathBuf>`.
**Edge cases**: A GUI app launched from Finder (macOS) or an app already running when a CLI was installed (Windows) would otherwise never see the CLI on its inherited `PATH`.
**Frontend dependency**: none.
**Markers**: none

### AI-006 Windows resolves bare names to `.exe`/`.cmd`/`.bat`, exe preferred per directory
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: For each directory in `search_dirs()`, in order, try `<name>.exe`, then `<name>.cmd`, then `<name>.bat`; the first existing file wins. A name already containing a path separator or an extension is trusted as-is and skips resolution entirely.
**Inputs / outputs**: `(binary: string, dirs: IReadOnlyList<PathBuf>) -> string`.
**Edge cases**: A real `.exe` beats a `.cmd`/`.bat` shim **only within the same directory** — an earlier directory's shim still wins over a later directory's native exe. `Command` needs the resolved path (not the bare name) to route a `.cmd` through `cmd.exe` correctly on the sidecar ≥1.77.
**Frontend dependency**: none.
**Markers**: none

### AI-007 Manual `binary_path` setting bypasses all discovery
**Implementation**: `src/CodeFlow.App/Ai/AiCommands.cs`, `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: A non-blank `{provider}_binary_path` setting is used exactly as stored — `resolve_binary`'s "already has a separator or extension" branch means an absolute/explicit path is never extension-resolved or directory-searched.
**Inputs / outputs**: setting string → `AiConfig.binary`.
**Edge cases**: A blank stored value counts as unset and falls back to `engine.default_binary()`.
**Frontend dependency**: Settings' per-provider "binary path" field.
**Markers**: none

### AI-008 `probe()` is the Settings availability check, per transport
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: `Ollama` → `fetch_tags`; `OpenAiCompatible` → (if key present) `fetch_models`, else `(false, "missing-api-key")`; `Subprocess` → `find_on_path`. Returns `(available: bool, detail: string)` where `detail` is the resolved path/endpoint on success or a short raw reason (never pre-translated) on failure.
**Inputs / outputs**: `(engine, binary) -> (bool, string)`.
**Edge cases**: none beyond the above.
**Frontend dependency**: `check_ai_provider` command → Settings "available / not found" badge, which wraps `detail` in its own translated label.
**Markers**: none

### AI-009 `run()`: spawn, concurrent stdin feed, concurrent output pump, cancellation
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: Stdin is written from a separate spawned task concurrently with waiting for the child, specifically so an engine that never reads stdin can't deadlock the pipe once its OS buffer fills. Stdout/stderr are pumped concurrently with the wait for the same deadlock-avoidance reason. `Task.WhenAny` races `child.wait()` against cancellation; on cancel, `AiRunRegistry` runs and the pump/writer tasks are awaited before returning `Err(CANCELLED_MARKER)`, so no task keeps emitting into an already-finished run.
**Inputs / outputs**: `(engine, binary, inv) -> AiRun`.
**Edge cases**: Both captured streams are ANSI-stripped before `engine.interpret` ever sees them.
**Frontend dependency**: `ai:output` events (see AI-013) and `cancel_ai_run`.
**Markers**: none

### AI-010 `pump()`: byte-safe line streaming with a `\r` progress-bar escape hatch
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: Reads raw bytes (not `string`) to avoid corrupting a multi-byte UTF-8 character split across two `read()` calls. Complete lines (byte-split on `\n`) are streamed via `AiRunRegistry` as they complete. A pending (no-newline-yet) buffer over 8,192 bytes is flushed as one line — this is what keeps a CLI redrawing a `\r` progress bar from growing memory unboundedly and showing nothing.
**Inputs / outputs**: async pipe reader → accumulated `byte[]` (also drives the side-effecting emits).
**Edge cases**: A `None` `RunCtx` (untracked call) still accumulates bytes for the interpreter but emits nothing.
**Frontend dependency**: `ai:output`.
**Markers**: none

### AI-011 `ai:output` is a formatted activity log, never the answer
**Implementation**: `src/CodeFlow.App/Ai/AiRunRegistry.cs`, `src/CodeFlow.App/Ai/Engines/Claude.cs` (result_payload), `src/CodeFlow.App/Ai/Engines/OpenCode.cs` (parse_events)
**Behaviour**: Every line pumped from stdout/stderr while a process runs is emitted live as `ai:output`. The reply text is extracted only after the process exits, from the terminal event/whole buffer, inside each engine's `interpret()` — never assembled incrementally from the streamed lines.
**Inputs / outputs**: n/a.
**Edge cases**: For Claude, intermediate `ai:output` lines include `stream-json` tool-use events that are never the final answer even though they are valid JSON events; only the last `{"type":"result"}` line is.
**Frontend dependency**: any consumer of `ai:output` must render it as a live activity/progress log, not as a partial answer — a standing misconception this document exists partly to correct.
**Markers**: none

### AI-012 `emit_line`: per-line cap, blank-line drop, bounded trace
**Implementation**: `src/CodeFlow.App/Ai/AiRunRegistry.cs`, `28`, `33`
**Behaviour**: Trims trailing whitespace; drops the line entirely if now empty; truncates to 2,000 chars (`MAX_LINE_CHARS`) with a trailing `…` if longer; when the run is `scoped_with_trace`, appends to a ring buffer capped at 300 lines (`MAX_TRACE_LINES`), dropping the oldest first.
**Inputs / outputs**: `(ctx, stream, line)` → emits `ai:output`, mutates `ctx.trace`.
**Edge cases**: A run started via plain `scoped` (no trace requested) never accumulates a trace, but still emits live events.
**Frontend dependency**: `send_chat_message`'s persisted `trace_json`.
**Markers**: none

### AI-013 Cancellation registry and `kill_tree`
**Implementation**: `src/CodeFlow.App/Ai/AiRunRegistry.cs`, `170-184`
**Behaviour**: A global `HashMap<run_id, `Channel`<bool>>` is populated for the lifetime of a `scoped`/`scoped_with_trace` future, registered before the future runs (not at spawn) so an early cancel is still observed. `cancel(id)` returns `false` for an untracked/already-finished id. `kill_tree` uses `taskkill /PID <pid> /T /F` on Windows (killing only the immediate `.cmd` shim would leave the real model call running/billing in a node grandchild) and a plain `child.kill()` on Unix.
**Inputs / outputs**: `cancel(string) -> bool`; `kill_tree(&mut Child)`.
**Edge cases**: A run with no cancel channel (`None`, untracked) waits forever in `cancelled()`, correctly never winning a `select!`.
`cancel(id)` also answers `false` — rather than failing the command — when the run ended between the
lookup and the signal, which disposes the source underneath it.
**Frontend dependency**: `cancel_ai_run` command.
**Markers**: none

**Every run has a deadline** (`AiRunRegistry.DefaultRunTimeout`, ten minutes). Nothing bounded a run
before: the wait was linked only to the caller's token, so a CLI that never exited left the panel
spinning with the stop button — inside a collapsed log — as the only way out. Expiry kills the
process tree exactly as a stop does, but reports `RUN_TIMED_OUT::` rather than `RUN_CANCELLED::`
(`XLANG-003`), because the user pressed nothing. The two sources are separate and linked into one
token so the handler can tell which fired; a shutdown racing the deadline still reads as a
cancellation. The constructor takes an override purely as a test seam.

**A run executes inside the analysed repository, and used to run under its configuration.** The CLI
is spawned with `WorkingDirectory` set to the project root (`AI-002`), so that repository's own
`.claude/settings.json` applied to the run: its hooks, plugins and LSP servers all loaded. A `Stop`
hook that type-checks and lints did so *inside* CodeFlow's analysis, holding the process open long
after the review itself was finished — observed live 2026-08-02, a one-file analysis sitting at
"working" for five minutes because the repository under review had just gained such a hook. The
cause is removed by `--setting-sources user` (`AI-028`), on the same reasoning that already gave
`--strict-mcp-config` its place; the deadline above remains as the backstop for a CLI that hangs for
any other reason.

### AI-014 QUOTA_MARKER / quota_signal / mark_quota
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: `QUOTA_MARKER = "QUOTA_EXCEEDED::"`. `quota_signal(text)` lower-cases and substring-matches against an 11-phrase dictionary (`usage limit`, `rate limit`, `quota exceeded`, `resets at`, `try again in`, `limit reached`, `insufficient balance`, `insufficient credit`, `out of credit`, `payment required`, `billing`) shared by every engine's own `interpret()`. `mark_quota` applies the same detection to the two HTTP engines' results in `run()`, since they never reach a subprocess `interpret()`.
**Inputs / outputs**: `string -> bool`; `AiRun -> AiRun`.
**Edge cases**: A message already prefixed with `QUOTA_MARKER` is left as-is (not double-prefixed).
The substring match runs over the engine's **whole output**, so a successful result whose *content*
mentions one of the phrases is misclassified as a quota failure — observed live 2026-08-01, when a
PR review finding explaining backoff mentioned "rate limiting" and the entire (correct, complete)
review was discarded as `QUOTA_EXCEEDED::`. Recorded as `BUG-AI-b` in `91-known-bugs.md`; preserved,
not fixed.
**Frontend dependency**: the frontend renders a dedicated "out of quota" notice whenever an error string starts with `QUOTA_MARKER`, instead of a generic error banner.
**Markers**: `VERBATIM` (the marker string and the 11 dictionary phrases); `BUG-AI-b`

### AI-015 Model listing precedence
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: HTTP engines list over their own API (bypassing everything below). For subprocess engines: `cached_models()` (a catalogue the CLI already wrote to disk) is checked before `list_models_args()` (spawning the CLI's own listing subcommand); an engine with neither returns an empty `Vec` with no process spawned.
**Inputs / outputs**: `(engine, binary) -> IReadOnlyList, string>`.
**Edge cases**: A non-zero exit from the listing subcommand surfaces `'{binary} {args}' failed: {stderr or "no output"}`.
**Frontend dependency**: `list_ai_models` command; an empty result is the frontend's signal to show its curated per-provider list instead.
**Markers**: none

### AI-016 `engine_version` probing and per-binary caching
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: Subprocess-only (`None` for HTTP transports). Runs `<binary> --version` once per distinct binary string for the life of the process, caching the `string?` result — including a failed probe, cached as `None`, so a missing/older binary isn't re-spawned on every chat turn. `parse_version` takes the first non-blank line, then the first whitespace-token that looks version-shaped (`v`-stripped, contains `.`, starts with a digit); falls back to the whole first line; caps the result at 40 chars; empty/whitespace-only output is `None`.
**Inputs / outputs**: `(engine, binary) -> string?`.
**Edge cases**: Checks stdout first, then stderr (not every CLI prints its banner on stdout).
**Frontend dependency**: `send_chat_message`'s `engine_version` field on `ChatReply`.
**Markers**: none

### AI-017 `stamp_footer` is review/analyze-only
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`, `1106`, `1152`
**Behaviour**: Appends `\n\n---\n🤖 Análisis automatizado ({kind}) · {label} ({model}) · {timestamp}` (local time, `%Y-%m-%d %H:%M`) to the result of `review_pull_request` (`kind = "pr-review"`) and `analyze_changes` (`kind = "análisis pre-commit"`) only. The model shown prefers what the CLI actually reported (`run.model`) over the configured setting, falling back to `"modelo predeterminado"` when both are blank.
**The stamp now says what the run cost**, appended after the timestamp:
` · {billed:N0} tokens ({cached:N0} desde caché)`, plus ` · equiv. API USD {cost:F4}` when the engine
reported one. `billed` is fresh input + output + cache writes; cached reads are stated apart because
they cost a fraction and move the most — folding them into one figure would make an agent that
re-read the repository look identical to one that did not. An engine that reports no usage appends
nothing at all rather than zeroes, so an unmeasured run never reads as a free one — which today is
Codex and Gemini (see `AI-019`).

**Why the money says "equiv. API".** The Claude CLI reports `total_cost_usd` whatever the account
is, computed from the token counts against the model's list price. A Claude Pro or Max subscriber
pays a flat fee and no per-token charge, so a bare figure would be an invoice for money nobody is
charging them — verified against a real subscription account (`organizationType: claude_max`,
`billingType: stripe_subscription`), which still receives a populated `total_cost_usd`. The number
is kept because it is the quickest way to compare two runs, and labelled because a number that means
something other than it appears to is worse than no number. It stays the engine's own arithmetic;
nothing here multiplies tokens by a price list this repository would then have to keep current.

**Why it is stamped rather than stored in a column.** Nothing recorded a run's cost anywhere, so
comparing two of them meant reading the CLI's own session files by hand — that is how a review that
became twice as fast was, for a while, only *believed* to have become cheaper. The stamp already
carries provenance, travels with `review_md` into `review_runs`, and is visible the moment a run
ends, with no new IPC surface and no migration. A richer breakdown in the panel is a separate change.

**Inputs / outputs**: `(body, kind, label, model, when, usage?) -> string`.
**Edge cases**: Never applied to commit messages, PR descriptions, conflict resolution, inline edits, or chat replies — those are meant to be used verbatim (a commit message, a file, a chat bubble), not read as a report.
**Frontend dependency**: any renderer of a review/analyze result must expect this trailing footer.
**Markers**: none

### AI-018 `strip_code_fence` for the three results meant to be used verbatim
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`, `914`, `1026`
**Behaviour**: If the trimmed text starts with ` ` `, strips one outer fence (opening line through the matching closing ` ` `) and returns the inner body trimmed; otherwise returns the trimmed text unchanged. Applied to `resolve_conflict`'s, `inline_edit`'s and `generate_commit_message`'s results — all three become raw content (a file, an editor selection, a commit message) and some models wrap their answer in a fence despite being told not to. The commit message joined them when `DEFAULT_COMMIT_TEMPLATE` started allowing a bulleted body: a multi-line answer is what a model fences, and those backticks would have gone into the repository's history.
**Inputs / outputs**: `string -> string`.
**Edge cases**: Only one outer fence is stripped; a genuinely fenced code block *inside* the intended content would not survive round-tripping, but this is what the source does.
**Frontend dependency**: none directly — feeds straight into the file-write/editor-buffer path.
**Markers**: none

### AI-019 `generate_commit_message`
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: Errors immediately on an empty/whitespace-only diff. Truncates the diff to `MAX_DIFF_CHARS` (20,000 chars, by Unicode scalar count via `.chars().take()`). Uses the caller-resolved `model` and `prompt_template` (falling back to `DEFAULT_COMMIT_TEMPLATE` when blank) verbatim; returns the reply with any outer code fence stripped (`AI-018`) and no footer.
**Inputs / outputs**: `(engine, binary, model, diff, template) -> string`; error `"No staged changes to summarize"`.
**Edge cases**: none beyond truncation and the fence stripping. A workspace that already stored its own `commit_template` keeps it — `Settings` only falls back to the built-in when the row is blank — so a change to `DEFAULT_COMMIT_TEMPLATE` reaches new workspaces and nobody's edited copy.
**Frontend dependency**: `generate_commit_message` command.
**Markers**: none

### AI-020 `generate_pr_description`
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: Errors on an empty diff-between-branches (Spanish message). Truncates to `MAX_REVIEW_DIFF_CHARS` (120,000 chars). stdin payload is `"RAMA ORIGEN: {source}\nRAMA DESTINO: {target}\n\nDIFF:\n{diff}"`. Returns the raw `TITLE:`-prefixed reply — no footer; the command layer (`src/CodeFlow.App/Review/ReviewCommands.cs`) splits title/body.
**Inputs / outputs**: `(engine, binary, model, source_branch, target_branch, diff, template) -> string`; error `"No hay diferencias entre las ramas para describir"`.
**Edge cases**: none beyond truncation.
**Frontend dependency**: `generate_pr_description` command (owned by `07-review-pipeline.md`).
**Markers**: none

### AI-021 `resolve_conflict`
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: Each of base/ours/theirs is independently capped at `MAX_CONFLICT_SIDE_CHARS` (40,000 chars) — a side bigger than that is judged better merged by hand than fed whole to the model. stdin payload labels the three sections in Spanish. Returns `strip_code_fence(run.text)`; nothing is written to disk here.
**Inputs / outputs**: `(engine, binary, model, file_path, base, ours, theirs, template) -> string`.
**Edge cases**: No empty-input guard (unlike the diff-based operations) — an empty conflict side is sent through as-is.
**Frontend dependency**: `resolve_conflict_with_ai` command.
**Markers**: none

### AI-022 `review_level_directive`: básico / completo / ultra
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: Appended to the end of the review prompt (after the base template, so it overrides any depth the standard implies). `"basico"`/`"básico"` → confidence ≥ 75, Blocker/Crítico only, terse. Unknown/empty/anything else → `"completo"` → confidence ≥ 60 (Blocker ≥ 50), all severities except Info. `"ultra"` → confidence ≥ 50, all six lenses including Info/nitpicks.
**Inputs / outputs**: `(string level, bool explorable) -> &'static str` (a directive block).
**Edge cases**: any unrecognised level string silently becomes `completo` — never an error.
**Frontend dependency**: the review level selector, whatever it is named on the frontend.
**Markers**: `VERBATIM` (the level headers `## NIVEL DE REVISIÓN ACTIVO: {level}`)

**Changed in 1.9.x — the directive knows whether there is a checkout, and the prose is English.**

`ultra` used to say *"lee el método completo alrededor de cada cambio"* at every level of the
cascade, including a review reached by pasted link — whose working directory holds `PULL_REQUEST.md`
and `changes.diff` and nothing else, and whose stdin already carries `NO_CLONE_CONTEXT` telling the
model not to try. The two instructions reached the model through **different channels of the same
invocation** — one on argv, one on stdin — so neither read as wrong on its own. `ultra` now resolves
to one of two blocks:

| `explorable` | resource | says |
|---|---|---|
| `true` (project-backed) | `REVIEW_LEVEL_ULTRA` | the `CODE AROUND THE CHANGES` section already quotes the declaration around every change; open a file only to follow a symbol quoted nowhere in it |
| `false` (link review) | `REVIEW_LEVEL_ULTRA_NO_CLONE` | there is no checkout; judge from the diff and lower confidence where the surrounding code is not visible |

`basico` and `completo` are unaffected by the flag.

The **instructions** in all the review prompts are now English, per the repository's own rule. What
stays Spanish, byte for byte, is everything the model is asked to *emit* and everything a parser
matches on: the `## NIVEL DE REVISIÓN ACTIVO:` headers, `📈 CALIDAD:`, `📍 Ubicación:`,
`💭 Por qué:`, `💡 Sugerencia:`, `🎯 Confianza:`, the severity and type words, and the standing order
to answer in Spanish. A workspace that already stored the Spanish methodology keeps it — `STORE-012`
only falls back to the built-in when the row is blank.

### AI-023 `review_pull_request`
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: Errors on an empty diff. stdin payload: `PR TITLE`, `PR DESCRIPTION` (`"(no description)"` when blank), optional `PROJECT REVIEW CONTEXT` (one `- {name}: {content}` line per enabled context), `DIFF:`, then — when there is a working tree to extract it from — the `CODE AROUND THE CHANGES` block (`GIT-033`). Prompt = (custom template or `DEFAULT_REVIEW_PROMPT`) + `\n\n` + the level directive for `(level, explorable)`.
**Inputs / outputs**: `(runner, config, pr_title, pr_description, contexts, diff, code_context, cwd, template, level, explorable, mcp_config_path, run) -> AiRun`; error `"This pull request has no changes to review"`.
**Edge cases**: none beyond the level fallback.
**Frontend dependency**: `review_pull_request`/`review_pr_from_link` commands (`src/CodeFlow.App/Review/ReviewCommands.cs`, `07-review-pipeline.md`).
**Markers**: none

**Changed in 1.9.x — it returns the run, and no longer stamps it.** The footer is written by
`ReviewRun` instead, for two reasons. It has to be the **last thing in the text**, and it was not:
stamped here, the resolved-findings history section was appended after it and `parseAnalysis`'s
end-anchored `FOOTER_RE` stopped matching. And half of what belongs in it — how long the whole
operation took, how much of the change reached the model, what the findings did since the last
review — is known there and not here. See `REVIEW-038`.

### AI-024 `analyze_changes`
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: Errors on an empty diff (Spanish message). stdin payload: optional `PROJECT CONTEXT` block, then `DIFF:`. Prompt = custom template or `DEFAULT_ANALYZE_TEMPLATE`. Result is `stamp_footer`'d with `kind = "análisis pre-commit"`.
**Inputs / outputs**: `(engine, binary, model, contexts, diff, allowed_tools, cwd, template, mcp_config_path) -> string`; error `"NOTHING_TO_ANALYZE: No hay cambios sin commitear para analizar"`.
**Edge cases**: none beyond what `GIT-031` leaves out, which it names in the payload.
**Frontend dependency**: `analyze_working_changes` command.
**Markers**: none

**Changed in 1.9.x — the refusal is no longer filed.** The message gained the `NOTHING_TO_ANALYZE: `
marker (`XLANG-015`) and `AiTurn.AnalyzeWorkingChangesAsync` now skips the `job_history` write for
it, the same way it already skipped a cancelled run. Filing it was right while reaching this needed
a deliberate click; the analyze tab starts a run when it is merely *opened*, so on a clean tree the
old behaviour left a permanent red row in Activity for a request nobody made — and showed Electron's
`Error invoking remote method 'codeflow:invoke'` as the explanation. The frontend now renders an
empty state and never starts the run in the first place.

**A context is capped, and says when it was.** `AppendContexts` concatenated every enabled project
or review context whole, with no limit of any kind — the one payload in this file without one, while
a commit diff has 20 000 characters and a conflict side 40 000. A context is a free-text field the
user pastes into, stored in a SQLite `TEXT` column that neither validates nor truncates, so pasting
an architecture document into one entered the prompt entire and unannounced. Each is now capped at
30 000 characters, per context rather than over all of them together — a shared pool would let the
first starve the rest, which is the failure `GIT-031` fixed for files — and a cut names how many
characters it left behind.

**The diff is no longer truncated here.** `MAX_REVIEW_DIFF_CHARS` was a blunt cut applied to the
joined text of all three prompt paths, and it was silent: whatever fell past 120 000 characters
simply was not there, unmarked. The budget now belongs to `GIT-031`, which spends it — trimming
unchanged context, excluding churn that carries no reviewable signal, sharing what is left between
files so none is lost for being last, and naming everything it leaves out. `AiOperations` appends
what it is given. Cutting it a second time by character count would silently undo all of that,
which is the defect this replaced.

### AI-025 `chat_with_repo`: context resend and forced edit approval
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: `needs_context = session_id.is_none() || !engine.resumes_sessions()` — project context and the system prompt are sent only once per engine-side session for engines that resume server-side; an engine that can't (Ollama) gets them re-sent every turn. `-p`/`prompt` always carries just the user's message. `auto_approve_edits` is unconditionally `true` for chat — headless runs can never answer an interactive permission prompt, so file-editing tools are pre-approved for every chat turn regardless of any general "allow edits" setting.
**Inputs / outputs**: `(engine, binary, model, contexts, message, session_id, allowed_tools, cwd, mcp_config_path) -> AiRun`.
**Edge cases**: none beyond the resume/no-resume branch.
**Frontend dependency**: `send_chat_message` command.
**Markers**: none

### AI-026 `apply_finding_fix`: fixed write-tool set, agentic guard
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: Refuses immediately (Spanish message naming Claude/Gemini/opencode as alternatives) when `!engine.agentic()` — a defensive backstop since the UI already hides "fix with AI" for non-agentic providers (Ollama, OpenAI-compatible). Always uses `engine.fix_tools()` for `allowed_tools`, **ignoring** the user's general `{provider}_allowed_tools` setting entirely — clicking "fix" is itself the write-access opt-in. `auto_approve_edits` is always `true`.
**Inputs / outputs**: `(engine, binary, model, finding_prompt, cwd) -> string`.
**Edge cases**: none beyond the agentic guard.
**Frontend dependency**: `resolve_finding_with_ai` command.
**Markers**: none

### AI-027 `inline_edit`: empty-selection guard, capped context
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: Errors immediately on an empty/whitespace-only selection (Spanish message). File content is capped at `MAX_DIFF_CHARS` for context. stdin payload labels `ARCHIVO`, `CONTENIDO DEL ARCHIVO (contexto)`, `FRAGMENTO SELECCIONADO`, `INSTRUCCIÓN`. System prompt is always `DEFAULT_INLINE_EDIT_PROMPT` (no override path). Result is `strip_code_fence`'d.
**Inputs / outputs**: `(engine, binary, model, file_path, file_content, selection, instruction) -> string`; error `"No hay código seleccionado para editar"`.
**Edge cases**: none beyond truncation.
**Frontend dependency**: `inline_edit_with_ai` command.
**Markers**: none

### AI-028 Claude argv shape
**Implementation**: `src/CodeFlow.App/Ai/Engines/Claude.cs`
**Behaviour**: `claude -p <prompt> [--append-system-prompt sp] [--model m] --output-format stream-json --verbose --setting-sources user [--tools t,… --allowedTools t1,t2,…] [--permission-mode acceptEdits] [--mcp-config path --strict-mcp-config] [--resume id]`, with `current_dir` set from `inv.cwd`. `stream-json --verbose` is unconditional — the only flag combination the CLI accepts alongside `-p` that streams events at all.

`--setting-sources user` is unconditional too, and is the second half of `--strict-mcp-config`'s
reasoning. `inv.cwd` is the repository under review, so the CLI would otherwise load that
repository's `.claude/settings.json` and run *its* hooks, plugins and LSP servers inside CodeFlow's
own analysis — a `Stop` hook that type-checks and lints held a one-file review open for five
minutes (observed 2026-08-02). `user` excludes `settings.local.json` as well, which is the same
repository under a different file name, and leaves the developer's own global configuration in
place. It is deliberately **not** `--bare`: that flag also stops the CLI reading OAuth and the
keychain, so every subscription user's run would fail for want of an `ANTHROPIC_API_KEY`.
**Inputs / outputs**: `(binary, inv) -> Command`.
**Edge cases**: `--setting-sources` requires a Claude CLI recent enough to know the flag (verified
against 2.1.220); an older binary rejects it outright rather than ignoring it. The same is already
true of `--strict-mcp-config`.
**Frontend dependency**: none directly (argv is backend-internal).
**Markers**: none

### AI-029 Claude `interpret_output`: stdout parsed before status, quota, model_used
**Implementation**: `src/CodeFlow.App/Ai/Engines/Claude.cs`
**Behaviour**: Finds the last `{"type":"result",…}` line (falling back to whole-buffer parse). If a `result` text is present: quota-checked first; if the run failed or `is_error` is set, that text is the error; otherwise it's the (trimmed) reply, with `session_id` and — only when `modelUsage` has exactly one key — `model`. If no parseable result text exists at all: on failure, quota-check stderr then stdout, else report the exit status with whichever stream is non-empty (`"claude exited with an error ({status}): {detail}"`, `"sin salida en stdout ni stderr"` when both are empty); on success, the trimmed raw stdout is the reply.
**Inputs / outputs**: `(success, status_label, stdout, stderr) -> AiRun`.
**Edge cases**: A run that used more than one model reports `model: None`, not a guess.
**Frontend dependency**: none beyond the shared `QUOTA_MARKER` contract.
**Markers**: none

### AI-030 Codex argv, sandbox and approval flags
**Implementation**: `src/CodeFlow.App/Ai/Engines/Codex.cs`
**Behaviour**: `codex exec [resume <id>] "<POINTER>" [--model m] --sandbox {workspace-write|read-only} -c approval_policy="never" [--cd dir]`, plus `current_dir`. `resume <id>` precedes the prompt (it is an `exec` subcommand). Sandbox is `workspace-write` iff `auto_approve_edits`, else `read-only`; `danger-full-access` is never used. `approval_policy` is forced to `"never"` via `-c` (not `--ask-for-approval`, which `codex exec` rejects on 0.145+).
**Inputs / outputs**: `(binary, inv) -> Command`.
**Edge cases**: `--cd` and `current_dir` are both set — the sandbox's workspace root and the process's actual cwd are tracked separately by Codex.
**Frontend dependency**: none directly.
**Markers**: none

### AI-031 Codex session id: scraped from the stderr preamble
**Implementation**: `src/CodeFlow.App/Ai/Engines/Codex.cs`
**Behaviour**: `session_id_from_preamble` matches a line whose trimmed, lower-cased form starts with `"session id:"` or `"session_id:"` (either spelling, any case) and returns the trimmed remainder, or `None` if that remainder is empty or no such line exists.
**Inputs / outputs**: `string -> string?`.
**Edge cases**: A preamble shape change silently costs continuity (fresh session next turn, context re-sent) rather than resuming an unrelated rollout — a deliberate choice, not a defect.
**Frontend dependency**: chat continuity for the Codex provider.
**Markers**: none

### AI-032 Codex model catalogue via `models_cache.json`
**Implementation**: `src/CodeFlow.App/Ai/Engines/Codex.cs`
**Behaviour**: `codex_home()` = `$CODEX_HOME` if set, else `~/.codex`. `read_models_cache` parses `{codex_home}/models_cache.json`, keeps entries with `visibility == "list"`, sorts ascending by `priority`. An empty or unreadable/unparsable result is `None` (not `Some(vec![])`), so the frontend's curated fallback applies. No subcommand is spawned.
**Inputs / outputs**: `&Path -> IReadOnlyList<string>?`.
**Edge cases**: none beyond the above.
**Frontend dependency**: `list_ai_models` for the `codex` provider.
**Markers**: none

### AI-033 Codex `interpret_output`: stdout is the reply, stderr is progress
**Implementation**: `src/CodeFlow.App/Ai/Engines/Codex.cs`
**Behaviour**: On failure, quota-check stderr then stdout, else `"codex exited with an error ({status}): {detail}"`. On success, the trimmed stdout is the reply (quota-checked too — a quota refusal can still exit 0); an empty stdout with non-empty stderr reports the stderr text as the error, empty-both reports `"codex produced no output"`.
**Inputs / outputs**: `(success, status_label, stdout, stderr) -> AiRun`.
**Edge cases**: stderr progress chatter on a clean exit is never mistaken for a failure.
**Frontend dependency**: none beyond the shared quota contract.
**Markers**: none

### AI-034 Gemini/agy brief composition and inline-vs-temp-file threshold
**Implementation**: `src/CodeFlow.App/Ai/Engines/Gemini.cs`, `146-156`
**Behaviour**: Composes `system_prompt + "\n\n" + prompt + "\n\n----- INPUT -----\n\n" + stdin_content` (when stdin non-empty) exactly like Codex's stdin brief, but delivers it via `-p`. If the composed brief is ≤ `INLINE_LIMIT` (12,000 chars), it goes inline as the `-p` argument; otherwise it is written to `codeflow-agy-<uuid>/brief.txt`, that directory is added with `--add-dir`, and `-p` instead carries a short pointer telling agy to read the file.
**Inputs / outputs**: `(binary, inv) -> Command`.
**Edge cases**: A failed temp-file write degrades to an inline attempt rather than failing the call outright (`write_brief_file_if_large` returns `None` on any `System.IO` error, same as the "fits inline" case).
**Frontend dependency**: none directly.
**Markers**: `BUG-AI-a` **closed** (the temp file/directory now has a lifecycle — see the Markers section)

### AI-035 Gemini/agy permission flag triggers
**Implementation**: `src/CodeFlow.App/Ai/Engines/Gemini.cs`
**Behaviour**: `--dangerously-skip-permissions` is set when `auto_approve_edits` is true **or** the brief was delivered via the temp-file path (`needs_read_permission`) — agy has no granular tool-allowlist, so even a read-only large-prompt run needs this to read its own temp file headlessly.
**Inputs / outputs**: n/a (flag presence).
**Edge cases**: A small, read-only prompt needs neither condition and gets no permission flag at all.
**Frontend dependency**: none directly.
**Markers**: none

### AI-036 Gemini/agy session continuity: sentinel + global `--continue`
**Implementation**: `src/CodeFlow.App/Ai/Engines/Gemini.cs`, `121-126`, `160-193`
**Behaviour**: `agy` cannot resume a specific conversation by id from `--print` mode. The engine always reports `Some(SESSION_SENTINEL)` (`"agy-last"`) as the session id on a successful run, and sends `--continue` (not a specific id) whenever `resume_session_id.is_some()`. `--continue` resumes agy's own idea of "the last run" globally, not scoped to any one conversation.
**Inputs / outputs**: n/a.
**Edge cases**: Two chats open on the same project can silently answer each other's context — a known, accepted limitation of the upstream CLI (tracked as `google-antigravity/antigravity-cli#7`), not something this port should paper over with a guess.
**Frontend dependency**: `session_for_provider` (AI-defined below) relies on providers reporting *some* session id consistently; agy's sentinel satisfies that even though it identifies nothing.
**Markers**: `DIVERGENCE-AI-b`

### AI-037 Gemini/agy `interpret_output`
**Implementation**: `src/CodeFlow.App/Ai/Engines/Gemini.cs`
**Behaviour**: Same failure/quota/empty-output shape as Codex (stdout is the whole reply; stderr is status/banner noise on success). A successful, non-empty stdout always yields `session_id: Some(SESSION_SENTINEL)`.
**Inputs / outputs**: `(success, status_label, stdout, stderr) -> AiRun`.
**Edge cases**: none beyond AI-036.
**Frontend dependency**: none beyond the shared quota contract.
**Markers**: none

### AI-038 opencode brief-to-file, forced `--format json`, pointer-before-file ordering
**Implementation**: `src/CodeFlow.App/Ai/Engines/OpenCode.cs`, `142-145`
**Behaviour**: `opencode run "<pointer>" --format json [--model m] [--auto] [--dir cwd] [--session id] --file <path>`. The full brief (system + ask + `----- INPUT -----` + data) is written to a fresh `codeflow-opencode-<uuid>.txt` and attached via `--file`; the pointer positional **must** precede `--file`, a variadic flag that would otherwise consume it as another attachment path.
**Inputs / outputs**: `(binary, inv) -> Command`.
**Edge cases**: A failed temp-file write means the run proceeds with only the pointer message — a degraded, non-crashing outcome (temp writes "~never fail", per source comment).
**Frontend dependency**: none directly.
**Markers**: `BUG-AI-a` **closed**.

### AI-039 opencode event parsing
**Implementation**: `src/CodeFlow.App/Ai/Engines/OpenCode.cs`
**Behaviour**: Every stdout line is `{type, timestamp, sessionID, ...data}`. `text` events contribute their trimmed `part.text`, joined with `\n` in arrival order, once each is non-empty. `error` events capture `"{name}: {message}"` (or just whichever of the two is present) from the **first** error event seen. `sessionID` is taken from the first event of *any* kind that carries a non-blank one — including an `error` event.
**Inputs / outputs**: `string -> Parsed { text, session_id, error }?`; `None` when no line parses as an `Event` at all.
**Edge cases**: A build that ignores `--format json` (or one whose stdout has no `{`-prefixed lines) returns `None`, and the caller falls back to treating all of stdout as plain text with no session id.
**Frontend dependency**: session continuity for the opencode provider.
**Markers**: none

### AI-040 opencode `interpret_output` and stale-session rewrite
**Implementation**: `src/CodeFlow.App/Ai/Engines/OpenCode.cs`
**Behaviour**: An `error` event, when present, wins over the exit status (opencode can emit one on an otherwise zero-exit run). Absent that, failure/quota/empty-output follow the shared shape. `stale_session_hint` rewrites any failure detail containing `"session not found"` (case-insensitive, anywhere in the string) into a fixed Spanish message telling the user to start a new conversation, on both the events-parsed and the raw-stdout failure paths.
**Inputs / outputs**: `(success, status_label, stdout, stderr) -> AiRun`.
**Edge cases**: A quota refusal that arrives as an `error` event (not just as an exit-status failure) still gets `QUOTA_MARKER`.
**Frontend dependency**: none beyond the shared quota contract; the stale-session message is user-facing prose, not a machine-parsed marker.
**Markers**: `VERBATIM` (the stale-session Spanish message)

### AI-041 opencode `fix_tools()` names are unverified and never actually used
**Implementation**: `src/CodeFlow.App/Ai/Engines/OpenCode.cs`, module doc `src/CodeFlow.App/Ai/Engines/OpenCode.cs`
**Behaviour**: Returns `["read","edit","write","bash","grep","glob"]`, assigned to `AiInvocation.allowed_tools` by `apply_finding_fix`, but `opencode run` has no tool-allowlist flag and `build_command` never reads `inv.allowed_tools` — write access for "fix with AI" on opencode comes entirely from `--auto`.
**Inputs / outputs**: n/a (dead data, kept "for parity/documentation" per source comment).
**Edge cases**: none — the values simply have no runtime effect today.
**Frontend dependency**: none.
**Markers**: `AMBIGUOUS-AI-a`

### AI-042 OpenAI-compatible `complete()`: message composition and status-code error mapping
**Implementation**: `src/CodeFlow.App/Ai/Engines/OpenAi.cs`
**Behaviour**: Fails fast (before any HTTP call) on a blank API key or blank model, both with actionable Spanish messages. Messages sent: optional `system`, then one `user` message = `prompt` + (`"\n\n" + stdin_content` when non-empty). `POST {base}/chat/completions` with `stream: false`. Status mapping: `401`/`403` → key rejected; `429` → `"Rate limit / quota exceeded: {detail}"` (the exact wording `quota_signal` matches); `404` → model doesn't exist at this endpoint; else raw status + `error_detail`.
**Inputs / outputs**: `(base_url, api_key, inv) -> AiRun`.
**Edge cases**: `session_id` is always `None` (no server-side session); `model` in the returned `AiRun` prefers the API's echoed `model` field over the requested one (an alias can resolve to a different id).
**Frontend dependency**: none beyond the shared quota contract.
**Markers**: none

### AI-043 OpenAI model discovery: chat-model filter, alphabetised, degrades to empty
**Implementation**: `src/CodeFlow.App/Ai/Engines/OpenAi.cs`
**Behaviour**: `fetch_models` calls `GET {base}/models`, keeps ids failing none of 16 non-chat substrings (`is_chat_model`), sorts alphabetically. `list_models` returns an empty `Vec` (not an error) when the key is blank or the fetch itself fails.
**Inputs / outputs**: `(base_url, api_key) -> IReadOnlyList, string>` (`fetch_models`, errors propagate — also the reachability probe); `(base_url, api_key) -> IReadOnlyList, string>` (`list_models`, never errors).
**Edge cases**: An exclude-list (not an allow-list) means a brand-new chat model ships already visible in the picker.
**Frontend dependency**: `list_ai_models` for the `openai` provider; empty result triggers the curated-list fallback.
**Markers**: none

### AI-044 Ollama `complete()`: mandatory model, synthetic session id, 404 mapping
**Implementation**: `src/CodeFlow.App/Ai/Engines/Ollama.cs`
**Behaviour**: Rejects a blank model up front with an actionable Spanish message (Ollama has no "pick for me" default). Message composition mirrors AI-042. `404` → actionable "not pulled" message naming `ollama pull {model}`; any other non-2xx → raw `"Ollama devolvió {status}: {detail}"`; a connection failure suggests `ollama serve`. `session_id` reuses the caller's `resume_session_id` when present, else mints `ollama-<uuid>` — bookkeeping only, so turns group in the activity log; Ollama itself holds no conversation state.
**Inputs / outputs**: `(base_url, inv) -> AiRun`.
**Edge cases**: No quota concept at all (a local, unmetered server) — `quota_signal` is never called from this file, though `src/CodeFlow.App/Ai/AiOperations.cs`'s `mark_quota` still wraps the result uniformly (and will simply never match).
**Frontend dependency**: `resumes_sessions() -> false` means `chat_with_repo` re-sends full context on every Ollama turn, unlike every other engine.
**Markers**: none

### AI-045 Ollama model discovery: `/api/tags`, degrades to empty
**Implementation**: `src/CodeFlow.App/Ai/Engines/Ollama.cs`
**Behaviour**: `fetch_tags` calls `GET {base}/api/tags`, returns model `name`s in whatever order the server reports (no client-side sort). `list_models` degrades to empty on any failure — the Settings status badge, not the picker, is what reports Ollama being unreachable.
**Inputs / outputs**: `(base_url) -> IReadOnlyList, string>` (both functions).
**Edge cases**: none beyond the above.
**Frontend dependency**: `list_ai_models` for the `ollama`/`local` provider.
**Markers**: none

### AI-046 `load_ai_config`: the full per-task resolution cascade
**Implementation**: `src/CodeFlow.App/Ai/AiCommands.cs`
**Behaviour**: Provider = `ai_provider_{task}` (non-blank) else `ai_provider` (else `"claude"`). Binary = `{provider}_binary_path` (non-blank) else `engine.default_binary()`. Tools = `{provider}_allowed_tools`, comma-split, trimmed, empties dropped. Model = `{provider}_{task}_model` (non-blank) else, for `Commit` only, `engine.commit_message_model()` (if non-empty) else `{provider}_model` (the last being the universal final fallback for every other task and for Commit when the engine has no dedicated fast model).
**Inputs / outputs**: `(conn, task: AiTask) -> AiConfig`.
**Edge cases**: A blank stored setting is always treated as unset at every step (`nonblank` helper) — never as an explicit empty override.
**Frontend dependency**: `src/state/aiProviderStore.ts` mirrors this exact chain with the same setting-key names — `ai_provider`, `ai_provider_{task}`, `{provider}_binary_path`, `{provider}_allowed_tools`, `{provider}_{task}_model`, `{provider}_model`.
**Markers**: none

### AI-047 `load_ai_config_for`: explicit provider+model bypass
**Implementation**: `src/CodeFlow.App/Ai/AiCommands.cs`
**Behaviour**: Used when an SDD/Harness agent supplies its own `agent_provider`+`agent_model` for a turn (both non-blank). Skips the provider-routing and model-cascade steps of AI-046 entirely — model is taken as given — but binary and allowed-tools are still read from that provider's saved settings.
**Inputs / outputs**: `(conn, provider: string, model: string, task: string) -> AiConfig`.
**Edge cases**: Only triggers when *both* `agent_provider` and `agent_model` are present and non-blank; either alone falls through to the normal per-task `load_ai_config`.
**Frontend dependency**: `analyze_working_changes` and `send_chat_message`'s `agent_provider`/`agent_model`/`agent_prompt` parameters.
**Markers**: none

**Changed in 1.9.x — the task travels down this route too.** It skipped the provider and model
cascade by design, and skipped the **judging-task toolset default** by accident: a workspace agent
driving a review or an analysis ran with every tool the CLI offers while the ordinary route was held
to `Read`/`Grep`/`Glob`. `task` is a required parameter rather than an optional one, so the omission
cannot recur silently. Found by CodeFlow reviewing its own pull request.

### Toolset defaults, and the difference between "unset" and "none"

`{provider}_allowed_tools` now distinguishes three states, and the engines act on all three:

| stored | `AiConfig.AllowedTools` | Claude Code receives |
|---|---|---|
| no row | `null`, unless the task is judging (then `["Read","Grep","Glob"]`) | no tool flags — the CLI's own defaults |
| empty string (every checkbox cleared) | `[]` | `--tools ""`, the CLI's spelling of "disable all tools" |
| `"Read,Grep"` | `["Read","Grep"]` | `--tools Read,Grep --allowedTools Read,Grep` |

The middle row is the one that changed: clearing the field was already *read* as "no tools" and had
no way to be *sent* as one, so it silently became "unset". It is also what a review below `ultra`
now asks for — see `REVIEW-039`.

`--tools` and `--allowedTools` answer different questions and both are sent when there is a list:
`--tools` is what exists for the run, `--allowedTools` is what runs without asking. Sending only the
second bounded nothing — seventeen `Bash` calls in one measured review, with zero denials.

### AI-048 `shared_template`: new key with legacy fallback
**Implementation**: `src/CodeFlow.App/Ai/AiCommands.cs`
**Behaviour**: Reads `key` first; if blank/unset, reads `legacy_key`; if that too is blank/unset, returns `""` (meaning "use the engine's built-in default"). Used for `commit_template`/`claude_commit_template`, `resolve_conflict_template`/`claude_resolve_conflict_template`, `analyze_template`/`claude_analyze_template`.
**Inputs / outputs**: `(conn, key, legacy_key) -> string`.
**Edge cases**: A pre-existing customization stored under the legacy key survives with no migration step.
**Frontend dependency**: Settings' template editors read/write the *new* key only; only the read path falls back to legacy.
**Markers**: none

### AI-049 `session_for_provider`: cross-engine session invalidation
**Implementation**: `src/CodeFlow.App/Ai/AiCommands.cs`
**Behaviour**: Given a candidate `session_id` and a `conversation_id`, looks up the *previous* turn's recorded provider for that conversation; if it differs from the provider about to run, the session id is dropped (`None`). No `conversation_id`, no session id, or a failed/absent lookup all keep the token as-is.
**Inputs / outputs**: `(conn, project_id, conversation_id, session_id, provider) -> string?`.
**Edge cases**: Guards only *cross*-provider reuse; two conversations on the *same* provider are already isolated by each engine resuming a specific id (agy's global `--continue` is the sole, documented exception — AI-036).
**Frontend dependency**: `send_chat_message`'s session-id handling; the model picker separately refuses to switch provider mid-chat, so this is the backstop for routing changed in Settings or a reopened past conversation.
**Markers**: none

### AI-050 `send_chat_message`: cancelled turns are never persisted
**Implementation**: `src/CodeFlow.App/Ai/AiCommands.cs`
**Behaviour**: A run whose error starts with `CANCELLED_MARKER` is returned to the caller as an error but **not** written to the store — neither as a failed nor as a successful turn — because a stopped run "has no answer" and would otherwise leave a permanent artefact for something the user did on purpose. Every other outcome (success or genuine failure) is recorded, tagged with `provider`, `model` (`None` on failure), `engine_version`, and `response_time_ms` (timed around the engine call only, excluding surrounding DB reads/IPC).
**Inputs / outputs**: n/a (persistence side effect).
**Edge cases**: A missing `conversation_id` from the caller gets a throwaway `conv-<uuid>` minted server-side rather than being dropped; a failed *activity-log write* after a successful run still returns the reply to the user, stamped with the current time instead of the DB-recorded one.
**Frontend dependency**: the chat history / activity list.
**Markers**: none

### AI-051 `analyze_working_changes`: job id doubles as run id, history persisted
**Implementation**: `src/CodeFlow.App/Ai/AiCommands.cs`
**Behaviour**: `job_id` is passed as the `AiRunRegistry` run id, so the pre-existing job-list row shows this run's live output and stop button with no separate id to plumb. On completion, unless cancelled, one job-history row is written (`"done"` + text, or `"error"` + message); a cancelled run writes nothing, mirroring AI-050's chat behaviour.
**Inputs / outputs**: n/a (persistence side effect).
**Edge cases**: An agent override (`agent_provider`+`agent_model`, both non-blank) uses `load_ai_config_for` (AI-047) instead of the normal `Analyze` task routing; the agent's own prompt, when present, is inserted as the first enabled context under the name `"Agent"`.
**Frontend dependency**: the job history list.
**Markers**: none

### AI-052 Checkpoints around write-capable AI flows
**Implementation**: `src/CodeFlow.App/Ai/AiCommands.cs`, `458-467`, `572-590`
**Behaviour**: `resolve_finding_with_ai` and `send_chat_message` both snapshot the working tree (`Checkpoints`) before running, best-effort (a snapshot failure is logged to stderr and swallowed — it must never block the AI action itself, only costs the user the undo button). Afterward, a checkpoint whose run left the tree unchanged is removed (`remove_if_unchanged`) rather than kept as clutter.
**Inputs / outputs**: n/a (side effect around the run).
**Edge cases**: `resolve_finding_with_ai` runs the post-check unconditionally, including on failure/cancellation, specifically so a half-applied fix from a killed run is still undoable.
**Frontend dependency**: the AI checkpoints / undo list (`list_ai_checkpoints`/`restore_ai_checkpoint`, owned by `04-git.md`).
**Markers**: none

### AI-053 The nine prompt constants are provider-neutral, mostly Spanish, two private
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs`
**Behaviour**: All nine are defined once in the provider-neutral `src/CodeFlow.App/Ai/AiOperations.cs` and sent identically regardless of the active engine — a customized commit/review/analyze/conflict template applies the same whichever CLI is behind it. Eight are Spanish; only `DEFAULT_COMMIT_TEMPLATE` is English. Seven are `pub const` with a `default_*_template` command exposing them; `DEFAULT_CHAT_SYSTEM_PROMPT` and `FIX_FINDING_SYSTEM_PROMPT` are private `const`s with no such command — they are only ever appended server-side.
**Inputs / outputs**: n/a (constants).
**Edge cases**: none.
**Frontend dependency**: the seven `default_*_template` commands; `DEFAULT_PR_REVIEW_STANDARD`'s output-format section additionally anchors two frontend parsers (see the "Prompt constants" section above).
**Markers**: `VERBATIM` (all nine)

### AI-054 A broken stdin pipe never replaces the run, and never passes for a whole one
**Implementation**: `src/CodeFlow.App/Ai/AiEngineRunner.cs` (`WriteStdin`, `SubprocessAsync`), `src/CodeFlow.App/Ai/AiRunRegistry.cs` (`RunAsync`, `ProcessOutcome`)
**Behaviour**: The stdin-feed task **cannot throw**. It swallows the `IOException` from the write *and* from `StreamWriter.Close()`, which flushes and therefore breaks a second time on the same pipe; it returns whether the whole payload got through, carried out as `ProcessOutcome.StdinDelivered`. `RunAsync` awaits that task after the process has already exited, so a throwing writer discarded a finished run — which is how `IOException: Pipe is broken.` came to replace both completed reviews and the CLI's own account of why it died at startup. `AiEngineRunner` then interprets the outcome **first** (the CLI's message is the reason a child that died at startup also broke the pipe), and only afterwards, when the engine declared a non-empty `stdin_payload` and delivery came back false, refuses the answer with `"{binary} stopped reading its input before all N characters had been handed over, so its answer covers an unknown part of the change"`.
**Inputs / outputs**: `ProcessOutcome.StdinDelivered: bool`, defaulted `true` so an unredirected run and every scripted test outcome read as complete.
**Edge cases**: a payload smaller than the OS pipe buffer (64 KiB) is reported delivered even if the child never read a byte — the kernel accepted it and there is nothing further to observe. Cancellation is unaffected: the writer's `OperationCanceledException` still propagates, and the guarded close cannot mask it. Engines whose CLI ignores stdin declare an empty payload, so the refusal never fires for them.
**Frontend dependency**: none directly; the error text reaches the AI error banner like any other `AiRunFailedException`.
**Markers**: none

## Test coverage

45 ` functions across six files (`src/CodeFlow.App/Ai/AiOperations.cs`, `src/CodeFlow.App/Ai/Engines/Claude.cs`, `src/CodeFlow.App/Ai/Engines/Codex.cs`, `src/CodeFlow.App/Ai/Engines/Gemini.cs`,
`src/CodeFlow.App/Ai/Engines/OpenCode.cs`, `src/CodeFlow.App/Ai/Engines/OpenAi.cs`); `src/CodeFlow.App/Ai/AiRunRegistry.cs`, `src/CodeFlow.App/Ai/Engines/Ollama.cs` and `src/CodeFlow.App/Ai/AiCommands.cs` carry none.
Every test is a pure-function `vector` — none needs a real subprocess, filesystem beyond a
throwaway temp directory, or network, so none is `behavioural`.

| extracted case | Source | Fixture | Kind |
|---|---|---|---|
| `strips_colour_codes_from_a_cli_error` | `src/CodeFlow.App/Ai/AiOperations.cs` | `ai.vectors.json#strips-sgr-colour-codes` | vector |
| `strips_osc_hyperlink_sequences` | `src/CodeFlow.App/Ai/AiOperations.cs` | `ai.vectors.json#strips-osc-hyperlink-sequences` | vector |
| `leaves_ordinary_text_untouched` | `src/CodeFlow.App/Ai/AiOperations.cs` | `ai.vectors.json#leaves-ordinary-text-untouched` | vector |
| `reads_the_version_out_of_each_cli_banner` | `src/CodeFlow.App/Ai/AiOperations.cs` | `ai.vectors.json#claude-banner-with-parenthetical-label` + 3 more cases | vector |
| `falls_back_to_the_banner_line_and_rejects_empty_output` | `src/CodeFlow.App/Ai/AiOperations.cs` | `ai.vectors.json#no-version-shaped-token-falls-back-to-whole-first-line` + 2 more cases | vector |
| `a_credit_balance_refusal_counts_as_a_quota_signal` | `src/CodeFlow.App/Ai/AiOperations.cs` | `ai.vectors.json#credit-balance-refusal-is-a-quota-signal` | vector |
| `a_genuine_failure_is_not_a_quota_signal` | `src/CodeFlow.App/Ai/AiOperations.cs` | `ai.vectors.json#unknown-flag-error-is-not-a-quota-signal` | vector |
| `surfaces_the_reason_json_carries_when_stderr_is_empty` | `src/CodeFlow.App/Ai/Engines/Claude.cs` | `claude.vectors.json#empty-stderr-surfaces-the-json-reason` | vector |
| `reads_the_verdict_out_of_a_streamed_run` | `src/CodeFlow.App/Ai/Engines/Claude.cs` | `claude.vectors.json#reads-the-verdict-out-of-a-streamed-run` | vector |
| `a_streamed_failure_reports_its_reason_not_the_exit_status` | `src/CodeFlow.App/Ai/Engines/Claude.cs` | `claude.vectors.json#a-streamed-failure-reports-its-reason-not-the-exit-status` | vector |
| `falls_back_to_the_exit_status_when_nothing_explains_the_failure` | `src/CodeFlow.App/Ai/Engines/Claude.cs` | `claude.vectors.json#falls-back-to-the-exit-status-when-nothing-explains-the-failure` | vector |
| `a_quota_failure_still_gets_the_marker_the_frontend_looks_for` | `src/CodeFlow.App/Ai/Engines/Claude.cs` | `claude.vectors.json#a-quota-failure-gets-the-marker` | vector |
| `a_successful_run_still_returns_the_reply_and_session_id` | `src/CodeFlow.App/Ai/Engines/Claude.cs` | `claude.vectors.json#a-successful-run-trims-and-returns-the-reply-and-session-id` | vector |
| `non_json_stdout_on_a_clean_exit_is_passed_through` | `src/CodeFlow.App/Ai/Engines/Claude.cs` | `claude.vectors.json#non-json-stdout-on-a-clean-exit-is-passed-through` | vector |
| `reports_the_model_the_cli_actually_ran` | `src/CodeFlow.App/Ai/Engines/Claude.cs` | `claude.vectors.json#reports-the-model-the-cli-actually-ran` | vector |
| `stays_silent_when_more_than_one_model_ran` | `src/CodeFlow.App/Ai/Engines/Claude.cs` | `claude.vectors.json#stays-silent-when-more-than-one-model-ran` | vector |
| `a_missing_model_usage_field_is_not_an_error` | `src/CodeFlow.App/Ai/Engines/Claude.cs` | `claude.vectors.json#a-missing-model-usage-field-is-not-an-error` | vector |
| `a_successful_run_returns_stdout_as_the_reply` | `src/CodeFlow.App/Ai/Engines/Codex.cs` | `codex.vectors.json#a-successful-run-returns-stdout-as-the-reply` | vector |
| `captures_the_rollout_id_from_the_stderr_preamble` | `src/CodeFlow.App/Ai/Engines/Codex.cs` | `codex.vectors.json#captures-the-rollout-id-from-the-stderr-preamble` | vector |
| `reports_no_session_when_the_preamble_omits_the_id` | `src/CodeFlow.App/Ai/Engines/Codex.cs` | `codex.vectors.json#reports-no-session-when-the-preamble-omits-the-id` | vector |
| `accepts_the_underscored_and_differently_cased_spellings` | `src/CodeFlow.App/Ai/Engines/Codex.cs` | `codex.vectors.json#accepts-underscored-and-mixed-case-spelling` + 1 more case | vector |
| `surfaces_the_failure_detail` (codex) | `src/CodeFlow.App/Ai/Engines/Codex.cs` | `codex.vectors.json#surfaces-the-failure-detail` | vector |
| `a_plan_limit_message_gets_the_marker` | `src/CodeFlow.App/Ai/Engines/Codex.cs` | `codex.vectors.json#a-plan-limit-message-gets-the-marker` | vector |
| `progress_on_stderr_is_not_mistaken_for_an_error_on_a_clean_exit` | `src/CodeFlow.App/Ai/Engines/Codex.cs` | `codex.vectors.json#progress-on-stderr-is-not-mistaken-for-an-error-on-a-clean-exit` | vector |
| `reads_the_catalog_newest_first_and_skips_hidden_entries` | `src/CodeFlow.App/Ai/Engines/Codex.cs` | `codex.vectors.json#newest-first-skipping-hidden-entries` | vector |
| `no_catalog_falls_back_rather_than_reporting_an_empty_list` | `src/CodeFlow.App/Ai/Engines/Codex.cs` | `codex.vectors.json#no-file-on-disk-falls-back-to-none` + 1 more case | vector |
| `a_successful_run_returns_stdout_as_the_reply` (gemini) | `src/CodeFlow.App/Ai/Engines/Gemini.cs` | `gemini.vectors.json#a-successful-run-returns-stdout-and-the-session-sentinel` | vector |
| `surfaces_the_failure_detail` (gemini) | `src/CodeFlow.App/Ai/Engines/Gemini.cs` | `gemini.vectors.json#surfaces-the-failure-detail` | vector |
| `a_quota_message_gets_the_marker` (gemini) | `src/CodeFlow.App/Ai/Engines/Gemini.cs` | `gemini.vectors.json#a-quota-message-gets-the-marker` | vector |
| `empty_output_on_a_clean_exit_is_an_error_not_a_blank_reply` (gemini) | `src/CodeFlow.App/Ai/Engines/Gemini.cs` | `gemini.vectors.json#empty-output-on-a-clean-exit-is-an-error-not-a-blank-reply` | vector |
| `small_prompts_stay_inline` | `src/CodeFlow.App/Ai/Engines/Gemini.cs` | `gemini.vectors.json#a-4-char-prompt-stays-inline` | vector |
| `reads_the_reply_and_the_real_session_id_out_of_the_event_stream` | `src/CodeFlow.App/Ai/Engines/OpenCode.cs` | `opencode.vectors.json#reads-the-reply-and-the-real-session-id-out-of-the-event-stream` | vector |
| `joins_multiple_text_parts_in_order` | `src/CodeFlow.App/Ai/Engines/OpenCode.cs` | `opencode.vectors.json#joins-multiple-text-parts-in-order` | vector |
| `an_error_event_beats_a_clean_exit_status` | `src/CodeFlow.App/Ai/Engines/OpenCode.cs` | `opencode.vectors.json#an-error-event-beats-a-clean-exit-status` | vector |
| `a_quota_failure_alongside_events_still_gets_the_marker` | `src/CodeFlow.App/Ai/Engines/OpenCode.cs` | `opencode.vectors.json#a-quota-failure-alongside-events-still-gets-the-marker` | vector |
| `a_quota_error_event_gets_the_marker` | `src/CodeFlow.App/Ai/Engines/OpenCode.cs` | `opencode.vectors.json#a-quota-error-event-gets-the-marker` | vector |
| `a_dropped_session_explains_itself_instead_of_repeating_the_cli_error` | `src/CodeFlow.App/Ai/Engines/OpenCode.cs` | `opencode.vectors.json#a-dropped-session-explains-itself` | vector |
| `falls_back_to_plain_stdout_and_reports_no_session` | `src/CodeFlow.App/Ai/Engines/OpenCode.cs` | `opencode.vectors.json#falls-back-to-plain-stdout-and-reports-no-session` | vector |
| `surfaces_the_failure_detail` (opencode) | `src/CodeFlow.App/Ai/Engines/OpenCode.cs` | `opencode.vectors.json#surfaces-the-failure-detail` | vector |
| `a_quota_message_gets_the_marker` (opencode) | `src/CodeFlow.App/Ai/Engines/OpenCode.cs` | `opencode.vectors.json#a-quota-message-gets-the-marker` | vector |
| `empty_output_on_a_clean_exit_is_an_error_not_a_blank_reply` (opencode) | `src/CodeFlow.App/Ai/Engines/OpenCode.cs` | `opencode.vectors.json#empty-output-on-a-clean-exit-is-an-error-not-a-blank-reply` | vector |
| `pulls_the_message_out_of_an_openai_error_body` | `src/CodeFlow.App/Ai/Engines/OpenAi.cs` | `openai.vectors.json#pulls-the-message-out-of-an-openai-error-body` | vector |
| `falls_back_to_the_raw_body_when_it_is_not_openai_shaped` | `src/CodeFlow.App/Ai/Engines/OpenAi.cs` | `openai.vectors.json#falls-back-to-the-raw-body-when-not-openai-shaped` | vector |
| `keeps_chat_models_including_ones_that_do_not_exist_yet` | `src/CodeFlow.App/Ai/Engines/OpenAi.cs` | `openai.vectors.json#gpt-5-is-a-chat-model` + 4 more cases | vector |
| `drops_models_that_cannot_run_a_chat_completion` | `src/CodeFlow.App/Ai/Engines/OpenAi.cs` | `openai.vectors.json#text-embedding-3-large-is-not-a-chat-model` + 6 more cases | vector |

45 tests accounted for.

## Markers raised

| Marker | What | Where |
|---|---|---|
| `DIVERGENCE-AI-a` | `src/CodeFlow.App/Ai/Engines/Gemini.cs` drives the Antigravity CLI (`agy`), not a `gemini` binary — the UI label "Gemini" is the account/brand, not the executable. Deliberate; do not "fix" the naming. | `src/CodeFlow.App/Ai/Engines/Gemini.cs` |
| `DIVERGENCE-AI-b` | agy/Gemini session resume is a fixed sentinel + global `--continue`, not a per-conversation id — the CLI gives a headless caller no way to target a specific conversation. Two chats on the same project can silently cross contexts; accepted upstream limitation (`google-antigravity/antigravity-cli#7`), not a bug to fix in this port. | `src/CodeFlow.App/Ai/Engines/Gemini.cs`, AI-036 |
| ~~`BUG-AI-a`~~ **CLOSED** | Temp payload files were written per invocation and never deleted: opencode's `--file` attachment (`codeflow-opencode-<uuid>.txt`) and agy's large-brief directory (`codeflow-agy-<uuid>/brief.txt`) — unbounded temp growth over the life of the app. Closed by `src/CodeFlow.App/Ai/EngineScratch.cs`, the one owner of the naming contract: creation, recognition from the built command's own arguments, deletion in the runner's `finally` on every exit path (reply, CLI error, launch failure, cancellation), and an age-gated startup sweep (> 1 h, so a concurrent instance's live invocation is never claimed). See `91-known-bugs.md`. | AI-034, AI-038, `src/CodeFlow.App/Ai/EngineScratch.cs` |
| `AMBIGUOUS-AI-a` | opencode's `fix_tools()` tool-name list (`read/edit/write/bash/grep/glob`) is marked `TODO(verify)` in source and has no observable runtime effect (opencode has no allow-list flag to pass them to). Whether these are opencode's true internal tool names is unconfirmed by the source itself. | `src/CodeFlow.App/Ai/Engines/OpenCode.cs`, AI-041 |
| `VERBATIM` | The nine prompt constants; `QUOTA_MARKER` and the 11-phrase `QUOTA_SIGNALS` dictionary; the three review-level directive blocks; opencode's stale-session Spanish message. | Prompt constants section, AI-014, AI-022, AI-040 |
