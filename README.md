# CodeFlow

A desktop code-review and API workbench: **Electron shell + C#/.NET 10 sidecar + React 19
renderer**. Git, AI-assisted pull-request review across six engines, an integrated terminal, and a
Postman-style API client, in one window.

`docs/business-rules/` is the specification the code must satisfy. Where the code and a document
disagree, that is a defect in one of them — see [The documentation is not optional
reading](#the-documentation-is-not-optional-reading).

## Prerequisites

| Tool | Version | Pinned by |
|---|---|---|
| .NET SDK | 10.0.302 | `global.json` (`rollForward: latestPatch`) |
| Node | 24 | `.github/workflows/ci.yml` |
| pnpm | 11.20.0 | `packageManager` in `renderer/package.json` and `shell/package.json` |

There is no `package.json` at the repository root — this is a .NET solution with two independent
pnpm packages inside it, each with its own lockfile.

## Layout

```
CodeFlow.slnx              solution (.slnx — the SDK 10 format)
global.json                pins the SDK to 10.0.302
Directory.Build.props      net10.0, nullable, warnings as errors
Directory.Packages.props   central package versions, pinned exactly
src/CodeFlow.App/          the sidecar — one executable, feature folders
tests/CodeFlow.Tests/      xunit v3, folders mirroring the app
shell/                     the Electron shell — window, tray, menu, IPC bridge
renderer/                  the React 19 UI (Vite, Tailwind 4, zustand)
scripts/                   release.sh, build-app.sh, publish-release.sh — the real runner
docs/                      the specification the code must satisfy
```

`src/CodeFlow.App/Program.cs` holds composition and transport only. Every command handler body
lives in its feature folder — that rule is what keeps a feature findable in one place.

## Build and test

```sh
dotnet build CodeFlow.slnx      # warnings are errors
dotnet test  CodeFlow.slnx
```

## Building an installable app

Three build products have to arrive together — this repository's renderer, shell and sidecar are
three independent projects — so one script owns the order:

```sh
scripts/build-app.sh mac        # .dmg and .zip, arm64
scripts/build-app.sh win        # NSIS installer and portable .exe, x64
scripts/build-app.sh mac --dir  # unpacked, no installer: the fast loop
```

Output lands in `dist-installers/`. The sidecar is published **self-contained**, so the machine
running CodeFlow needs no .NET runtime — but it does need **Node on PATH**, because installing a
skill shells out to `npx`.

A macOS installer can only be built on macOS: the `.dmg` target depends on `hdiutil`. Both
installers are built by `.github/workflows/ci.yml` on their own runners, which is also the only
Windows this project has ever run on. Building either here is for looking at it before it ships.

Rebuilding a `.dmg` **deletes the previous one first** — only for the platform being built, and only
after the renderer, shell and sidecar have all compiled, so a failed build never leaves the machine
with nothing installable. Two macOS builds are over 600 MB between their `.dmg` and `.zip` files;
they used to accumulate.

### Neither build is signed

Signing needs paid certificates that this project does not have, so both platforms will refuse the
app until you tell them not to:

- **macOS** — open it once and let Gatekeeper refuse, then go to **System Settings → Privacy &
  Security** and press **Open Anyway**. The old Control-click → Open shortcut was removed in macOS
  Sequoia. You are asked once per app.
- **Windows** — SmartScreen says "Windows protected your PC"; press **More info** and then
  **Run anyway**.

Making that go away means an Apple Developer Program membership (99 USD/year, for the Developer ID
certificate and notarisation) and a Windows OV certificate (from ~219 USD/year, whose private key
must live in an HSM or token since 2023). Both are external prerequisites with real lead times;
the release workflow leaves marked places for them.

One extra check worth running:

```sh
# the three native-backed dependencies actually load on this machine
dotnet run --project src/CodeFlow.App -- --smoke-test
```

## Releasing

```sh
scripts/release.sh              # patch: 1.9.1 -> 1.9.2
scripts/release.sh minor        # 1.10.0
scripts/release.sh 2.0.0        # an explicit version
scripts/release.sh --dry-run    # print the plan, write nothing
```

One command, and nothing left over. It refuses unless you are on `main`, clean, in sync, and every
check that ran **on the head of `main`** is green — asked of the commit, not of the workflows, so a
green run of an earlier commit is not an answer, which is how a red `main` got merged past once.
With CI switched off no check runs at all, and it says so before falling through to the local suite.
It then builds and tests everything here, bumps `shell/` and `renderer/` and pushes that commit,
hands the build and upload to `publish-release.sh`, waits out the Windows job, and refuses to call it
done until the release carries the `.dmg`, the `.exe` and **both `.sha256` files** — the updater will
not install an installer with no digest beside it. `--fast` skips the local suite, and is refused
outright when no check verified the commit either: that combination would verify nothing.

If something fails after the version is pushed, running it again with the same version resumes:
the bump is skipped when it is already there.

The layer underneath stays usable on its own, and is what does the actual assembling:

```sh
scripts/publish-release.sh v1.9.2
```

The two installers come from two machines and neither can produce the other's, so a release is
assembled rather than built in one place. That script checks the tag against the version in
`shell/package.json` (a mismatch produces an update the installed build reports as still available,
forever), requires a clean working tree, builds the macOS `.dmg` locally, pushes the tag — which
starts the pipeline in `.github/workflows/ci.yml` — creates the GitHub release if that pipeline has
not already, and uploads the `.dmg`. It remains the manual path; the usual one is simply to merge a
version bump to `main` and let `ci.yml` build and publish both installers.

## The documentation is not optional reading

`docs/` is the output of two completed phases and is the only specification this application has:

- **`docs/BUSINESS_RULES.md`** — index into `docs/business-rules/`: 218 commands, 13 events,
  18 tables, 6 AI engines, 6 protocols, documented across ~11 000 lines.
- **`docs/business-rules/90-ambiguities.md`** — what is unsettled, and what has never been
  executed against a real external system. Read before assuming.
- **`docs/business-rules/91-known-bugs.md`** — 22 behaviours **preserved for 1.7.2 compatibility,
  not fixed**. The renderer and existing installs may depend on them.

Before implementing a feature, read its domain document, then `90` and `91`.

## Conventions that are load-bearing

- **The behaviours in `91-known-bugs.md` are preserved, not fixed.** A silent correction changes
  the application for every existing install.
- **Verbatim content stays verbatim** — prompts, regexes, keychain key formats, error-string
  prefixes the frontend parses. `docs/business-rules/13-cross-language-contracts.md` lists every
  literal duplicated across C# and TypeScript; they break silently if paraphrased.
- **No credential ever reaches an AI agent process.** This is structural, not advisory.
- **The credential store fails loudly.** Never a plaintext fallback, never a silent empty store.
- Package versions are pinned exactly and verified against the registry, never recalled.

## Status

Verified on macOS arm64. **Windows is no longer verified by anything.** For a while its suite ran on
every pull request — 1 167 tests on a `windows-2025` runner, because there is still no Windows
machine here — and that was already narrower than it sounded: the suite passing is not the same as
the application having been *used* there. Nobody has opened the window, typed in the terminal or run
a review on Windows. What CI establishes is narrower and worth stating exactly: `ci.yml` builds the
Windows installer on a Windows runner, so the whole tree still has to *compile* there. It runs no
tests anywhere, so the four Windows-exclusive tests (`CredentialStoreTests` and company) — which a
macOS machine skips — are covered by nothing at all.

The first run found three, one of them a real leak in shipped code: every rendered Azure diff left
its throwaway repository behind, because git writes loose objects read-only and Windows refuses to
delete a read-only file — an exception `Discard` was swallowing. Unix never noticed, since permission
to unlink comes from the containing directory. That is the class of thing this buys.

| Done | |
|---|---|
| Scaffold | Solution, pinned toolchain, dependency smoke tests |
| Shell and IPC | Electron window, tray, native menu; two-channel length-prefixed transport; the renderer speaking to it through `renderer/src/lib/bridge/` |
| Storage and secrets | 18 tables, 20 migrations, OS keychain via P/Invoke |
| Phase 2 spike | PTY, agent streaming, Velopack packaging (the applied macOS update needs signing, which needs an Apple Developer ID) |
| Git | All 44 commands, both events, over LibGit2Sharp with the four network operations shelling out to `git` |
| Workspaces | Workspaces, projects, settings, prompt overrides, review contexts, MCP servers, the SDD agent roster, and the eight-task AI routing cascade |
| Agent adapters | All six engines behind one interface, binary discovery over the known install directories, model discovery over its three strategies, provider probes |
| Chat and history | The six AI operations, the run lifecycle behind them, the conversation transcript and the job list |
| GitHub provider | The REST client, the host dispatch, linking, and the PR panel's read and act commands |
| Azure DevOps provider | The REST client, the diff Azure has no endpoint for, the manual link dialog, and one `IPullRequestHost` over both providers |
| Review engine | Both review entry points, the finding parser and its reconciliation, the `review_runs` memory and the seven commands that manage it |
| Review publishing | The eight comment-writing calls on the two hosts, one thread per finding for a pull request's whole life, and both posting commands |
| Terminal | Sessions over a real pseudo-terminal, Git Bash resolved through `git --exec-path` on Windows, and output ordered ahead of the exit that follows it |
| Files and search | The explorer's file operations behind their two distinct traversal guards, and one gitignore-pruning walk under "go to file", find-in-project and repo-wide replace |
| Watcher and secrets | The working-tree watcher's leading-edge-with-catch-up throttle, and the fifteen-rule scanner on the pre-commit gate |
| Skills | A workspace's skill store, its in-app editor, and the sync that puts enabled skills where the engines can find them |
| API collections | The API tester's tree, environments, request history and cookie jar |
| API transport | Sending HTTP and GraphQL, with Digest, AWS SigV4, hand-followed redirects, multipart streaming and cancellation |
| API streaming | WebSocket, Socket.IO framed by hand, and MQTT 3.1.1 and 5.0 |
| Launching CLIs from a bundled app | The login shell's `PATH`, read once at startup, and every binary resolved to a full path before it is spawned |
| Updates | Reading this repository's releases with a credential the user already has, and handing over the artefact each platform can actually use |
| Renderer tests | Vitest over the pure logic that has consequences: the AI reviewer's output, variable precedence, and the anchor pattern's contract with the sidecar |

**Slice 14 was mostly already done, and saying so is more useful than doing it again.** It was
scoped as "the editor surface, localisation and the remaining UI wiring", and an audit against the
reference found the twelve editor components and `translations.ts` byte-identical to it, every
command they invoke registered, and every event they subscribe to emitted. Both locales carry the
same 1 330 keys. What it was actually missing was anything holding that in place, so what landed is
the check rather than the feature: `renderer/scripts/i18n-parity.test.mjs`, because a key missing
from `es` falls back to English and nothing else would ever notice.

**The renderer has its own test suite.** Vitest is one dev dependency, `environment: "node"`,
no jsdom and no testing-library: this covers pure logic, not components, and says so. It reads the
existing `vite.config.ts`, which matters because `renderer/src` is full of extensionless imports
that Node's own resolver rejects — that is why `node --test` was not enough, and why the i18n check
(which reads its file as text) was the one test that could exist before.

So v1's remaining gaps are the two features §3.4 defers — dynamic gRPC and the debuggers — and
signing. The debug panel and the gRPC panel are mounted and their commands do not exist, so both
answer `unknown command` when used; neither fires on its own.

**Four of the twenty-two preserved behaviours are now closed.** Preserving a behaviour is the
default, and changing one is a decision taken deliberately with its own test. The four chosen are
the ones that lose data, refuse to start, or weaken transport security —
`BUG-STORE-a`, `BUG-REVIEW-a`, `BUG-REVIEW-b` and `BUG-API-d`. Each has its own test and its row in
`91-known-bugs.md` struck through with the reasoning kept. The other eighteen are still preserved;
`BUG-REVIEW-a`'s Azure half is among them, and says why.

### Known gaps

**Two divergences from CodeFlow 1.7.2, both to keep observable behaviour the same rather than change it.**

`XLANG-AI-a`: passing a bare command name to the OS and letting it search the child's `PATH` is
what 1.7.2 did, and it cannot work here. `Process.Unix.cs`'s `ResolvePath` falls through to
`FindProgramInPath`, which reads `Environment.GetEnvironmentVariable("PATH")` — the *parent's*
`PATH`, never the `ProcessStartInfo.Environment` prepared for the child. A CLI in an install
directory that is off the app's own `PATH` therefore probed as found and then failed to launch. So
`ResolveBinary` now resolves to a full path on every platform, not only on Windows.

The login shell's `PATH` is read once at startup, which 1.7.2 never did. The fixed directories the
CLI installers use are covered; no version manager's are — mise, nvm, asdf, volta and fnm all put
their binaries behind a version number nobody can guess. The gap only shows in a bundled app,
because a macOS app opened from Finder inherits launchd's `PATH` and never reads a shell profile,
while a dev server runs from a terminal that did.
Measured at ~500 ms on a profile with a version manager in it, overlapped with window creation, and
skipped entirely on Windows.

**The updater checks and downloads; on macOS it cannot install.** A signed manifest on a public
repository is not available here: this repository is private, so nothing can be read anonymously,
and the app is unsigned, so there is no key to verify a replacement with. What replaced it reads the release list with a credential
the user already has — the GitHub token in the OS keychain, else `gh auth token` — so **no token is
embedded in the app or committed**. Windows runs the NSIS installer and restarts into the new build;
macOS opens the `.dmg` and says to drag it, because replacing a running unsigned `.app` in place
leaves a bundle Gatekeeper has no record of.

**What is verified, and what is not.** Every release publishes a `<installer>.sha256` beside each
artefact, and the app hashes what it downloaded and refuses anything that does not match — deleting
the file rather than leaving a rejected installer in Downloads. A release carrying no digest is
refused outright. This is not a code signature and does not pretend to be one: it moves the trust
from "whatever that HTTPS response contained" to "the bytes the release recorded", so a substitution
has to beat two uploads instead of one. Signing still needs a certificate whose private key lives in
an HSM, which the project does not have. The old panel claimed updates "only work in the
installed app", which was never true — the check was a stub that always threw. **Nothing here has
been run against a real release**, because none has been published yet: today the check reaches
GitHub and is answered `404`, which is reported as "no release has been published yet".

**Installing a skill from skills.sh has never been run here.** It shells out to `npx skills add`,
which needs the network, so the tests cover how the command line is built — including the `cmd /C`
shim Windows needs — and not a real install. Everything else about skills is exercised end to end.

**Everything that publishes to a host is `UNVERIFIED`:** submitting a review, closing a pull
request, casting an Azure reviewer vote, abandoning one, and every comment, reply and thread-status
write the review pipeline makes. They compile and are covered by tests against a fake transport;
none has ever run against a real API. See `docs/business-rules/90-ambiguities.md`.

**Azure's diff is rendered locally.** Azure DevOps has no endpoint that returns a pull request's
diff as text, so each changed file's two blobs are fetched and the diff is rendered here with
libgit2. LibGit2Sharp wraps no blob-to-blob patch that can name a path, so both sides are written
into a throwaway bare repository and compared tree to tree.
`src/CodeFlow.App/Providers/Azure/UnifiedPatch.cs` explains the one rule that keeps the output
correct.

**The API tester stores its credentials in plain text.** A request's auth configuration, a
collection's variables and a history entry's snapshot are opaque JSON in SQLite — passwords, tokens
and keys included — while every other secret in the application lives in the OS keychain. That is
`DIVERGENCE-STORE-a`, reproduced rather than corrected: closing it means a schema change and a
migration for data every existing 1.7.2 install already holds. Worth knowing before pointing the
tester at anything production.

**Three of a response's timings are always `-1`.** DNS, connect and TLS are the contract's
"unavailable": neither transport hands back a connection trace on a response, and splitting the
time-to-first-byte into three invented numbers would be worse than admitting there are none.

**MQTT has never spoken to a broker.** There is none on this machine and none in CI, so what is
tested is the endpoint parsing, the client id and the QoS clamp — the parts with extracted vectors —
and not one byte of the protocol. WebSocket and Socket.IO are exercised against a real loopback
server.

**Nothing reconnects, deliberately.** Not the WebSocket, not Socket.IO, not MQTT
(`DIVERGENCE-API-a`). An API testing tool that silently re-establishes a connection is falsifying
the thing the user is measuring, so a dropped connection reports itself and stops.

**gRPC and the debugger are deferred entirely**, so the gRPC
panel and the debug commands still answer `unknown command`.

**One preserved defect is worth naming, because it is the easiest to notice.** `BUG-FILE-a`: the
guard for paths that already exist decides containment by canonicalising, canonicalising a path that
is not on disk fails, and the fallback compares an unnormalised join — so a write to a file that
does not exist yet can land outside the repository. `FileOpsTests` pins both halves of it. The guard
is defensive rather than load-bearing, since the app only ever writes files the user picked out of
its own tree; correcting it would be a silent behaviour change.

**Opening a file with the OS does not try every Linux desktop.** `open_in_default_app` and
`reveal_in_file_manager` go through `UseShellExecute` — `ShellExecuteEx` on Windows, `/usr/bin/open`
on macOS, `xdg-open` on Linux. What is missing is the Linux fallback chain (`gio open`,
`gnome-open`, `kde-open`), so on a desktop without `xdg-open` the two implementations differ.

**Two commands deliberately do not exist.** `open_external_url` is absent and `open_repo_in_browser` is
`repo_web_url`, which returns the URL for the shell to open. Opening a browser belongs to the process
that owns the window, and the shell already gates the scheme — the same call slice 3 made for `quit_app`,
`reset_app_data` and `pick_folder`. Both renderer wrappers keep their exported signatures, so no
component knows the difference.

### Running the tests

```sh
dotnet test CodeFlow.slnx        # 1 218 on macOS, 1 167 on Windows; the difference is platform skips
pnpm -C shell test               # the login shell's PATH parsing, on `node --test`
pnpm -C renderer test            # Vitest over the pure logic
pnpm -C renderer typecheck
```

**These four are the whole gate, and they run here — no workflow runs them.**
`.github/workflows/ci.yml` builds the two installers and publishes them; it does not run a test, a
lint or an audit, and a pull request carries no checks at all. That is a decision, not a gap: CI
exists for the one thing a laptop cannot do, which is produce a Windows `.exe`. A red pipeline means
the installer did not build. `scripts/release.sh` is what enforces the four before a manual release,
and nothing else will catch a regression for you.

The suite opens real sockets — that is the transport under test — and `dotnet test` itself needs a
loopback listener to reach its test host. Under an agent sandbox that blocks local `bind()`, set
`sandbox.network.allowLocalBinding`; excluding `dotnet` from the sandbox entirely also works but
gives up filesystem and egress isolation for no extra benefit.

**Two tests need a credential store and skip without one**, and a handful more skip per platform —
the Windows-only shell resolution on macOS, the POSIX-shell streaming tests on a Windows machine with
no Git. A skip is printed, never silent.

`.gitattributes` pins the working tree to LF. That is not tidiness: this application compares
literals for a living — `docs/business-rules/13-cross-language-contracts.md` lists fourteen that are
byte-level contracts — and Git for Windows checks out CRLF by default, which made a prompt file's
`\r` fail an exact-match assertion the first time CI ran there.
