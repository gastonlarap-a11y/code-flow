# 10 — Security

## Scope

- `src/CodeFlow.App/Security/CredentialStore.cs`, `MacKeychain.cs`, `WindowsCredentialManager.cs`
- `src/CodeFlow.App/Security/SecretScan.cs`, `SecretCommands.cs`
- `src/CodeFlow.App/Files/WatcherCommands.cs` — the pre-commit scan command

## Commands

Full parameter/return signatures live in `01-ipc-surface.md`. One line per command here:

| Command | What it does |
|---|---|
| `set_ado_pat` | Stores an Azure DevOps PAT for an org in the OS credential store. |
| `has_ado_pat` | Returns whether a PAT is saved for an org. Never returns the PAT itself. |
| `delete_ado_pat` | Removes the stored Azure DevOps PAT for an org. |
| `set_github_token` | Stores a GitHub token for a host in the OS credential store. |
| `has_github_token` | Returns whether a token is saved for a host. Never returns the token itself. |
| `delete_github_token` | Removes the stored GitHub token for a host. |
| `set_ai_api_key` | Stores an HTTP AI provider's API key (e.g. OpenAI) in the OS credential store. |
| `has_ai_api_key` | Returns whether a non-empty API key is saved for a provider. Never returns the key itself. |
| `delete_ai_api_key` | Removes the stored API key for a provider. |
| `scan_staged_secrets` | Scans a repo's staged diff for hardcoded credentials and returns every match found. |

## Credential store

**Service name** (`src/CodeFlow.App/Security/CredentialStore.cs`), **`VERBATIM`**:

`csharp
public const string Service = "com.codeflow.app";
`

Every credential is stored under this one service name via the credential store's
`CredentialStore`(SERVICE, key)` (`src/CodeFlow.App/Security/CredentialStore.cs`). What distinguishes one credential from another
is the `key` string, built by three deterministic formatters — all **`VERBATIM`**, transcribed
exactly as the code builds them:

| Formatter | Source | Format string | Example |
|---|---|---|---|
| `ado_pat_key(org: string)` | `src/CodeFlow.App/Security/CredentialStore.cs` | `format!("ado-pat:{org}")` | `ado-pat:myorg` |
| `github_token_key(host: string)` | `src/CodeFlow.App/Security/CredentialStore.cs` | `format!("github-token:{host}")` | `github-token:github.com` |
| `ai_api_key(provider: string)` | `src/CodeFlow.App/Security/CredentialStore.cs` | `format!("ai-api-key:{provider}")` | `ai-api-key:openai` |

Azure DevOps is keyed per **org** (one PAT authenticates against a specific org); GitHub is
keyed per **host** (one token authenticates against every repo/org the account can see on that
host — `src/CodeFlow.App/Security/CredentialStore.cs` comment), leaving room for a GitHub Enterprise host later without
changing the key shape. The AI key is keyed per **provider id** so several providers can be
configured side by side (`src/CodeFlow.App/Security/CredentialStore.cs` comment).

**These three `(SERVICE, key)` pairs are the entire on-disk/OS-store contract.** A byte-for-byte
mismatch in `SERVICE` or in any of the three format strings makes every existing user's stored
PATs/tokens/API keys unreadable after the port (the OS store looks them up by exact
service+key match; there is no migration path back).

### Read / write / delete

`src/CodeFlow.App/Security/CredentialStore.cs`:

- `entry(key) -> Entry` — `CredentialStore`(SERVICE, key)`, stringifies any
  construction error.
- `set_secret(key, value) -> void` — `entry(key)?.set_password(value)`,
  stringifies any error. No return value on success.
- `get_secret(key) -> string?` — `entry(key)?.get_password()`; a
  `CredentialStoreException`::NoEntry` is mapped to `null` (missing credential is not an error); every
  other `CredentialStoreException` is stringified and returned as `Err`.
- `delete_secret(key) -> void` — `entry(key)?.delete_credential()`; both `success`
  **and** `CredentialStoreException`::NoEntry` are mapped to `success` (deleting something that was never
  there is not an error); every other error is stringified and returned as `Err`.

### Failure semantics

`src/CodeFlow.App/Security/CredentialStore.cs` contains no filesystem I/O, no HTTP calls, and no fallback storage path of any
kind — the only thing it does is call into `CredentialStore`. There is **no plaintext fallback**:
if the platform credential store is genuinely unreachable and returns an error, that error is
stringified and propagated as `Result.an exception` through the owning command
(`src/CodeFlow.App/Security/SecretCommands.cs`), which the transport surfaces to the frontend as a rejected promise. Nothing
in this module ever swallows an error into a silent `Ok`, except the two documented
`NoEntry → Ok` mappings above, both of which represent "there is genuinely nothing stored here"
rather than a store failure.

