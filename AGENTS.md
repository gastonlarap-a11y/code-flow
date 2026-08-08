# CodeFlow

Desktop code-review and API workbench: Electron shell + C#/.NET 10 sidecar + React 19 renderer.
No root `package.json` — a .NET solution with two independent pnpm packages inside it.

## Layout
- `src/CodeFlow.App/` — the sidecar, one executable, **one folder per feature** (`Git/`, `Ai/`,
  `Providers/`, `Review/`, `ApiClient/`, `Files/`, `Storage/`, `Security/`, `Ipc/`, …)
- `tests/CodeFlow.Tests/` — xUnit v3; a feature's tests live under the same folder name as the
  feature (`Git/` → `Git/`). `Platform/` has no test folder today.
- `shell/` — Electron main + preload (TypeScript, CommonJS, `tsc` only, no bundler)
- `renderer/` — React 19 + Vite + Tailwind 4 + zustand
- `docs/business-rules/` — the specification the code must satisfy; `docs/BUSINESS_RULES.md` indexes
  it. `docs/UX-REDESIGN.md` is the same for the interface
- `scripts/` — `release.sh`, `build-app.sh` and `publish-release.sh` are the real runner; there is
  no Makefile and no root `package.json` to hang npm scripts off.
  `build-icons.sh` regenerates the committed app icons from the SVG masters in `shell/assets/`

## Commands
- Build: `dotnet build CodeFlow.slnx --configuration Release` (warnings are errors)
- Test: `dotnet test CodeFlow.slnx --configuration Release --no-build` · single: `--filter "FullyQualifiedName~Name"`
- Shell: `pnpm -C shell test` · Renderer: `pnpm -C renderer typecheck` · `pnpm -C renderer test`
- Lint: `pnpm -C renderer lint` · `pnpm -C shell lint` (ESLint flat config; warnings do not fail)
- Smoke: `dotnet run --project src/CodeFlow.App -- --smoke-test`
- Supply chain (`release.sh` runs these; no CI does any more): `dotnet list package --vulnerable
  --include-transitive` · `pnpm -C shell|renderer audit --audit-level moderate`
- Package: `scripts/build-app.sh mac|win [--dir]` · `.dmg` alone: `scripts/build-dmg.sh` (builds and
  hashes it, touches no git state)
- Release: `scripts/release.sh [major|minor|patch|X.Y.Z]` — the whole thing: gates, bump, build, tag,
  upload, wait for Windows, verify the artefacts. `scripts/publish-release.sh vX.Y.Z` is the layer
  under it and still works on its own.
- Icons: `scripts/build-icons.sh` (macOS only — `qlmanage`/`sips`/`iconutil`; commit what it writes)
- Building `CodeFlow.Tests` needs a local `bind()`, not only `dotnet test`: under a sandbox that
  blocks it the build hangs exactly 5 min and reports `Build FAILED` with **zero errors**. Allow
  local binding.

## Rules
- **No CI verifies a pull request.** `ci-web.yml` and `ci-sidecar.yml` are parked, commented out, in
  `.github/workflows-disabled/` — outside that directory GitHub never reads them (their headers say
  why, and how to restore them). `release.yml` is the only workflow left and it builds the Windows
  installer without running a single test. The suites above are the only gate, and `release.sh` is
  what enforces them. Nothing is "waiting for CI to catch it". The four Windows-exclusive tests
  (`CredentialStoreTests` and company) now run nowhere at all.
- **Never leave a disabled workflow inside `.github/workflows/`.** A file there with no `on:` and no
  `jobs:` is an *invalid* workflow, not an absent one: GitHub fails it on every push and marks the
  commit red through a failed check suite. Disabling means moving the file out of that directory.
- Behaviours listed in `docs/business-rules/91-known-bugs.md` are **preserved on purpose**. Never
  "fix" one silently — existing 1.7.2 installs and the renderer depend on them.
- Literals in `docs/business-rules/13-cross-language-contracts.md` are byte-level contracts between
  C# and TypeScript. Paraphrasing one compiles and breaks a feature silently.
- **No credential ever reaches an AI agent process.** Structural, not advisory.
- The credential store fails loudly: never a plaintext fallback, never a silent empty store.
- Package versions are pinned exactly in `Directory.Packages.props` and verified against the
  registry — never recalled from memory. A NuGet change must regenerate both `packages.lock.json`
  files (`dotnet restore CodeFlow.slnx`): CI restores with `--locked-mode` and fails on drift.
- English everywhere (code, comments, commits, docs) — prompt instructions included. The exceptions
  are user-facing es/en strings and the Spanish literals a prompt asks the model to **emit**
  (`📍 Ubicación`, `🎯 Confianza`, `## NIVEL DE REVISIÓN ACTIVO:`, the severity words, the order to
  answer in Spanish): two parsers match on those, so they are byte-level contracts. `XLANG-001`.
- Changed behaviour → update the owning document under `docs/business-rules/` in the same change.

## Architecture
- `Program.cs` holds composition and transport only; every command handler body lives in its
  feature folder. A feature must be findable in one place.
- Dependency direction is features → `Storage`/`Ipc`/external IO. `Ipc/` knows no feature: each one
  attaches itself with an `Add…Commands(…)` extension method on `CommandRegistry`.
- Errors are translated at the edge: domain-typed exceptions plus sentinel prefixes
  (`STALE_REVIEW:`, `CREDENTIAL_REFUSED:`, `SELF_APPROVAL:`, `QUOTA_EXCEEDED::`) the renderer parses.
- The renderer never reaches the sidecar directly: all remote IO goes through
  `renderer/src/lib/bridge/host.ts` → `renderer/src/lib/ipc/`. No component touches `window.codeflow`.
- Tests live with the unit they cover: a sidecar test goes in the folder named after its feature; a
  renderer test sits beside its module (`anchors.ts` / `anchors.test.ts`).
- An interface needs two real implementations or a genuine test seam (`IPullRequestHost`, the six
  engine adapters). No `IFooService` extracted for its own sake, no mapper between identical records.

## Engineering standards
- Every feature ships with its tests. Run build + `dotnet test` + `pnpm -C renderer typecheck` before
  declaring work done; report real results.
