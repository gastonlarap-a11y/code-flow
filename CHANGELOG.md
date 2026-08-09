# Changelog

The in-repo record of user-visible changes, newest first. Published release notes are generated
from merged pull requests at release time (`scripts/publish-release.sh` → `gh release create
--generate-notes`); this file is where a change's release note is written the moment the change
lands, so the story survives squashes and rewrites.

## Unreleased

### Fixed

- **A pull that fully succeeded could still show an error.** `repoStore.ts`'s post-pull refreshes
  (branches, commits, stashes, remotes, merge state) shared one generic error message with the pull
  itself, so a transient failure in one of those read-only follow-ups looked identical to the pull
  having failed — even though the changes had already landed. Each refresh now retries once and, if
  it still fails, names itself in the toast instead of a bare exception.
- **A pull that needed a merge commit could open an editor with nothing to type into.** `git pull`
  ran with no flags, so a divergent (non-fast-forward) pull tried to open an interactive editor for
  the merge message — with no terminal for it to attach to. `pull` now passes `--no-edit`, accepting
  git's own generated message; a fast-forward pull is unaffected.

## v1.9.1 — 2026-08-02

### Fixed

- **"Analyse changes" on a clean working tree no longer reports an error.** Opening the tab with
  nothing uncommitted showed a failure — with an internal `codeflow:invoke` message as the
  explanation — and filed it in Activity as a run that failed. It now says there is nothing to
  analyse yet, starts nothing, and records nothing. A run you open from Activity yourself still
  shows what it said.
- **The tab analyses again after one of those.** A stored "nothing to analyse" row counted as the
  project's most recent analysis, so the tab kept showing it instead of running — including rows
  saved by earlier versions.
- **A critical finding opens with its detail**, so "Fix with AI" is visible rather than one expand
  away, both before committing and when reviewing a pull request.

### Internal

- Building the macOS installer no longer rewrites `packages.lock.json`. The two runtimes the build
  publishes are declared, so a release leaves the working tree clean — and CI's locked-mode restore
  now verifies those runtime graphs too.

## v1.9.0 — 2026-08-02