**`DIVERGENCE-SEC-c`** — this loud-failure guarantee only holds on the platforms with a real
credential-store backend. There are exactly two:
`src/CodeFlow.App/Security/MacKeychain.cs` (Security.framework's `SecItem` API) and
`src/CodeFlow.App/Security/WindowsCredentialManager.cs` (advapi32's `Cred*` API), selected in
`CredentialStore.SelectBackend()`.

Linux has neither. Rather than degrade into a store that accepts writes and returns nothing,
`UnsupportedPlatform` throws on the first call:

`csharp
private static CredentialStoreException Fail() =>
    new($"no credential store is available on {Environment.OSVersion.Platform}. " +
        "CodeFlow targets Windows and macOS; it will not fall back to storing secrets in plaintext.");
`

That is the deliberate difference from CodeFlow 1.7.2, whose credential layer had no backend
compiled in for Linux and silently round-tripped through a non-persistent stub — writes
"succeeded", reads came back empty.

**No shipped build is affected.** `.github/workflows/release.yml`'s build matrix
(`:60-66`) contains exactly two entries — `windows-latest` and `macos-latest` with
`--target universal-apple-darwin`. The `ubuntu-latest` at `:11` is the `check-version` job,
which reads `package.json` and assembles release notes; it produces no bundle. So although
the shell sets `"targets": "all"`, that only widens the bundle formats
built *on the two matrix platforms*. There is no Linux artifact.

What remains true is a **build-time constraint**, not a defect in a shipped binary: anyone
who compiles this tree on Linux gets `keyring`'s non-persistent stub, where `set_secret`
reports success, nothing is stored, and every later `get_secret` returns `null` —
indistinguishable from "the user never saved a credential". That is the silent-success
failure mode `00-conventions.md` prohibits, and the codebase's own comment already names it.

Here this is a requirement rather than a behaviour to preserve: the design targets
Windows and macOS only, and §3 also requires the credential store to **fail loudly when the
platform store is unavailable, never falling back to plaintext and never failing silently**.
The C# implementation must therefore treat "no backend for this platform" as a hard error at
startup rather than as an empty store.

## The credential invariant

**No code path gives a credential to an AI agent (CLI) subprocess.** Established from every call
site of `CredentialStore` outside `src/CodeFlow.App/Security/CredentialStore.cs` itself (searched across `src/CodeFlow.App/`, not just this
document's files):

- `src/CodeFlow.App/Ai/AiOperations.cs` — `engine_for("openai")` reads `ai_api_key("openai")` once and embeds it in
  `OpenAiEngine { api_key }`.
- `src/CodeFlow.App/Providers/ProviderCommands.cs` — `github_authenticated_user` reads `github_token_key(host)`.
- `src/CodeFlow.App/Review/ReviewCommands.cs` — reads `github_token_key`/`ado_pat_key` for
  linking, validation and API calls.

Tracing where that value goes next:

- The OpenAI key flows into `Transport.OpenAiCompatible { api_key }` (`src/CodeFlow.App/Ai/Engines/OpenAi.cs`) and is
  attached with reqwest's `.bearer_auth(api_key)` when the the sidecar backend itself issues the HTTP
  request (`src/CodeFlow.App/Ai/Engines/OpenAi.cs` in `complete`, `src/CodeFlow.App/Ai/Engines/OpenAi.cs` in `fetch_models`) — an HTTP header on
  a request the backend process sends directly. It never becomes an environment variable or CLI
  argument.
- The GitHub/Azure DevOps tokens are used the same way: passed as function arguments into
  backend HTTP-client calls (e.g. `get_authenticated_user`(&host, &token)`), or used only
  for an `.is_some()` existence check.
- The four CLI-driven AI engines that spawn a subprocess — `src/CodeFlow.App/Ai/Engines/Claude.cs`, `src/CodeFlow.App/Ai/Engines/Gemini.cs`,
  `src/CodeFlow.App/Ai/Engines/OpenCode.cs`, `src/CodeFlow.App/Ai/Engines/Codex.cs` — and the local HTTP engine `src/CodeFlow.App/Ai/Engines/Ollama.cs` contain **zero** references
  to `CredentialStore`, `api_key`, or `token` (confirmed by grep across all five files). The only
  thing ever set on a spawned `Command` is `PATH` (`src/CodeFlow.App/Ai/AiOperations.cs`:apply_path`, `cmd.env("PATH", joined)`
  at `src/CodeFlow.App/Ai/AiOperations.cs`), so an AI CLI subprocess can find its own binary — never a credential.

So the invariant holds exactly as stated: every credential consumer is either (a) an HTTP
request built and sent by the the sidecar backend process itself, using `reqwest`'s `bearer_auth`/
header machinery, or (b) an existence check. A spawned AI agent CLI (Claude Code, Gemini,
OpenCode, Codex) never receives an app credential through its environment, its arguments, or
its stdin payload.

**`DIVERGENCE-SEC-d`** — the invariant above is about *agent subprocesses*. It now also holds for
the *frontend*, which is a change from 1.7.2 and from the port's own first pass. **No credential
command returns a secret.** All three families are `set`/`has`/`delete`.

`DIVERGENCE-SEC-a` recorded the opposite, and is superseded: 1.7.2's `get_ado_pat` and
`get_github_token` both returned `string?`, so the raw unmasked secret crossed the IPC boundary
into the webview process for those two families, while the AI-key family deliberately withheld it.
The port reproduced that asymmetry faithfully. It is now removed — see SEC-006 for the reasoning
and for why removing it cost nothing. What follows describes the behaviour up to 1.7.4 and is kept
so nobody reads the old shape as still current: `getAdoPat`
(`renderer/src/lib/ipc/commands.ts:410`) was called from `renderer/src/lib/adoConnections.ts:26`
purely for a truthiness check during a legacy single-org-to-multi-org migration, but the actual PAT
value is what travelled over IPC to make that check. `getGithubToken`
(`renderer/src/lib/ipc/commands.ts:417`) had a frontend wrapper but no
call site found in `src/lib/**/*.ts(x)` — its frontend liveness is `01-ipc-surface.md`'s call,
not this document's. This is a deliberate asymmetry between the AI-key family and the
PAT/token families that a reader expecting uniform treatment (from the AI-key comment) would not
predict; preserve it consciously in the port rather than assuming all three families behave
alike.

## Secret scanning

`src/CodeFlow.App/Security/SecretScan.cs` is a separate, deterministic (regex-only, no AI) scanner for the pre-commit
gate. It is explicitly **not** the credential store above — module doc (`src/CodeFlow.App/Security/SecretScan.cs`):
it looks at the *staged diff* and flags credentials about to be committed. Only added lines
(`origin == "+"`) are scanned; a secret already sitting in a context line was already in the
repo and is not this gate's concern. At most one hit is reported per line, to keep the report
readable.

### The 15 rules

14 `new Rule(...)`(...)` literals (`src/CodeFlow.App/Security/SecretScan.cs`) plus one `generic` rule built separately
and pushed last (`src/CodeFlow.App/Security/SecretScan.cs`) — **15 total**, not 14; a naive
`grep -c '`new Rule(...)`('` undercounts by missing the `generic` rule. Rule order is significant: for
a given added line, rules are tried in this exact order and the **first** match wins
(`src/CodeFlow.App/Security/SecretScan.cs`); an earlier rule shadows a later one on the same line. All patterns
below are `VERBATIM` — transcribed exactly as written, including the raw-string form
(`r"..."` vs `r#"..."#`).

| # | `id` | Display name | Severity | Regex pattern (verbatim) | `check_placeholder` |
|---|---|---|---|---|---|
| 1 | `private-key` | Private key (PEM) | `critical` | `r"-----BEGIN (?:RSA \|EC \|DSA \|OPENSSH \|PGP )?PRIVATE KEY-----"` | false |
| 2 | `aws-access-key` | AWS access key id | `critical` | `r"\bAKIA[0-9A-Z]{16}\b"` | false |
| 3 | `aws-secret-key` | AWS secret access key | `critical` | `r#"(?i)aws_secret_access_key\s*[:=]\s*['"]?(?P<val>[A-Za-z0-9/+=]{40})['"]?"#` | false |
| 4 | `github-token` | GitHub token | `critical` | `r"\b(?:ghp\|gho\|ghu\|ghs\|ghr)_[A-Za-z0-9]{36}\b"` | false |
| 5 | `github-pat` | GitHub fine-grained PAT | `critical` | `r"\bgithub_pat_[A-Za-z0-9_]{22,}\b"` | false |
| 6 | `google-api-key` | Google API key | `critical` | `r"\bAIza[0-9A-Za-z\-_]{35}\b"` | false |
| 7 | `slack-token` | Slack token | `critical` | `r"\bxox[baprs]-[0-9A-Za-z-]{10,48}\b"` | false |
| 8 | `slack-webhook` | Slack webhook URL | `warning` | `r"https://hooks\.slack\.com/services/[A-Za-z0-9/]+"` | false |
| 9 | `stripe-secret-key` | Stripe secret key | `critical` | `r"\bsk_live_[0-9A-Za-z]{16,}\b"` | false |
| 10 | `stripe-restricted-key` | Stripe restricted key | `critical` | `r"\brk_live_[0-9A-Za-z]{16,}\b"` | false |
| 11 | `openai-key` | OpenAI API key | `critical` | `r"\bsk-proj-[A-Za-z0-9_-]{20,}\b"` | false |
| 12 | `npm-token` | npm access token | `critical` | `r"\bnpm_[A-Za-z0-9]{36}\b"` | false |
| 13 | `azure-storage-key` | Azure storage account key | `critical` | `r"(?i)AccountKey=[A-Za-z0-9+/=]{40,}"` | false |
| 14 | `jwt` | JSON Web Token (JWT) | `warning` | `r"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b"` | false |
| 15 | `hardcoded-secret` | Hardcoded secret assignment | `warning` | `r#"(?i)(?:password\|passwd\|pwd\|secret\|api[_-]?key\|apikey\|access[_-]?token\|auth[_-]?token\|client[_-]?secret\|private[_-]?key\|token)\s*[:=]\s*['"](?P<val>[^'"\n]{8,})['"]"#` | **true** |

Only rules 3 (`aws-secret-key`) and 15 (`hardcoded-secret`) define a named `(?P<val>...)` group;
for these, `caps.name("val")` is used as the matched value (`src/CodeFlow.App/Security/SecretScan.cs`). Every other
rule — including 13 (`azure-storage-key`), whose pattern has no named group despite matching an
`AccountKey=` prefix — has no named group, so its matched value is the **whole match**
(`caps.get(0)`), prefix included where the pattern includes one.

Rule 15 is the only one with `check_placeholder = true`: its matched value is passed through
`is_placeholder` and the hit is discarded (not reported) if it looks like a template rather than
a real secret (`src/CodeFlow.App/Security/SecretScan.cs`).

### `is_placeholder` (`src/CodeFlow.App/Security/SecretScan.cs`), `VERBATIM`

`csharp
private static readonly string[] Needles =
    ["example", "changeme", "placeholder", "your-", "your_", "yourtoken", "xxxx", "todo", "<"];

internal static bool IsPlaceholder(string value)
{
    if (value.Contains("${", StringComparison.Ordinal)
        || value.Contains("{{", StringComparison.Ordinal)
        || value.Contains("process.env", StringComparison.Ordinal)
        || value.Contains("os.environ", StringComparison.Ordinal)
        || value.Contains("getenv", StringComparison.Ordinal))
    {
        return true;
    }

    var lower = value.ToLowerInvariant();

    return Needles.Any(n => lower.Contains(n, StringComparison.Ordinal));
}
`

Two independent checks, either true short-circuits to "is a placeholder":

1. The raw (not lowercased) value contains any of `${`, `{{`, `process.env`, `os.environ`,
   `getenv` — interpolation/environment-variable syntax.
2. The lowercased value contains any of the 9 needles: `example`, `changeme`, `placeholder`,
   `your-`, `your_`, `yourtoken`, `xxxx`, `todo`, `<`.

Note `<` is a bare needle — any captured value containing a literal `<` (e.g. an HTML/XML
placeholder like `<your-token>`) is treated as a placeholder regardless of context.

### `mask` (`src/CodeFlow.App/Security/SecretScan.cs`), `VERBATIM`

`csharp
internal static string Mask(string matched)
{
    var trimmed = matched.Trim();
    var runes = trimmed.EnumerateRunes().ToArray();
    var n = runes.Length;

    if (n <= 6)
    {
        return new string('•', Math.Max(n, 3));
    }

    var head = string.Concat(runes.Take(3));
    var tail = string.Concat(runes.Skip(n - 2));
    var dots = Math.Min(n - 5, 16);

    return $"{head}{new string('•', dots)}{tail}";
}
`

Algorithm, precisely:

1. Trim the matched value, count its Unicode scalar values (`chars().count()`, not bytes) as `n`.
2. If `n <= 6`: return `n.max(3)` bullet characters (`•`, U+2022) and nothing else — no original
   characters survive. (`n` between 0 and 2 still yields 3 bullets, not `n` bullets — an
   intentional floor, not a bug: it never reveals that the value was that short.)
3. Otherwise: keep the **first 3 characters** (`head`) and the **last 2 characters** (`tail`)
   verbatim; replace everything in between with `min(n - 5, 16)` bullet characters. For
   `n <= 21` this preserves the original length (`3 + (n-5) + 2 = n`); for `n > 21` the bullet
   run is capped at 16, so the masked string is shorter than the original and no longer reveals
   the secret's true length beyond "more than 21 characters."

### The scan algorithm (`src/CodeFlow.App/Security/SecretScan.cs`)

`scan_diff(files: IReadOnlyList<FileDiffInfo>) -> IReadOnlyList<SecretHit>`:

1. For each `FileDiffInfo`, the reported path is `new_path`, falling back to `old_path`, falling
   back to the literal string `"?"` if both are `None` (`src/CodeFlow.App/Security/SecretScan.cs`).
2. For each hunk, for each line: **skip immediately unless `line.origin == "+"`** — only
   newly-added content is scanned; context (`" "`) and removed (`"-"`) lines are never
   considered, regardless of what they contain.
3. For an added line, try the 15 rules **in table order**. The first rule whose regex matches
   the line's content wins; no other rule is tried for that line (`break` after the first hit,
   `src/CodeFlow.App/Security/SecretScan.cs`) — **at most one hit per line**, even if the line would match several
   rules.
4. The matched value is the named `val` capture if the rule defines one, else the whole match.
   If the winning rule has `check_placeholder = true` and `is_placeholder(value)` is true, the
   match is discarded and the **next** rule in order is tried instead (`continue`, not `break`)
   — only rule 15 (`hardcoded-secret`) sets this flag, and it is last, so in practice a
   placeholder verdict on it simply ends the line with no hit.
5. Every reported hit (`SecretHit`, `src/CodeFlow.App/Security/SecretScan.cs`) carries: `file` (the resolved path),
   `line` (`line.new_lineno.unwrap_or(0)` — 1-based line number in the new file, `0` if libgit2
   didn't report one), `rule` (the stable id, e.g. `"github-token"`), `rule_name` (the
   human-readable display name, left untranslated by design), `severity` (`"critical"` or
   `"warning"`), and `preview` (`mask(value)` — never the raw matched value).
6. `scan_diff` cannot fail — it returns `IReadOnlyList<SecretHit>` directly, never a `Result`. The owning
   command, `scan_staged_secrets`, only propagates errors from opening the repo / reading the
   diff (`src/CodeFlow.App/Files/WatcherCommands.cs` doc comment); the scan itself always succeeds, empty
   or not.

## Rules

### SEC-001 Every credential lives under one keychain service name
**Implementation**: `src/CodeFlow.App/Security/CredentialStore.cs`
**Behaviour**: All credentials (Azure DevOps PATs, GitHub tokens, AI provider API keys) are
stored in the OS-native credential store (`CredentialStore`) under the single service name
`"com.codeflow.app"`. What differs per credential is only the `key` string.
**Inputs / outputs**: `entry(key: string) -> Entry`; `CredentialStore`(SERVICE, key)`,
construction errors stringified.
**Edge cases**: none — the service name is a compile-time constant, never parameterized.
**Frontend dependency**: none directly; every stored credential is only reachable through this
service name, so any C# port must use the exact same string to read pre-existing entries.
**Markers**: `VERBATIM`

### SEC-002 Deterministic per-credential key formats
**Implementation**: `src/CodeFlow.App/Security/CredentialStore.cs`
**Behaviour**: Three formatter functions build the `key` half of the `(SERVICE, key)` pair:
`ado_pat_key(org) = format!("ado-pat:{org}")`, `github_token_key(host) = format!("github-token:{host}")`,
`ai_api_key(provider) = format!("ai-api-key:{provider}")`. Azure DevOps is keyed per org
(a PAT is org-scoped); GitHub is keyed per host (a token is account-wide across every repo/org
that host serves, and this shape leaves room for a GitHub Enterprise host later); the AI key is
keyed per provider id (several providers configurable side by side).
**Inputs / outputs**: `org: string` / `host: string` / `provider: string` in; a `string` key out. No
validation, sanitization, or escaping of the input — whatever string is passed becomes part of
the key verbatim (e.g. a colon in `org` would produce a key with two colons; nothing prevents
this).
**Edge cases**: empty string input produces `"ado-pat:"` / `"github-token:"` / `"ai-api-key:"` —
a syntactically valid but semantically empty key; not specially handled.
**Frontend dependency**: `setAdoPat`/`getAdoPat`/`deleteAdoPat`, `setGithubToken`/`getGithubToken`/
`deleteGithubToken`, `setAiApiKey`/`hasAiApiKey`/`deleteAiApiKey` (`renderer/src/lib/ipc/commands.ts`) —
see `01-ipc-surface.md` for signatures.
**Markers**: `VERBATIM` — reproducing these three format strings byte-for-byte is mandatory; any
deviation orphans every existing user's stored credentials.

### SEC-003 `NoEntry` is not an error; every other keyring error propagates
**Implementation**: `src/CodeFlow.App/Security/CredentialStore.cs`
**Behaviour**: `get_secret` maps `CredentialStoreException`::NoEntry` to `null`; `delete_secret` maps
both success and `NoEntry` to `success`. Every other `CredentialStoreException` (from `set_secret`,
`get_secret`, or `delete_secret`) is stringified via `.to_string()` and returned as `Err`.
**Inputs / outputs**: `set_secret(key, value) -> void`,
`get_secret(key) -> string?`, `delete_secret(key) -> void`.
**Edge cases**: deleting a credential that was never stored succeeds silently (by design — it's
treated as already-satisfied, not as an error).
**Frontend dependency**: all 9 `src/CodeFlow.App/Security/SecretCommands.cs` commands propagate this `_`
directly to the frontend as a resolved value or a rejected promise.
**Markers**: none

### SEC-004 No plaintext fallback; store failures are loud — except on Linux
**Implementation**: `src/CodeFlow.App/Security/CredentialStore.cs` (whole file)
(`keyring` package dependencies)
**Behaviour**: `src/CodeFlow.App/Security/CredentialStore.cs` has no filesystem or alternate storage path — every operation goes
through `CredentialStore`, and any error that isn't the two documented `NoEntry` cases is
propagated as `an exception` all the way to the frontend. On macOS and Windows this is backed by
a real OS credential store, so a genuine store failure (locked keychain, permission denial,
etc.) is loud. On Linux there is no backend at all, and `UnsupportedPlatform` throws rather than
pretending: this is the one place the behaviour deliberately differs from 1.7.2, which fell back
to a non-persistent stub where writes reported success, were not persisted, and every read
afterwards came back empty — indistinguishable from "never saved." Linux is not a release target
(`.github/workflows/release.yml`,
`shell/src/main.ts`'s `"targets": "all"`).
**Inputs / outputs**: n/a (platform/build-configuration behaviour, not a function signature).
**Edge cases**: this affects every one of the 9 `src/CodeFlow.App/Security/SecretCommands.cs` commands identically on Linux —
`set_*` always reports success, `get_*`/`has_*` always report "not set" afterwards, `delete_*`
always reports success (there was never anything to delete).
**Frontend dependency**: every UI flow that saves a PAT/token/API key and expects it to persist
across app restarts — but only in a build made on a platform with no backend compiled in.
**Markers**: `DIVERGENCE-SEC-c`. **No shipped build is affected**: `release.yml`'s build matrix
is `windows-latest` and `macos-latest` only (the `ubuntu-latest` job computes the version and
release notes and produces no bundle), and both have a real backend compiled in. This is a
build-time constraint on CodeFlow 1.7.2, not a defect in a distributed binary.
For the port it converts into a requirement rather than a behaviour to reproduce:
Failing loudly when the platform store is unavailable is mandatory, so the C#
credential store must treat "no backend for this platform" as a hard startup error rather than
as an empty store.

### SEC-005 Commands are thin wrappers; only the AI-key family withholds the raw secret
**Implementation**: `src/CodeFlow.App/Security/SecretCommands.cs`
**Behaviour**: All 9 commands call straight through to `CredentialStore`/`get_secret`/
`delete_secret` with the matching key formatter — no additional logic. `has_ai_api_key`
(`src/CodeFlow.App/Security/SecretCommands.cs`) is the one command that doesn't simply forward a store call:
it calls `get_secret`, then `.filter(|k| !k.trim().is_empty())`, then `.is_some()`, converting a
possibly-whitespace-only stored value into a clean boolean. There is deliberately no
`get_ai_api_key` command — the AI key is only ever read backend-side (`src/CodeFlow.App/Ai/AiOperations.cs`), so it never
crosses into the frontend at all; `has_ai_api_key` is Settings' only way to know one is
configured.
**Inputs / outputs**: see `01-ipc-surface.md` for full signatures.
**Edge cases**: a stored AI key that is empty or all-whitespace is treated by `has_ai_api_key` as
"not set," even though `get_secret` would return `Ok(Some(""))` for it.
**Frontend dependency**: `hasAiApiKey`, `setAiApiKey`, `deleteAiApiKey`
(`renderer/src/lib/ipc/commands.ts:242-247`).
**Markers**: none

### SEC-006 `has_ado_pat`/`has_github_token` answer existence without returning the secret
**Implementation**: `src/CodeFlow.App/Security/SecretCommands.cs`
**Behaviour**: Like the AI-key family (SEC-005), the Azure DevOps and GitHub families expose only
`set`/`has`/`delete`. No stored credential value crosses the IPC boundary into the webview process.
**Inputs / outputs**: `has_ado_pat(org: string) -> bool`, `has_github_token(host: string) -> bool`.
**Edge cases**: a stored value that is empty or all-whitespace is treated as absent, matching
`has_ai_api_key`. A credential-store failure still throws rather than answering `false` — the
loud-failure guarantee (SEC-002) is unchanged, and "the store is broken" must not read as
"no PAT is saved."
**Frontend dependency**: `hasAdoPat` (`renderer/src/lib/ipc/commands.ts`), called from
`renderer/src/lib/adoConnections.ts` for the legacy single-org migration check. `hasGithubToken`
has a wrapper and no call site — kept for symmetry, and because Settings is the obvious future
caller.
**Markers**: `DIVERGENCE-SEC-d`, superseding `DIVERGENCE-SEC-a`.

**Why this one was closed when `91-known-bugs.md`'s rule is not to fix things silently.** It is not
being fixed silently — this is the decision recorded, with its own test and its own release note,
the same way `BUG-STORE-a` and the other three were closed.

The decision was cheap because **nothing used the values**. `get_github_token` had no caller in the
renderer at all. `get_ado_pat` had exactly one, and it used the PAT as a boolean: `if (pat) return
[{ org: legacyOrg }]`. So the plaintext travelled over IPC to answer a question a `bool` answers.

What it buys: the renderer renders untrusted content (repo markdown, AI output, PR comments)
through `dangerouslySetInnerHTML`, and the preload's `invoke` is a generic passthrough to the whole
command surface. Before this change, an XSS in that path could read a GitHub token or an ADO PAT
out of the OS keychain. After it, there is no command that would answer. That is a structural
property rather than a mitigation, which is the same standard SEC-007 is held to.

**Compatibility**: an existing 1.7.4 install is unaffected — the stored credentials are untouched
and the renderer half ships in the same build. A renderer built before this change and run against
a sidecar built after it would fail the legacy ADO migration check, which is not a combination the
app ever produces (both ship in one bundle).

### SEC-007 The credential invariant: no AI agent subprocess ever receives a credential
**Implementation**: `src/CodeFlow.App/Ai/AiOperations.cs` (`engine_for`), `src/CodeFlow.App/Ai/Engines/OpenAi.cs`, `src/CodeFlow.App/Ai/AiOperations.cs`
(spawn/PATH setup), plus zero matches for `CredentialStore`/`api_key`/`token` in `src/CodeFlow.App/Ai/Engines/Claude.cs`,
`src/CodeFlow.App/Ai/Engines/Gemini.cs`, `src/CodeFlow.App/Ai/Engines/OpenCode.cs`, `src/CodeFlow.App/Ai/Engines/Codex.cs`, `src/CodeFlow.App/Ai/Engines/Ollama.cs`
**Behaviour**: The only AI engine that reads a stored credential is the HTTP-based `openai`
engine: `engine_for("openai")` reads `ai_api_key("openai")` once and embeds it in
`OpenAiEngine { api_key }` (`src/CodeFlow.App/Ai/AiOperations.cs`); that value is attached as a bearer header
(`reqwest`'s `.bearer_auth(api_key)`, `src/CodeFlow.App/Ai/Engines/OpenAi.cs`) on HTTP requests the the sidecar backend
process itself sends. The four CLI-driven engines that spawn a child process — Claude Code,
Gemini, OpenCode, Codex — and the local HTTP engine Ollama never reference a stored credential
at all; the only environment variable ever set on a spawned `Command` is `PATH`
(`src/CodeFlow.App/Ai/AiOperations.cs`:apply_path`, `cmd.env("PATH", joined)` at `src/CodeFlow.App/Ai/AiOperations.cs`), so the child can locate its own
binary. GitHub/Azure DevOps tokens follow the identical backend-HTTP-only pattern
(`src/CodeFlow.App/Providers/ProviderCommands.cs`, `src/CodeFlow.App/Review/ReviewCommands.cs`).
**Inputs / outputs**: n/a — cross-cutting architectural invariant, not a single function.
**Edge cases**: none identified; the invariant held across every `CredentialStore.Get` call site
found in the whole the shell tree.
**Frontend dependency**: none — this invariant is specifically about backend-to-subprocess data
flow, not about the frontend (see SEC-006 for the separate, frontend-facing exposure).
**Markers**: none — established fact, not ambiguous.

### SEC-008 `scan_diff` is a pure function over already-parsed diff data
**Implementation**: `src/CodeFlow.App/Security/SecretScan.cs`
**Behaviour**: `scan_diff(files: IReadOnlyList<FileDiffInfo>) -> IReadOnlyList<SecretHit>` never fails and does no I/O;
it only iterates the given `FileDiffInfo`/`DiffHunkInfo`/`DiffLine` structures (produced
elsewhere, by `src/CodeFlow.App/Git/`::get_staged_diff`). For each file, the reported path prefers
`new_path`, falls back to `old_path`, falls back to `"?"` if both are absent.
**Inputs / outputs**: input is `IReadOnlyList<FileDiffInfo>` (`src/CodeFlow.App/Git/Diff.cs`: `old_path: string?`,
`new_path: string?`, `status: string`, `hunks: IReadOnlyList<DiffHunkInfo>` → `header: string`,
`lines: IReadOnlyList<DiffLine>` → `origin: string`, `content: string`, `old_lineno: uint?`,
`new_lineno: uint?`); output is `IReadOnlyList<SecretHit>` (`src/CodeFlow.App/Security/SecretScan.cs`).
**Edge cases**: both `old_path` and `new_path` absent → file reported as `"?"`. Empty input
slice → empty output vector.
**Frontend dependency**: consumed only through `scan_staged_secrets`; see SEC-011.
**Markers**: none

### SEC-009 Only added lines are scanned; at most one hit per line
**Implementation**: `src/CodeFlow.App/Security/SecretScan.cs`
**Behaviour**: A `DiffLine` is scanned only if `line.origin == "+"`; context (`" "`) and removed
(`"-"`) lines are skipped unconditionally regardless of content. Within an added line, the 15
rules (see the "Secret scanning" table above) are tried in table order; the first rule that
matches wins and no further rule is tried for that line (`break`) — so a line matching several
rule patterns is reported under only its first-in-order match.
**Inputs / outputs**: see SEC-008.
**Edge cases**: rule 15 (`hardcoded-secret`) is the only one with `check_placeholder = true`; if
its match is judged a placeholder, the loop `continue`s to the next rule instead of breaking —
but since it is last in table order, this simply means the line ends with no hit.
**Frontend dependency**: `scanStagedSecrets` (`renderer/src/lib/ipc/commands.ts:166`).
**Markers**: none

### SEC-010 15 deterministic secret-detection rules
**Implementation**: `src/CodeFlow.App/Security/SecretScan.cs`
**Behaviour**: 14 `new Rule(...)`(...)` literals plus one `generic` rule (`id = "hardcoded-secret"`)
built separately and pushed last — 15 total. Full id/name/severity/regex/`check_placeholder`
table is transcribed verbatim in the "Secret scanning" section above. Values captured by a
named `(?P<val>...)` group (rules `aws-secret-key`, `hardcoded-secret`) are used as the matched
value; all other rules use the whole match.
**Inputs / outputs**: `Regex.new(pattern)` — a compile failure panics at first use
(`rules().get_or_init`; `unwrap_or_else(|e| panic!(...))`, `src/CodeFlow.App/Security/SecretScan.cs`), since patterns
are static and covered by tests; not a runtime error path.
**Edge cases**: a diff line matching multiple rule patterns is reported under whichever rule
comes first in the fixed table order.
**Frontend dependency**: `SecretHit.rule`/`rule_name`/`severity` drive the UI's report rendering
(exact consumer is outside this document's owned files).
**Markers**: `VERBATIM` for every regex pattern.

### SEC-011 `is_placeholder` needle list
**Implementation**: `src/CodeFlow.App/Security/SecretScan.cs`
**Behaviour**: A value is a placeholder if it contains any of `${`, `{{`, `process.env`,
`os.environ`, `getenv` (raw, case-sensitive check), **or** its lowercased form contains any of
the 9 needles `example`, `changeme`, `placeholder`, `your-`, `your_`, `yourtoken`, `xxxx`,
`todo`, `<`. Only applied to the `hardcoded-secret` rule's captured value.
**Inputs / outputs**: `is_placeholder(v: string) -> bool`.
**Edge cases**: the bare `<` needle matches any captured value containing that character for
any reason (e.g. a real secret that happens to be adjacent to an HTML tag on the same line,
inside the 8+-character capture window) — not specially handled, would suppress a genuine hit.
**Frontend dependency**: none directly (affects whether a `SecretHit` is ever produced).
**Markers**: `VERBATIM`

### SEC-012 `mask` preview algorithm
**Implementation**: `src/CodeFlow.App/Security/SecretScan.cs`
**Behaviour**: counts Unicode scalar values (`chars().count()`), not bytes. `n <= 6` → `n.max(3)`
bullet characters (`•`) and nothing else. `n > 6` → first 3 chars + `min(n-5, 16)` bullet
characters + last 2 chars; length-preserving up to `n = 21`, capped/shortened beyond that.
**Inputs / outputs**: `mask(matched: string) -> string`.
**Edge cases**: `n` in `0..=2` still yields exactly 3 bullets (never fewer), intentionally not
revealing that the value was shorter than 3 characters.
**Frontend dependency**: `SecretHit.preview` — the only representation of the matched value the
UI (and this document) ever sees; the true matched value never leaves `src/CodeFlow.App/Security/SecretScan.cs`.
**Markers**: `VERBATIM`

### SEC-013 `scan_staged_secrets` never fails on the scan itself
**Implementation**: `src/CodeFlow.App/Files/WatcherCommands.cs`
**Behaviour**: Opens the repo and reads its staged diff (`get_staged_diff(&repo_path)`,
propagating any error from that step as `an exception`), then calls `scan_diff` — which cannot
fail — and always returns `Ok(IReadOnlyList<SecretHit>)` from that point on, empty when the staged
changes look clean.
**Inputs / outputs**: `scan_staged_secrets(repo_path: string) -> IReadOnlyList, string>`.
**Edge cases**: a repo path that doesn't exist or isn't a git repo surfaces as an `Err` from the
`get_staged_diff` step, before any scanning happens.
**Frontend dependency**: `scanStagedSecrets` (`renderer/src/lib/ipc/commands.ts:166`).
**Markers**: none

### SEC-014 API client auth is plaintext JSON in SQLite, not the keychain
**Implementation**: `src/CodeFlow.App/Storage/Migrations.cs` (`api_collections`), `src/CodeFlow.App/Storage/Migrations.cs`
(`api_requests`)
**Behaviour**: The app's own credentials (Azure DevOps PATs, GitHub tokens, AI provider keys) go
through the keychain (SEC-001–SEC-004). The bundled API client's per-collection and per-request
auth configuration does not: `api_collections.auth` is a `TEXT NOT NULL DEFAULT ''` column
holding a JSON `AuthConfig` blob (`src/CodeFlow.App/Storage/Migrations.cs` comment: `'' = nothing configured
(children fall through to "none")`), and `api_requests.spec` is a `TEXT NOT NULL DEFAULT '{}'`
column holding a JSON `ApiRequestSpec` blob that embeds — among params, headers, body and
protocol settings — that request's own `auth` (`src/CodeFlow.App/Storage/Migrations.cs` comment). Both are
stored as plain SQLite `TEXT`, unencrypted, in the app's local database file. `api_folders.auth`
(`src/CodeFlow.App/Storage/Migrations.cs`) follows the same pattern for folder-level auth inheritance.
**Inputs / outputs**: n/a — schema-level fact, not a function.
**Edge cases**: none — this is the storage model for every collection/folder/request's
configured auth (Basic, Bearer, API key, etc.) used by the API-client feature, unconditionally.
**Frontend dependency**: the API client feature (owned by another document) reads/writes these
columns directly; out of scope here beyond flagging the asymmetry.
**Markers**: `DIVERGENCE-SEC-b` — this is a pre-existing, deliberate-looking asymmetry (the
column comments show it was a conscious schema choice, not an oversight) between "the app's own
credentials" (keychain) and "credentials the user configures for API requests they build"
(plaintext SQLite). Recorded so the port makes a conscious decision — matching it, or upgrading
API-client auth storage to the credential store — rather than silently inheriting whichever
behavior falls out of a straight port.

## Test coverage

| extracted case | Source | Fixture | Kind |
|---|---|---|---|
| `roundtrip` | `src/CodeFlow.App/Security/CredentialStore.cs` | — | behavioural |
| `detects_github_token` | `src/CodeFlow.App/Security/SecretScan.cs` | `secret_scan.vectors.json#detects-github-token` | vector |
| `detects_aws_and_private_key` | `src/CodeFlow.App/Security/SecretScan.cs` | `secret_scan.vectors.json#detects-aws-access-key`, `secret_scan.vectors.json#detects-private-key-pem-header` | vector |
| `ignores_context_lines` | `src/CodeFlow.App/Security/SecretScan.cs` | `secret_scan.vectors.json#ignores-context-lines` | vector |
| `skips_placeholders` | `src/CodeFlow.App/Security/SecretScan.cs` | `secret_scan.vectors.json#skips-placeholder-your-prefix`, `secret_scan.vectors.json#skips-placeholder-env-var-interpolation` | vector |
| `flags_real_hardcoded_password` | `src/CodeFlow.App/Security/SecretScan.cs` | `secret_scan.vectors.json#flags-real-hardcoded-password` | vector |
| `clean_line_has_no_hits` | `src/CodeFlow.App/Security/SecretScan.cs` | `secret_scan.vectors.json#clean-line-has-no-hits` | vector |

`src/CodeFlow.App/Security/SecretCommands.cs` and `src/CodeFlow.App/Files/WatcherCommands.cs` carry no ` functions — 7
tests total across this document's files.

### `roundtrip` acceptance checklist (behavioural — needs a real OS keychain)

`src/CodeFlow.App/Security/CredentialStore.cs` calls `set_secret`, `get_secret`, then `delete_secret` against a real
platform credential store; it cannot be faked deterministically in a data file (per
`test-vectors/README.md`'s "not extracted" category) and, per SEC-004, its outcome is
platform-dependent (silently vacuous on an unconfigured Linux build). Phase 3 acceptance
checklist for the ported equivalent:

- [ ] Storing a credential under a fresh key, then reading it back in the same process, returns
      the exact value stored (byte-for-byte, including any leading/trailing whitespace).
- [ ] Reading a key that was never stored returns "not found" (the port's equivalent of `null`),
      not an error.
- [ ] Deleting a key that was never stored succeeds without error (idempotent delete).
- [ ] Deleting a previously-stored key, then reading it back, returns "not found."
- [ ] On the target OS's real credential store (macOS Keychain / Windows Credential Manager /
      the chosen Linux backend), the value persists across process restarts — not just within
      the same process.
- [ ] The credential is discoverable in the OS's own credential-management UI (Keychain
      Access.app / Credential Manager) under the exact service name and key used, confirming
      the store, not an in-memory stand-in, was used.

## Markers raised

| Local id | Kind | Summary |
|---|---|---|
| `DIVERGENCE-SEC-c` | `DIVERGENCE` | A `keyring` backend is compiled in only for Windows and macOS. Those are the only two platforms in `release.yml`'s build matrix, so no shipped build is affected — but a build made anywhere else silently no-ops every credential write. Reclassified from `BUG` after verifying the release matrix. See SEC-004. |
| ~~`DIVERGENCE-SEC-a`~~ | **SUPERSEDED** | Was: `get_ado_pat`/`get_github_token` return the raw stored secret to the frontend, unlike the AI-key family which deliberately withholds it. Closed by `DIVERGENCE-SEC-d`. |
| `DIVERGENCE-SEC-d` | `DIVERGENCE` | Both `get_*` credential commands were **removed** in favour of `has_ado_pat`/`has_github_token`. No command of any family returns a secret now. Neither `get` was carrying its weight — one had no caller, the other used the value as a boolean — so the plaintext left the sidecar for nothing. See SEC-006. |
| `DIVERGENCE-SEC-b` | `DIVERGENCE` | The API client stores per-collection/per-request auth as plaintext JSON in SQLite (`api_collections.auth`, `api_requests.spec`), while the app's own credentials go through the OS keychain. See SEC-014. |