The interface redesign, finished (PRs #49–#55). Every screen was migrated area by area onto one
type scale, one set of controls and one modal shell — and the check that holds the line now runs
with no exceptions left in it.

### Changed

- **One type scale across the app.** Sizes and line heights travel together instead of being
  chosen per component; the 380-odd hand-rolled buttons became two primitives with a floor of
  24px for a hit target and 14px for an icon. Nothing in the app is smaller than that any more.
- **Dialogs behave like dialogs.** One shell for titled dialogs and one for the search-first
  ones (the command palette, "go to file", the branch switcher) — a heading that a screen reader
  actually announces, a focus trap, Escape, and the scrim in one place rather than twenty-two.
- **Settings reads as a place where you decide something**: larger body text, buttons with
  visible words, and every input tied to its label. Twenty-three fields there had no accessible
  name at all.

### Fixed

- **Focus comes back when you close a dialog.** It never did in any dialog with an autofocused
  field — most of them — so keyboard users were returned to the top of the document each time.
- **The primary button is legible on every accent.** White on the accent colour failed the
  4.5:1 contrast minimum on six of the eight accent options in the light theme and on all eight
  in the dark one (1.67:1 at worst). The accent's two jobs — ink and fill — are separate colours
  now, and a test holds every option to the minimum.
- **Status colours read on every theme.** Success, warning and danger were chosen against a white
  background and used against 21 themes; on the tinted light ones they measured 3.73:1. Their
  light shades are darker, checked against each theme's real surface.
- **Error notifications are announced.** Toasts carried no role, so a screen reader said nothing
  when one appeared.
- **A collection's run order can be changed without a mouse.** The runner's drag handle answers
  the arrow keys and says which position it is on.
- **Icon-only controls have names.** Nineteen of them were "button" to a screen reader,
  including fourteen close buttons; an unnamed one no longer compiles.

### Known

Two contrast items are open on purpose, both recorded with their measurements in
`docs/UX-REDESIGN.md` §II.7: accent-*coloured text* in the light theme (raising it would change
the colour you picked), and the danger colour on four dark themes whose background is unusually
light.

## v1.8.0 — 2026-08-01

Everything merged since v1.7.7 (42 commits): a faster renderer, six inherited defects closed,
and a modernised toolchain underneath.

### Performance

- **The app starts leaner.** Monaco, the DBML parser and the terminal emulator load on demand —
  the entry bundle no longer carries what the first screen does not use.
- **Big trees stay fluid.** The file explorer and the API collection tree are virtualized: only
  the visible rows exist in the DOM, so thousand-entry trees scroll and drag without jank.

### Fixed

Six defects inherited from CodeFlow 1.7.2 — preserved until now under the known-bugs rule — are
closed, each as its own decision with its own test (`docs/business-rules/91-known-bugs.md` has
the full record):

- **Renames show as renames.** A renamed file now appears as a single `renamed` entry carrying
  both paths — in the Changes panel and in every diff — instead of an unrelated delete plus add
  (`BUG-GIT-a`).
- **A moved project keeps its review history.** Moving a project to another workspace takes its
  saved review runs along; histories stranded by older moves are repaired automatically on the
  next launch (`BUG-STORE-b`).
- **Removing a skill can no longer orphan its folder.** If the skill's folder cannot be deleted
  (locked, open elsewhere), the removal now says so and changes nothing — retry after closing
  whatever holds it. A re-install over an existing skill name is refused up front instead of
  silently duplicating the entry (`BUG-WS-a`, `BUG-WS-b`).
- **AI temp files are cleaned up.** The scratch files some AI engines hand to their CLIs are
  deleted when the invocation ends, and leftovers from crashed runs are swept at startup —
  temp-directory growth is no longer unbounded (`BUG-AI-a`).
- **Path traversal into not-yet-existing files is refused.** Writing through `../` to a file
  that does not exist yet is rejected before anything touches disk, closing the one gap in the
  repository-containment guard (`BUG-FILE-a`).

---

## Before 2.0.0

CodeFlow was developed in a private repository and released thirteen times between 2026-07-30 and
2026-08-06. Opening the project restarted its history from a single commit and removed those
releases, so what follows is the record those release notes held. The `#N` references still resolve
— the pull requests survive — but the commits they merged do not.

**v1.12.2** · 2026-08-06 — a release with no functional change of its own.

**v1.12.1** · 2026-08-05 — pull errors stopped being misattributed, and a divergent merge no longer
hangs waiting on an editor that has no terminal (#68).

**v1.12.0** · 2026-08-05 — per-workspace git identities (#66); prompt instructions translated to
English while the Spanish payload literals the parsers match on stayed byte-for-byte (#67).

**v1.11.0** · 2026-08-04 — faster diffs in the commit graph and better stash handling (#65).

**v1.10.0** · 2026-08-03 — the analyse run made readable, bounded and isolated (#59); the diff shaped
for the model instead of flattened (#60); the pipe that swallowed a review and the copy button that
lied (#61); `release.sh`, one command that cuts a release and verifies it landed (#62, #63); and the
CI gate rewritten to ask the commit what ran rather than each workflow for its newest run (#64).

**v1.9.1** · 2026-08-02 — the analyze tab on a clean tree, and a lock file every macOS build kept
rewriting (#57).

**v1.9.0** · 2026-08-02 — the UX redesign, phases A through D: design tokens and primitives (#49),
tree row density as a preference (#50), navigation and modal shells (#51), layout and git surfaces
(#52), the API shell and typography (#53), API panels, Settings, the AI panel, editor and git (#54),
and a final pass that removed the guard's exemption list (#55).

**v1.8.0** · 2026-08-01 — ESLint with `react-hooks/rules-of-hooks` as an error (#38); the bundle
code-split (#39); tests for the sentinel prefixes, the API importers and the script runtime (#40–#42);
store and component splits with tree virtualization (#43); TypeScript 6, Vite 8, a hardened tsconfig
and NuGet lock files (#44); and six 1.7.2 legacy bugs closed (#46).

**v1.7.7** · 2026-07-31 — `useDialog` called before the early return rather than after it (#37).

**v1.7.6** · 2026-07-31 — the Windows artefacts renamed so the digest check can find them (#36).
GitHub rewrites spaces in asset names, which had made every Windows update unverifiable.

**v1.7.5** · 2026-07-31 — the audit's security chain, its confirmed bugs and the CI gaps closed (#25);
the Rust reference dropped and the repo given an agent configuration (#24).

**v1.7.4** · 2026-07-31 — a release with no notes of its own.

**v1.7.3** · 2026-07-30 — the first release: the whole backend ported from the 1.7.2 reference to
.NET across thirteen slices (#1–#15) — transport and storage, workspaces and agent adapters, the
GitHub and Azure DevOps providers, the review engine and its memory, the terminal, the skills
subsystem, file operations and repo-wide search, the API tester, and WebSocket, Socket.IO and MQTT —
plus the installers for macOS and Windows.
