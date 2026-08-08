# 02 — Bootstrap and platform

## Scope

- `src/CodeFlow.App/Program.cs` — composition and the startup sequence
- `src/CodeFlow.App/Platform/AppPaths.cs`
- `src/CodeFlow.App/Platform/AppCommands.cs`
- `shell/src/main.ts` — window, tray and native menu

Storage and its migrations are owned by `03-storage.md`; this document only fixes where the
database is opened in the startup sequence.

## Paths

All paths are relative to `AppPaths`()` (`src/CodeFlow.App/Platform/AppPaths.cs`):

| Platform | `base_dir()` |
|---|---|
| Windows | `C:\CodeFlow` — literal, hardcoded string, **not** `%LOCALAPPDATA%` |
| macOS / Linux | the user profile directory/CodeFlow`, i.e. `~/CodeFlow`; falls back to `./CodeFlow` (relative to the process's current directory) if the user profile directory returns `None` |

Derived paths, all functions in `src/CodeFlow.App/Platform/AppPaths.cs`:

| Function | Path | Purpose |
|---|---|---|
| `db_path()` | `{base_dir}/codeflow.db` | SQLite database file |
| `logs_dir()` | `{base_dir}/logs` | application logs |
| `clone_root()` | `{base_dir}/repos` | default destination for repos cloned from within CodeFlow |
| `workspace_skills_dir(workspace_id)` | `{base_dir}/workspaces/{workspace_id}/skills` | canonical, workspace-scoped copy of installed skills |
| `reset_marker_path()` | `{base_dir}/.reset-pending` | the "wipe on next launch" marker |

**`logs_dir()` now has something in it.** It was created empty on every launch and never written to:
`LogsDirectory` had no reference outside its own definition, and the sidecar's only output was a
console the packaged app discards. So a command that failed left its message in the renderer's
memory and nowhere else — reporting one meant retyping it out of a banner that could not even be
selected, and recovering one afterwards meant reading SQLite's write-ahead log. Both happened.

`Diagnostics/ErrorLog` appends one line per failed command — timestamp, method, exception type and
the same message the renderer was told — from `IpcServer.DispatchAsync`'s catch-all, which is the
one place every command failure already passes through. It rolls over at 2 MB to a single `.1`
sibling: a user asks for a recent error, not an archive. It is not a logging framework and takes no
dependency, and it **never throws** — a logger that could fail the command it was only recording
would be worse than the silence it replaces. Path: `{base_dir}/logs/errors.log`.

**Both ends of it are injected**, and that is not decoration. `IpcServer` takes the recorder as a
constructor parameter (`Program.cs` passes `ErrorLog.Record`; a test passes nothing) and `ErrorLog`
takes its directory as one. Without either, the test suite filed its own fixtures in the user's real
error log — forty-five lines of `contoso`, `boom` and `ghe.example.invalid` in the file that exists
to tell a user what went wrong.

**Nothing that looks like a credential reaches the file.** `ErrorLog.Redact` blanks three shapes
before anything is written: `user:password` inside a URL, an `Authorization` / `X-API-Key` /
`Private-Token` value, and a token recognisable by its own published prefix (`ghp_`, `github_pat_`,
`gho_`, `ghs_`, `xox…`, `sk-`). The messages are not ours to trust — `GitNetwork` turns git's own
stderr into an exception message, and a failed `fetch` prints the remote URL it tried, which in a
great many repositories carries an embedded token. That did not matter while the text lived and died
in the renderer's memory; it matters now that it is a file whose whole purpose is to be sent to
somebody else. Found by this application reviewing the change that introduced the log, and
`.claude/rules/dotnet.md` already said never to log token values.

Deliberately blunt: it replaces rather than detects. A false positive costs a line of diagnostics, a
false negative costs a credential. The header *name* is kept — knowing a request carried an
`Authorization` at all is part of what makes the line useful — and the value stops at the first
quote, comma or brace rather than the first space, so a token embedded in a JSON error body does not
take the syntax around it with it.

**`errors.log` was not enough, and the first real Windows install proved it.** Two failures it
cannot see by construction, both of which leave a running window whose every button does nothing:

- `{base_dir}/logs/startup.log` — `Diagnostics/StartupLog`, written by `Program.RunAsync`'s `Stage`
  wrapper around steps 1–3 (the reset sweep, `EnsureDirectories`, `Database.Open` and its
  migrations). Those run *before* an `IpcServer` exists, so `ErrorLog`'s only call site cannot see
  them; until this existed, a sidecar that could not create its data directory or migrate its
  database exited leaving nothing at all behind. The whole `ToString()` is recorded, not just the
  message: a failed migration's message names neither the step nor the table. It writes to stderr
  as well, and falls back to `Path.GetTempPath()` when the log directory is itself what failed.
- `{base_dir}/logs/shell.log` — `shell/src/shell-log.ts`, the Electron main process's own log. Every
  `[core] …` and `[shell] …` line went to `console.log`/`console.error`, and a packaged NSIS build
  has no console attached: the shell's record of a sidecar that never spawned was discarded at the
  moment it was written. Same shape as `ErrorLog` — one line appended and flushed, one `.1` rollover
  at 2 MB, never throws — including the redaction, which is not decoration here either: the
  `[core] …` lines carry the sidecar's stderr verbatim.

`shell-log.ts` resolves `base_dir()` itself rather than asking the sidecar, because the process that
needs to write the line is usually the one that could not reach the sidecar at all. That makes it the
**third** independent hardcoding of `C:\CodeFlow`, after `AppPaths` and `installer/hooks.nsh`; all
three must be kept in step by hand.

`ensure_dirs()` (`src/CodeFlow.App/Platform/AppPaths.cs`) eagerly `create_dir_all`s exactly three of these:
`base_dir()`, `logs_dir()`, `clone_root()`. It does **not** create
`workspaces/{id}/skills` — that directory is the concern of whatever writes into it
(`src/CodeFlow.App/Workspaces/SkillCommands.cs`, out of this document's scope), not of startup.

`reset_marker_path()`'s doc comment (`src/CodeFlow.App/Platform/AppPaths.cs`) states the reasoning behind the whole
reset design: a "wipe everything" request can't delete `codeflow.db` live, because on
Windows a file still locked open by this process's own SQLite connection can't be removed.
So requesting a reset only ever *writes the marker and quits* (see `reset_app_data` below);
the actual deletion is deferred to the next launch's step 1, "when nothing has touched the
directory yet."

`DIVERGENCE-BOOT-a`: `base_dir()`'s Windows branch is a literal `C:\CodeFlow`, not the
conventional `%LOCALAPPDATA%`. The doc comment calls this an explicit product requirement:
a fixed, predictable location that (a) the installer's keep/wipe uninstall prompt can target
by a literal path with no runtime resolution, and (b) never needs elevated permissions,
unlike writing under `/Applications` or `/Library` on macOS (the macOS/Linux branch,
`~/CodeFlow`, is chosen for the same two reasons). Every path in the table above is derived
from this constant, so all of them — the database, logs, cloned repos, workspace skill
folders, and the reset marker — live under `C:\CodeFlow` on Windows. This must be preserved
byte-for-byte in the port: changing it strands an existing Windows user's database and
stored credentials at the old location, where the new build will never look.

The Windows uninstaller (`installer/hooks.nsh:11-20`) independently hardcodes the same
literal string a second time — `${If} ${FileExists} "C:\CodeFlow\*.*"` /
`RMDir /r "C:\CodeFlow"` — rather than calling back into the app or reading `src/CodeFlow.App/Platform/AppPaths.cs`. The
two hardcodings must be kept in sync by hand; the uninstaller comment (`hooks.nsh:1-9`) notes
that install-side needs no hook at all because `AppPaths`()` (`create_dir_all`, a
no-op if the tree already exists) recreates `C:\CodeFlow` on first launch, so a previous
install's config/credentials/skills are picked up automatically.

## Window lifecycle

The app has a single webview window, id `"main"` (referenced throughout as
`app.get_webview_window("main")`).

**Close is not quit.** `.on_window_event` (`src/CodeFlow.App/Program.cs`) intercepts
the window `close` event, which fires identically for the custom title bar's
close button, Alt+F4, the (Windows/Linux) taskbar's "Close window", and the macOS red
traffic light. On every `CloseRequested`:
- if the tray's quitting flag is `false`: `api.prevent_close()` is
  called (suppressing the close) and `hide_to_background(window)` runs instead — the window
  is hidden, not destroyed, so background jobs (AI runs, open terminals) keep running,
  "Docker Desktop-style."
- if the flag is `true`: neither call happens, so the close proceeds normally.

**`QuittingFlag`** (`shell/src/main.ts`) is an `AtomicBool` (`Ordering.SeqCst` for both
load and store), default `false`, with `is_quitting()`/`mark_quitting()`. It is set to `true`
by exactly four call sites, all of which pair `mark_quitting()` with an immediate
`app.exit(0)`:
1. the tray menu's "Quit CodeFlow" item (`shell/src/main.ts`);
2. the macOS native app menu's "Quit {name}" item, bound to ⌘Q (`shell/src/main.ts`);
3. the `quit_app` command (`src/CodeFlow.App/Platform/AppCommands.cs`);
4. the `reset_app_data` command (`src/CodeFlow.App/Platform/AppCommands.cs`), which additionally writes
   the reset marker first.

There is no fifth path that sets it — every other way of dismissing the window (title bar
close button, Alt+F4, red traffic light) goes through `CloseRequested` and hits the
`hide_to_background` branch instead.

**`hide_to_background`** (`src/CodeFlow.App/Program.cs`) is platform-specialized:
- **Non-macOS** (`src/CodeFlow.App/Program.cs`): unconditionally `window.hide()`, ignoring the result.
- **macOS** (`src/CodeFlow.App/Program.cs`): if the window is not fullscreen (`is_fullscreen().unwrap_or(false)`
  is `false`), hides immediately, same as above. If it *is* fullscreen: calls
  `window.set_fullscreen(false)` to begin exiting fullscreen, then spawns
  a task that polls `window.is_fullscreen()` every 50ms for up to 40 iterations (~2s ceiling),
  breaking out of the loop the first time it reports `false`, and only then calls
  `window.hide()`.
  - **Why polling, not a fixed sleep**: the module doc comment (`src/CodeFlow.App/Program.cs`) explains that
    macOS gives a fullscreened window its own Space; hiding it there leaves that Space
    standing but empty, landing the user on a black screen with nothing to click — the window
    has to leave fullscreen *first*, and only hide once AppKit's exit-fullscreen transition
    (animated, roughly half a second) actually finishes. `tao` (the windowing layer under
    the framework) clears its internal fullscreen flag inside `windowDidExitFullScreen`, which macOS
    calls exactly when that transition completes — so polling `is_fullscreen()` is an *exact*
    "transition finished" signal, not a guess at the animation's duration. A fixed sleep would
    either cut the animation short (the empty-Space glitch this code exists to avoid) or hide
    later than necessary.
  - **Why the 40×50ms bound**: so a transition that, for whatever reason, never reports
    completion still ends with the window hidden rather than stuck on screen forever.
  - This code path is only reachable at all since the app switched to native window
    decorations on macOS (see Plugins and window configuration): a borderless window has no
    working green button, so there was previously no way to be in fullscreen when closing.

**Dock reopen** (`src/CodeFlow.App/Program.cs`, macOS only): `RunEvent.Reopen` — raised when the user
clicks the Dock icon while the window is hidden but the process is still running — calls
the tray(_app_handle)`.

**Tray** (`shell/src/main.ts`): a single tray icon, id `"main-tray"`, using the app's bundled
default window icon (`.expect("app icon must be bundled")` — startup panics if none is
bundled). Menu: "Show CodeFlow" (id `"show"`) and "Quit CodeFlow" (id `"quit"`).
`show_menu_on_left_click(false)`: a left click does not open this menu. Left-click on the
tray icon (`TrayIconEvent.Click` with `MouseButton.Left`) calls `show_main_window`. The
"show" menu item also calls `show_main_window`; "quit" marks `QuittingFlag` and exits.

`show_main_window(app)` (`shell/src/main.ts`): looks up the `"main"` webview window and, if
found, calls `show()`, `unminimize()`, `set_focus()` in that order — all with `let _ =`, so
if the window handle is missing or any individual call fails, it fails silently with no
retry and no user-facing error.

## Native menu

`shell/src/main.ts` is macOS-only end to end: the real implementation is
`; on every other platform `setup()` is a no-op that returns
`success` (`shell/src/main.ts`). The module doc comment (`shell/src/main.ts`) states why it
exists on macOS and nowhere else:

> Without an Edit menu, ⌘X/⌘C/⌘V/⌘A never reach the webview. On macOS those chords are menu
> *key equivalents* — AppKit resolves them against the menu bar before any view sees the key
> event, so an app with no Edit menu has no clipboard at all in its plain `<input>`s and
> `<textarea>`s.

**The failure mode is silent**: there is no error, no console log, no exception — a field
simply appears to ignore paste, because the keystroke was never delivered to the webview at
all. This makes `shell/src/main.ts` a hard functional dependency for every text field in the app on
macOS, not decoration, even though a user is not expected to click most of its items
directly. Windows and Linux don't need it: any menu there would render *inside* the window,
sitting on top of the custom title bar, and both platforms deliver clipboard chords straight
to the webview without a menu bar involved at all.

`app.set_menu(...)` installs one menu bar for the whole application (not per-window — macOS
has exactly one menu bar). Three submenus, in this order:

**App menu** (titled after `app.package_info().name`, e.g. "CodeFlow"):
1. About (a predefined menu item)
2. separator
3. Services (a predefined menu item)
4. separator
5. Hide (a predefined menu item)
6. Hide Others (a predefined menu item)
7. Show All (a predefined menu item)
8. separator
9. **"Quit {name}"** — a *custom* `MenuItem` (id `"quit"`, key equivalent `Cmd+Q`), not
   the predefined Quit item. The code comment (`shell/src/main.ts`) is explicit about why:
   this item must go through the same path as the tray's Quit (mark `QuittingFlag`, then
   `app.exit(0)`) — if it were the predefined quit item instead, ⌘Q would trigger the shell's own
   built-in quit behavior, which, given that `CloseRequested` is globally intercepted to hide
   the window, would make ⌘Q *hide* the app instead of exiting it. This wiring detail is a
   hazard worth carrying into the port: any Electron/other-shell equivalent of ⌘Q must be
   routed through the same "are we actually quitting" flag rather than through a
   platform-default quit action.

**Edit menu**: Undo, Redo, separator, Cut, Copy, Paste, Select All — all
the predefined menu items, i.e. native OS-provided behavior, not custom handlers.

**Window menu**: Minimize, Maximize, separator, Close Window — all predefined.

`app.on_menu_event(...)` (`shell/src/main.ts`) handles only the `"quit"` id; every other item
in this menu is a `PredefinedMenuItem` whose behavior AppKit supplies directly, with no
sidecar-side handler at all.

## Plugins and window configuration

Five shell capabilities back the frontend's non-command surface:

| Plugin | Registered at | Used by (frontend) |
|---|---|---|
| Opening a URL | `src/CodeFlow.App/Program.cs` | `UpdateNotesModal.tsx` (`openUrl`) |
| Native file dialogs | `src/CodeFlow.App/Program.cs` | `ImportModal.tsx`, `ProvidersSection.tsx`, `ReviewMemoriesSettings.tsx`, `SkillsSettings.tsx`, `CodeSnapModal.tsx` |
| Platform detection | `src/CodeFlow.App/Program.cs` | `platform.ts` |
| Relaunch | `src/CodeFlow.App/Program.cs` | `updateStore.ts` (`relaunch`) |
| Update check and download | `src/CodeFlow.App/Program.cs` | `updateStore.ts` (`check`, `downloadAndInstall`) |

The tray icon is created by the shell (`shell/src/main.ts`), required for
`shell/src/main.ts`'s `TrayIconBuilder` to exist at all.

Window geometry/decoration is configured per platform, base config plus a macOS override:

| | `shell/src/main.ts` (Windows/Linux) | its macOS branch (override) |
|---|---|---|
| size | 1440×900, min 1024×640 | same |
| `decorations` | `false` — fully custom title bar, drawn by `TitleBar.tsx` | `true` — native decorations |
| `titleBarStyle` | — | `"Overlay"` |
| `hiddenTitle` | — | `true` |
| `trafficLightPosition` | — | `{x: 20, y: 22}` |

On macOS the traffic lights are real AppKit buttons drawn *over* the webview at that
position (running roughly x=20 to x=74); `TitleBar.tsx`'s `MacControlsSpacer` reserves a
62px gap so the custom title bar's own controls don't collide with them. Keeping native
decorations on macOS (rather than the fully custom bar used on Windows/Linux) is also what
gives the window a working green button/native fullscreen — the precondition for the
`hide_to_background` fullscreen-exit dance documented above ever being reachable.

Other configuration of note: `security.csp` is `null` (no Content-Security-Policy
configured); `bundle.createUpdaterArtifacts` is `true` (required for the updater plugin to
have signed release artifacts to compare against); the Windows NSIS bundle wires
`installer/hooks.nsh` as its `installerHooks`; app identifier is `com.codeflow.app`; the
updater reads this repository's GitHub Releases
endpoint (`.../releases/latest/download/latest.json`) and a minisign public key for update
signature verification.

## Non-command shell surface

These eleven frontend files call the bridge modules directly instead of going through an
`invoke`d command, which means their replacement in an Electron shell lives outside the C#
core entirely — in the Electron main/preload layer, not in any ported command handler.

1. **`components/layout/TitleBar.tsx`** — `getCurrentWindow()` from `renderer/src/lib/bridge/shell.ts`,
   held as a module-level singleton. Calls `win.minimize()`, `win.toggleMaximize()`,
   `win.close()` from the Windows/Linux custom title bar's control buttons (`WindowsControls`,
   rendered only when `!isMac`; macOS instead reserves space for the native traffic lights and
   renders no custom buttons at all). **Requirement**: the Electron shell must expose
   minimize / toggle-maximize / close for the current window to the renderer, used only on
   Windows/Linux, and must leave macOS's native window controls alone (no custom buttons on
   that platform).

2. **`state/updateStore.ts`** — three surfaces: `getVersion()` (`renderer/src/lib/bridge/updater.ts`, read
   once, cached in store state); `check()` and the returned `Update.downloadAndInstall(...)`
   with `Started`/`Progress`/`Finished` events (`renderer/src/lib/bridge/updater.ts`); `relaunch()`
   (`renderer/src/lib/bridge/updater.ts`). Drives an hourly automatic check (`CHECK_INTERVAL_MS` = 1h,
   the interval owner is outside this file), a manual check, download-with-progress, install,
   and restart-to-apply — one shared store so the corner notice, the what's-new window and
   Settings never disagree about update state. `check()` throwing in a plain dev server (no installed
   binary to replace) is deliberately swallowed as a silent no-op rather than surfaced.
   **Requirement**: an update mechanism exposing current version, a feed check, download with
   progress callbacks, and post-install relaunch, that likewise degrades silently when there
   is no installed binary to update.

3. **`components/api/ImportModal.tsx`** — `getCurrentWebview().onDragDropEvent(...)`
   (`renderer/src/lib/bridge/webview.ts`). the native webview drag handler consumes OS file drops before any
   DOM `drop` event fires, so this is the only way to receive a dropped file's absolute path;
   the listener is window-wide (not hit-tested to a drop zone) and the code accepts a drop
   anywhere while the modal is open, since the modal is exclusive and nothing else could be
   the intended target. Payload variants handled: `enter`/`over` (highlight), `leave`
   (un-highlight), `drop` (load `payload.paths[0]` via the `apiReadTextFile` command).
   **Requirement**: deliver a dropped file's filesystem path to the same load-by-path flow;
   Electron's renderer already receives native drops as ordinary DOM drag/drop events (with
   `webUtils.getPathForFile` or equivalent for the path), so this bypass is likely simpler to
   replace than to preserve as-is, but the "accept anywhere while the modal is open" behavior
   must still be reproduced since there is no independent hit-testing to fall back on.

4. **`components/settings/ProvidersSection.tsx`** — `open()` (aliased `openDialog`, from
   `renderer/src/lib/bridge/dialog.ts`) in `browseBinary()`: a native single-file picker for locating
   a provider's CLI binary on disk. **Requirement**: a single, non-directory native file-open
   dialog reachable from the renderer.

5. **`components/settings/ReviewMemoriesSettings.tsx`** — `open()` with `directory: true`
   (`renderer/src/lib/bridge/dialog.ts`) in `exportRuns()`: picks a destination folder, then calls the
   `export_review_runs` command with that path. **Requirement**: a native directory-picker
   dialog.

6. **`components/settings/SkillsSettings.tsx`** — `open()` with `directory: true`
   (`renderer/src/lib/bridge/dialog.ts`) in `importFolder()`: picks a local folder to import as a
   workspace skill via `importSkillFromFolder`. **Requirement**: same native directory-picker
   dialog as above.

7. **`components/editor/CodeSnapModal.tsx`** — `save()` (`renderer/src/lib/bridge/dialog.ts`) in
   `download()`: a native save-file dialog, pre-filled with a suggested filename
   (`suggestedSnapName`) and a PNG file-type filter; the resulting path is written to via the
   `write_file_bytes` command. **Requirement**: a native save-file dialog accepting a default
   filename and an extension filter.

8. **`components/layout/UpdateNotesModal.tsx`** — `openUrl()` (`renderer/src/lib/bridge/shell.ts`) in
   `openLinkExternally`, which intercepts clicks on links inside the rendered release-notes
   markdown and sends only `http(s)://` hrefs to the system's default browser (anything else
   is silently swallowed); without this, a click would navigate the app's own webview away
   from CodeFlow to GitHub with no way back. **Requirement**: open http(s) URLs in the
   system's default browser, gated to http/https only, from a renderer-triggered call.

9. **`lib/platform.ts`** — `platform()` (`renderer/src/lib/bridge/shell.ts`), called once and memoized
   in a module-level `cached` variable; on failure (the OS plugin is unavailable outside the
   Electron shell, e.g. plain `vite dev` in a browser) it falls back to a `navigator.platform`
   regex check for macOS specifically, defaulting to `"unknown"` otherwise. Every other
   modifier-key label, title-bar layout branch (`isMac` in `TitleBar.tsx`), and
   platform-specific shortcut in the app reads this resolved value. **Requirement**: a
   synchronous, memoized platform read (`"macos" | "windows" | "linux" | "unknown"`) available
   to the renderer from process start.

10. **`state/sidecarStore.ts`** — `host.sidecarStatus()` plus the `codeflow:sidecar-status` event
   (`renderer/src/lib/ipc/events.ts`), feeding `components/layout/SidecarBanner.tsx`. The only entry
   added after the port, and the only one that exists *because* of it: whether the .NET core is
   answering is a fact that has no meaning while the backend and the UI share a process, so it has no
   `invoke`d command behind it and could not have one — a core that is down is exactly the core that
   cannot answer being asked. **Requirement**: a shell-side, renderer-readable availability state
   with a human-readable reason, available both as a subscription and as a value that can be read at
   mount. See BOOT-032.

11. **`lib/ui/useCopy.ts`** — `host.clipboardWrite()`, with `navigator.clipboard` behind it, and
   **`components/layout/SidecarBanner.tsx`** — `host.openLogs()`. **Requirement**: a write-only
   clipboard path that does not depend on a web permission, and a way to reveal the app's own log
   directory in the OS file manager. Neither can be a sidecar command: one has to run where the
   window is, and the other is needed precisely when the sidecar is what failed. See BOOT-033.

## Rules

### BOOT-001 The reset marker is checked and consumed before any other startup work
**Implementation**: `src/CodeFlow.App/Program.cs`, `src/CodeFlow.App/Platform/AppPaths.cs`
**Behaviour**: `run()`'s first act, before the window exists, is checking
whether `{base_dir}/.reset-pending` exists. If it does, `remove_dir_all(base_dir())` deletes
the entire base directory tree — database, logs, cloned repos, workspace skill folders, and
the marker itself, all at once.
**Inputs / outputs**: no inputs; side effect only. The `Result` of `remove_dir_all` is
discarded (`let _ =`).
**Edge cases**: a partial or failed delete (e.g. a file locked by another process) is never
reported to the user or logged; the app simply continues starting up against whatever is
left of `base_dir()`.
**Frontend dependency**: none directly; every command that reads or writes under `base_dir()`
implicitly depends on this step having either fully completed or never run.
**Markers**: none

### BOOT-002 `Database.Open`()` runs after the reset check and before window/menu setup
**Implementation**: `src/CodeFlow.App/Program.cs`
**Behaviour**: `Database.Open`()` is the argument to `.manage(`Database.Open`().expect(...))`
(`src/CodeFlow.App/Program.cs`), evaluated synchronously at that point in the builder chain — after the reset
delete (BOOT-001), and necessarily before the `.setup()` closure (tray + native menu) runs,
since setup runs during window creation (`src/CodeFlow.App/Program.cs`), which comes after every
`.manage()`/`.plugin()` call has already executed. The doc comment at `src/CodeFlow.App/Program.cs` states
this ordering is required, referring to `AppPaths`()`'s own doc comment for
why. Directory creation and migrations inside `Database.Open`()` itself belong to `src/CodeFlow.App/Storage/Database.cs`'s own
document; this rule only fixes its position relative to the reset check and the window/menu
setup.
**Inputs / outputs**: returns managed state; `.expect("failed to initialize CodeFlow
database")` panics the whole process on any failure, before any window has been created.
**Edge cases**: if this ran before BOOT-001 instead of after, a same-session reset request
from the *previous* run would destroy a database this run has already opened.
**Frontend dependency**: none directly — every database-backed command depends on this step
having completed.
**Markers**: none

### BOOT-003 `base_dir()` hardcodes `C:\CodeFlow` on Windows
**Implementation**: `src/CodeFlow.App/Platform/AppPaths.cs`
**Behaviour**: on Windows, returns the literal `PathBuf.from(r"C:\CodeFlow")`; on
macOS/Linux, returns the user profile directory/CodeFlow`, falling back to `./CodeFlow` (relative to
the process's current directory) if the user profile directory is `None`.
**Inputs / outputs**: no inputs; pure function of the compile-target OS.
**Edge cases**: the macOS/Linux fallback when `home_dir()` fails is deterministic but
platform-launch-context-dependent (a relative path resolves against whatever the process's
CWD happens to be when launched); in practice the user profile directory failing on a normal desktop
session is rare.
**Frontend dependency**: none directly.
**Markers**: `DIVERGENCE-BOOT-a` — deliberate: a fixed, predictable, elevation-free location
the installer's keep/wipe prompt (`installer/hooks.nsh`) can target with a literal string.
Every other path in this document is derived from this one, so changing it strands an
existing Windows user's database, logs, cloned repos and workspace skill files at the old
`C:\CodeFlow`, invisible to a build that looks under `%LOCALAPPDATA%` instead. The Windows
uninstaller hardcodes the identical literal a second, independent time
(`installer/hooks.nsh:12,15`) and must be kept in sync by hand with any change here.

### BOOT-004 Derived filesystem paths, all rooted at `base_dir()`
**Implementation**: `src/CodeFlow.App/Platform/AppPaths.cs`
**Behaviour**: `db_path()` → `{base_dir}/codeflow.db`; `logs_dir()` → `{base_dir}/logs`;
`clone_root()` → `{base_dir}/repos`; `workspace_skills_dir(workspace_id)` →
`{base_dir}/workspaces/{workspace_id}/skills`.
**Inputs / outputs**: `workspace_skills_dir` takes a `string` workspace id and joins it
unescaped as a path segment; no validation of the id happens in this file.
**Edge cases**: none specific to this file — path-traversal or invalid-character handling
for `workspace_id`, if any, lives in whichever command constructs the id, out of this
document's scope.
**Frontend dependency**: none directly.
**Markers**: none

### BOOT-005 `ensure_dirs()` pre-creates three of the five base-relative directories
**Implementation**: `src/CodeFlow.App/Platform/AppPaths.cs`
**Behaviour**: `create_dir_all`s `base_dir()`, `logs_dir()`, `clone_root()`, in that order,
returning the first error encountered (via `?`) and short-circuiting the rest.
**Inputs / outputs**: `void`.
**Edge cases**: does **not** create `workspaces/{id}/skills` — that directory's creation is
the responsibility of whatever writes into it (`src/CodeFlow.App/Workspaces/SkillCommands.cs`, out of scope), not of
startup.
**Frontend dependency**: none directly; `installer/hooks.nsh`'s comment confirms this
function is also what recreates `C:\CodeFlow` on first launch after a fresh install, since
`create_dir_all` is a no-op if the tree already exists.
**Markers**: none

### BOOT-006 The reset marker file records a pending wipe for the *next* launch
**Implementation**: `src/CodeFlow.App/Platform/AppPaths.cs`, `src/CodeFlow.App/Platform/AppCommands.cs`
**Behaviour**: `reset_marker_path()` is `{base_dir}/.reset-pending`. Nothing ever reads it
except BOOT-001's startup check; nothing ever writes it except `reset_app_data` (BOOT-017).
**Inputs / outputs**: n/a — existence-only marker, empty file contents (`System.IO`(path,
"")`).
**Edge cases**: none beyond BOOT-001's.
**Frontend dependency**: `resetAppData` (the `reset_app_data` TS wrapper, see `01-ipc-surface.md`).
**Markers**: none

### BOOT-007 `CloseRequested` hides the window to the tray unless `QuittingFlag` is set
**Implementation**: `src/CodeFlow.App/Program.cs`
**Behaviour**: fires identically for the custom title bar's close button, Alt+F4, the
taskbar's "Close window", and the macOS red traffic light. If
the tray's quitting flag is `false`: `api.prevent_close()` +
`hide_to_background(window)`. If `true`: neither call runs, so the close proceeds normally.
**Inputs / outputs**: none (event handler, no return value consumed).
**Edge cases**: none — this is a total interception; there is no window-close path that
bypasses it except by the flag already being `true` when the event fires.
**Frontend dependency**: the custom title bar's close button (`WindowsControls` in
`TitleBar.tsx`, via `win.close()`) relies on this to hide rather than terminate.
**Markers**: none

### BOOT-008 `QuittingFlag` has exactly four setters, all pairing `mark_quitting()` with `app.exit(0)`
**Implementation**: `shell/src/main.ts`; `shell/src/main.ts`; `src/CodeFlow.App/Platform/AppCommands.cs`
**Behaviour**: an `AtomicBool` (`Ordering.SeqCst`), default `false`. Set to `true` only by:
the tray's "Quit CodeFlow" menu item; the macOS app menu's "Quit {name}" item (⌘Q); the
`quit_app` command; the `reset_app_data` command. Every one of the four immediately follows
`mark_quitting()` with `app.exit(0)` — the flag is never set without an accompanying exit
call in the same code path.
**Inputs / outputs**: n/a.
**Edge cases**: there is no fifth path — every other dismissal (title bar close, Alt+F4, red
traffic light) goes through `CloseRequested` (BOOT-007) and never touches this flag.
**Frontend dependency**: `quitApp`, `resetAppData` (see `01-ipc-surface.md`).
**Markers**: none

### BOOT-009 macOS `hide_to_background` waits out the fullscreen-exit animation by polling
**Implementation**: `src/CodeFlow.App/Program.cs`
**Behaviour**: if not fullscreen, hides immediately. If fullscreen: calls
`set_fullscreen(false)`, then polls `is_fullscreen()` every 50ms (up to 40 times, ~2s cap) in
a spawned async task, hiding as soon as it first reports `false`.
**Inputs / outputs**: none (side effect on the window).
**Edge cases**: if the transition never reports completion within the ~2s cap, the window is
hidden anyway on the 40th iteration rather than left stuck on screen.
**Frontend dependency**: none directly — this is purely native-window behavior triggered by
BOOT-007.
**Markers**: none — the polling-over-fixed-sleep choice is deliberate and explained in the
source (`tao` clears its fullscreen flag inside `windowDidExitFullScreen`, exactly when
AppKit's exit-fullscreen transition completes, so polling the flag is an exact
transition-finished signal rather than a guess at the animation's ~500ms duration); this is
documented in full in the Window lifecycle section above rather than repeated as a marker.

### BOOT-010 Non-macOS `hide_to_background` hides immediately, unconditionally
**Implementation**: `src/CodeFlow.App/Program.cs`
**Behaviour**: `window.hide()`, no fullscreen check.
**Inputs / outputs**: none.
**Edge cases**: none — Windows/Linux have no equivalent of macOS's per-fullscreen-window
Space, so there is nothing to wait for.
**Frontend dependency**: none directly.
**Markers**: none

### BOOT-011 Dock icon reopen shows the hidden window (macOS only)
**Implementation**: `src/CodeFlow.App/Program.cs`
**Behaviour**: the Dock reopen event (raised when the Dock icon is clicked while the
window is hidden but the process is running) calls the tray.
**Inputs / outputs**: none.
**Edge cases**: this `RunEvent` variant only exists in the macOS build of the enum at all
(` gates the whole `if let`), so on Windows/Linux the closure body
is empty.
**Frontend dependency**: none directly.
**Markers**: none

### BOOT-012 Tray icon: menu, click behavior, iconography
**Implementation**: `shell/src/main.ts`
**Behaviour**: tray id `"main-tray"`, icon = the app's bundled default window icon
(`.expect("app icon must be bundled")`), tooltip "CodeFlow", `show_menu_on_left_click(false)`.
Menu: "Show CodeFlow" (id `"show"`, → `show_main_window`), "Quit CodeFlow" (id `"quit"`, →
mark `QuittingFlag` + `app.exit(0)`). Left tray-icon click (not opening the menu) also calls
`show_main_window`.
**Inputs / outputs**: `show_main_window` calls `show()`, `unminimize()`, `set_focus()` on the
`"main"` window, each with its `Result` discarded.
**Edge cases**: if no icon is bundled, the `.expect` panics the tray, which propagates
out of `.setup()` and fails app startup entirely. If the `"main"` window handle can't be
found, `show_main_window` silently no-ops.
**Frontend dependency**: none directly.
**Markers**: none

### BOOT-013 The native macOS Edit menu is a hard clipboard-functionality dependency
**Implementation**: `shell/src/main.ts`
**Behaviour**: without this Submenu (Undo, Redo, Cut, Copy, Paste, Select All — all
the predefined menu items), ⌘X/⌘C/⌘V/⌘A never reach the webview on macOS: those chords are menu
key equivalents that AppKit resolves against the menu bar before any view sees the key event.
**Inputs / outputs**: none — the items are entirely AppKit-native, with no sidecar-side handler.
**Edge cases**: the failure mode is completely silent — no error, no console log, no
exception; a text field simply appears to ignore paste. This makes the menu load-bearing for
every `<input>`/`<textarea>` in the app on macOS, despite having no click-driven purpose most
users will exercise directly.
**Frontend dependency**: every text-editing surface in the app, implicitly, on macOS only.
**Markers**: none — this is a documented, deliberate dependency (see the Native menu section
above), not a source ambiguity, defect, or divergence from expected behaviour in the marker
table's sense.

### BOOT-014 The macOS Quit menu item is custom, not the predefined Quit item
**Implementation**: `shell/src/main.ts`
**Behaviour**: "Quit {name}" (⌘Q) is built as `MenuItem.with_id(app, "quit", ..., Some("Cmd+Q"))`
and routed through the same `on_menu_event` handler that marks `QuittingFlag` and calls
`app.exit(0)` — deliberately not the predefined Quit item.
**Inputs / outputs**: none.
**Edge cases**: none in the code itself; the *reason* is the notable part — using the
predefined quit item would let the framework's own quit behavior interact with the global
`CloseRequested` interceptor (BOOT-007) in a way that would make ⌘Q merely hide the app
instead of exiting it.
**Frontend dependency**: none directly.
**Markers**: none — this is a wiring hazard worth preserving conceptually in the port (route
any "real quit" affordance through the same flag/exit pair rather than a platform-default
quit action), documented in prose above rather than as a formal marker since the sidecar
behavior itself is neither buggy nor ambiguous.

### BOOT-015 `setup` is a no-op on Windows/Linux
**Implementation**: `shell/src/main.ts`
**Behaviour**: the entire menu-construction function is `; the
non-macOS variant is a no-op.
**Inputs / outputs**: always `success` on non-macOS.
**Edge cases**: none.
**Frontend dependency**: none.
**Markers**: none

### BOOT-016 `quit_app` is the only path that unconditionally terminates the process on command
**Implementation**: `src/CodeFlow.App/Platform/AppCommands.cs`
**Behaviour**: marks `QuittingFlag`, then `app.exit(0)`. See `01-ipc-surface.md` for the
command's parameter/return signature (none / `()`).
**Inputs / outputs**: see `01-ipc-surface.md`.
**Edge cases**: none beyond BOOT-007/BOOT-008.
**Frontend dependency**: `quitApp` TS wrapper (see `01-ipc-surface.md`).
**Markers**: none

### BOOT-017 `reset_app_data` marks a pending wipe and quits; it does not touch the OS keychain
**Implementation**: `src/CodeFlow.App/Platform/AppCommands.cs`, `src/CodeFlow.App/Platform/AppPaths.cs`, `installer/hooks.nsh:8-9`
**Behaviour**: writes an empty file at `AppPaths`()`, marks `QuittingFlag`,
calls `app.exit(0)`. The doc comment (`src/CodeFlow.App/Platform/AppCommands.cs`) frames this as "the in-app
equivalent of the Windows installer's 'delete my data' uninstall prompt — the only way to get
that same choice on macOS, since a DMG install has no uninstaller/hook mechanism to intercept
at all." The actual deletion happens on the *next* launch (BOOT-001), not here.
**Inputs / outputs**: `void` — the only failure mode is the marker write itself
failing (mapped to a string error); once the marker write succeeds, the function always exits
the process, so a caller never observes an "it ran but the reset didn't happen" state from
this call alone.
**Edge cases**: neither this command nor BOOT-001's deletion touches secrets stored in the OS
keychain (ADO PAT, GitHub token, per-provider AI API keys — `src/CodeFlow.App/Security/CredentialStore.cs`, out of scope). This
mirrors the Windows uninstaller, whose own hook comment (`installer/hooks.nsh:8-9`) notes
explicitly that keychain-referenced credentials are "untouched" by its wipe prompt too — so
the scopes agree, but a "reset app data" affordance leaving credentials behind is easy to
assume is a bug rather than the deliberately matched behavior it is.
**Frontend dependency**: `resetAppData` TS wrapper (see `01-ipc-surface.md`).
**Markers**: `DIVERGENCE-BOOT-b` — deliberate: "reset app data" wipes `base_dir()` only
(database, logs, cloned repos, workspace skill files) and never the OS keychain, matching the
Windows uninstaller's identical scope. Must be preserved: a port that also purges keychain
entries on this path silently changes what "reset" means for a returning user, and a port
that assumes it already does so would ship with the same illusion the current shape avoids
only because both wipe paths independently document the same restriction.

### BOOT-018 Window geometry and decoration configuration diverges by platform
**Implementation**: `shell/electron-builder.yml` · `shell/src/main.ts`
**Behaviour**: base config (Windows/Linux): 1440×900, min 1024×640, `decorations: false`.
macOS override: same size/min, `decorations: true`, `titleBarStyle: "Overlay"`,
`hiddenTitle: true`, `trafficLightPosition: {x: 20, y: 22}`.
**Inputs / outputs**: n/a — static config, merged by the shell's platform-override
mechanism.
**Edge cases**: none in the config itself; `TitleBar.tsx`'s `MacControlsSpacer` (62px) exists
specifically to avoid the custom title bar's own controls colliding with the native traffic
lights drawn at that position.
**Frontend dependency**: `TitleBar.tsx` (both the Windows/Linux custom buttons and the macOS
spacer) branches on `usePlatform()` to decide which of these two layouts to render.
**Markers**: none

### BOOT-018b The sidecar reads its IPC token from stdin, not from its command line
**Implementation**: `src/CodeFlow.App/Program.cs` (`ReadIpcTokenAsync`) · `shell/src/main.ts`
(`startSidecar`)
**Behaviour**: the shell spawns `codeflow-core` with stdin piped, writes the handshake token as
one line, and closes the pipe. The core reads that line before anything else and exits `2` if it
is missing or blank. The only remaining argument is `--app-version`.
**Inputs / outputs**: one line of UTF-8 on stdin, trimmed. Not logged on either side.
**Edge cases**: a core started by hand gets no line, prints the "spawned by the shell" message and
exits `2` — the same refusal as before, with a different reason text. `--smoke-test` short-circuits
above this and needs no token.
**Frontend dependency**: none.
**Markers**: `DIVERGENCE-BOOT-e` — the token used to arrive as `--ipc-token <uuid>`, which is how
1.7.2 did it and what the port reproduced. A command line is world-readable on POSIX: any process
the same user runs can recover the token from `ps` or `/proc/<pid>/cmdline`, which made it a secret
kept in the one place designed to be public. An environment variable is narrower but not enough —
this process spawns AI CLIs, `git` and `npx`, all of which inherit its environment. A closed pipe
leaves no artefact at all. The socket permissions in `IpcListener` are still the first line of
defence; this stops the second one being given away.

### BOOT-021 An update is verified against a published digest before it is handed over
**Implementation**: `src/CodeFlow.App/Update/UpdateService.cs` (`ExpectedDigestAsync`,
`DigestFor`, `VerifyDigestAsync`) · `scripts/publish-release.sh` · `.github/workflows/release.yml`
**Behaviour**: every release carries a `<asset>.sha256` beside each installer, written by
`shasum -a 256` on macOS and `sha256sum` on Windows. `update_download` fetches that file, hashes
what it downloaded, and refuses anything that does not match — deleting the file rather than
leaving a rejected installer one double-click away in Downloads.
**Inputs / outputs**: unchanged on the wire. The digest is fetched sidecar-side, deliberately: the
renderer passes the asset url and name back into `update_download`, so a digest that made the same
round trip would be one an attacker who reached the renderer could choose.
**Edge cases**: a release with no digest file for the asset is **refused**, not installed
unverified. A file with a **single entry yields its digest without checking the name**: the caller
fetched it as `<asset>.sha256`, so the binding is already the asset's own name. Where there are
several entries the last path segment is compared, and GNU's `*` binary marker is stripped.

The single-entry rule is not a convenience. v1.7.5 shipped without it and **refused every Windows
update**: GitHub rewrites spaces to dots when it stores a release asset, so the API answered
`CodeFlow.1.7.5.exe` while `sha256sum` had recorded `CodeFlow 1.7.5.exe`, and the two never
matched. `win.artifactName` in `shell/electron-builder.yml` now names the artefacts without spaces,
which fixes it at the source — but a verifier whose failure mode is "silently refuse every update"
must not depend on that holding.
**Also**: `UpdateAssets.For` picks the Windows **installer**, not merely the first `.exe`. A
Windows release carries a portable build too, and with `InstallKind()` at `auto` the chosen
artefact is executed — handing over the portable one launches a loose copy of the new version and
leaves the installed build untouched, an update that appears to work and updates nothing. The
installer is identified by the `-Setup-` in `win.artifactName`; a release older than v1.7.6 has no
marker, so an unmarked lone `.exe` is still offered rather than refused.
**Frontend dependency**: none; the refusal surfaces as the command's error message.
**Markers**: `DIVERGENCE-BOOT-f` — nothing was checked before. On Windows `InstallKind()` is
`auto`, so the NSIS installer was launched straight after the download, unsigned: anything that
could put bytes on that response had code execution with no second opinion. TLS proves who served
it, not what they served. A code signature would be better and needs a certificate the project does
not have (see `UpdateAssets`); a digest published as its own asset is what is available, and it
moves the trust from "whatever this response contained" to "the bytes the release recorded".
One file per artefact rather than a shared `SHA256SUMS`, because the two installers are built on
different machines at different times and a shared file would be two uploads racing.

### BOOT-019 Five shell capabilities back exactly one frontend bypass concern each
**Implementation**: `src/CodeFlow.App/Program.cs`, `Directory.Packages.props`
**Behaviour**: the shell, the shell, the shell,
the shell, the shell, registered in that order; all pinned to major
version `2`.
**Inputs / outputs**: n/a.
**Edge cases**: none.
**Frontend dependency**: see the Non-command shell surface section and BOOT-020–028 below —
every plugin here maps to at least one of the nine bypass files.
**Markers**: none

### BOOT-020 `TitleBar.tsx` drives the current window directly for Windows/Linux chrome
**Implementation**: `renderer/src/components/layout/TitleBar.tsx`
**Behaviour**: `getCurrentWindow()` (`renderer/src/lib/bridge/shell.ts`), held module-level; `minimize()`,
`toggleMaximize()`, `close()` wired to the Windows/Linux-only `WindowsControls` buttons.
**Inputs / outputs**: none beyond the window-manager calls themselves.
**Edge cases**: macOS renders no custom buttons at all (native traffic lights instead, per
BOOT-018).
**Frontend dependency**: this *is* the frontend dependency — no backend command involved.
**Markers**: none

### BOOT-021 `updateStore.ts` owns the entire update lifecycle via three bridge surfaces
**Implementation**: `renderer/src/state/updateStore.ts`
**Behaviour**: `getVersion()`, `check()`/`Update.downloadAndInstall()`, `relaunch()`. See the
Non-command shell surface section, item 2, for the full behavior (status machine, guard
against concurrent checks, silent-vs-visible error handling).
**Inputs / outputs**: n/a — store-internal.
**Edge cases**: `check()` throws in a plain dev server (no installed binary to replace);
deliberately swallowed rather than surfaced.
**Frontend dependency**: `UpdateNotesModal.tsx` and the title-bar update badge (not in this
file set) both read this store.
**Markers**: none

### BOOT-022 `ImportModal.tsx` receives dropped files via the native webview drag channel
**Implementation**: `renderer/src/components/api/ImportModal.tsx`
**Behaviour**: `getCurrentWebview().onDragDropEvent(...)`; see Non-command the shell surface
item 3 for the full payload/behavior.
**Inputs / outputs**: n/a.
**Edge cases**: accepts a drop anywhere in the window while the modal is mounted (no
hit-testing to a specific drop zone).
**Frontend dependency**: `apiReadTextFile` command consumes the resolved path (see
`01-ipc-surface.md`).
**Markers**: none

### BOOT-023 `ProvidersSection.tsx` uses a native file picker for a provider's binary path
**Implementation**: `renderer/src/components/settings/ProvidersSection.tsx`
**Behaviour**: `open()` (`renderer/src/lib/bridge/dialog.ts`), single file, non-directory.
**Inputs / outputs**: n/a.
**Edge cases**: none.
**Frontend dependency**: feeds `saveBinary`, which persists via the `set_setting` command.
**Markers**: none

### BOOT-024 `ReviewMemoriesSettings.tsx` uses a native directory picker for export destination
**Implementation**: `renderer/src/components/settings/ReviewMemoriesSettings.tsx`
**Behaviour**: `open({ directory: true, multiple: false })`.
**Inputs / outputs**: n/a.
**Edge cases**: none.
**Frontend dependency**: feeds the `export_review_runs` command.
**Markers**: none

### BOOT-025 `SkillsSettings.tsx` uses a native directory picker to import a skill folder
**Implementation**: `renderer/src/components/settings/SkillsSettings.tsx`
**Behaviour**: `open({ directory: true, multiple: false })`.
**Inputs / outputs**: n/a.
**Edge cases**: none.
**Frontend dependency**: feeds the `import_skill_from_folder` command.
**Markers**: none

### BOOT-026 `CodeSnapModal.tsx` uses a native save-file dialog for PNG export
**Implementation**: `renderer/src/components/editor/CodeSnapModal.tsx`
**Behaviour**: `save({ defaultPath, filters: [{ name: "PNG", extensions: ["png"] }] })`
(`renderer/src/lib/bridge/dialog.ts`).
**Inputs / outputs**: n/a.
**Edge cases**: user cancel (`null` path) is caught and treated as a silent no-op.
**Frontend dependency**: feeds the `write_file_bytes` command.
**Markers**: none

### BOOT-027 `UpdateNotesModal.tsx` opens release-note links in the system browser
**Implementation**: `renderer/src/components/layout/UpdateNotesModal.tsx`
**Behaviour**: `openUrl()` (`renderer/src/lib/bridge/shell.ts`), gated to `http(s)://` hrefs only;
non-matching hrefs are silently swallowed.
**Inputs / outputs**: n/a.
**Edge cases**: prevents the webview's own default navigation on every intercepted click
(`e.preventDefault()`), regardless of whether the href matched the http(s) gate.
**Frontend dependency**: rendered release-note markdown (`renderMarkdown`), not part of this
document's scope.
**Markers**: none

### BOOT-028 `platform.ts` resolves and memoizes the OS platform once per process
**Implementation**: `renderer/src/lib/platform.ts`
**Behaviour**: `platform()` (`renderer/src/lib/bridge/shell.ts`), called once, cached in a module-level
variable; on failure, falls back to a `navigator.platform` regex check for macOS, otherwise
`"unknown"`.
**Inputs / outputs**: `"macos" | "windows" | "linux" | "unknown"`.
**Edge cases**: the fallback path only distinguishes macOS from everything else — a
non-Electron, non-Mac browser context (plain `vite dev`) always resolves to `"unknown"`, never
`"windows"` or `"linux"`.
**Frontend dependency**: `TitleBar.tsx`'s `isMac` branch, `useShortcutHint`, and every other
platform-conditioned UI/shortcut decision in the app (outside this file set).
**Markers**: none

### BOOT-029 One web permission is granted — the clipboard write; everything else is refused
**Implementation**: `shell/src/permissions.ts`, `shell/src/main.ts` (`answerPermissions`)
**Behaviour**: Both Electron handlers — `setPermissionRequestHandler` and `setPermissionCheckHandler` — answer from the same one-entry set, `{"clipboard-sanitized-write"}`. That is the permission Chromium resolves `navigator.clipboard.writeText`/`write` against, and it asks through the **check** handler without a prior request, so a handler that only got the request path right would still leave every copy button dead. Everything else — geolocation, media, notifications, MIDI, HID, serial, USB, `clipboard-read` — is refused, and a refused *request* is logged to the shell console.
**Inputs / outputs**: `grants(permission: string): boolean`.
**Edge cases**: `clipboard-read` stays denied on purpose, which makes `OpenPrLinkModal`'s autofill from the clipboard a permanent no-op — it is written to treat a refusal that way, and ⌘V still pastes because the paste shortcut belongs to the OS (BOOT-013), not to the page. Both handlers used to return `false` unconditionally on the premise that the app asks for no permission at all; that premise was wrong in exactly this one place, and the renderer reported success regardless, so every copy button in the app was dead without a symptom.
**Frontend dependency**: `lib/ui/useCopy.ts`, the `common/CopyAnswer.tsx` button every AI answer ends with, and the seven other `navigator.clipboard` call sites; `CodeSnapModal`'s image copy.
**Also**: this permission is **no longer the only route to the clipboard** — see BOOT-033. `useCopy`
now prefers the shell's own `clipboard.writeText`, and the web API is what a browser-only run falls
back to. The grant stays exactly as it is: `CodeSnapModal`'s image copy and any future
`navigator.clipboard` caller still resolve against it, and removing it would break them silently in
the same way it did before.
**Markers**: none

### BOOT-033 Copying goes through the shell, and an error can be selected
**Implementation**: `shell/src/main.ts` (`codeflow:clipboardWrite`, `codeflow:openLogs`) ·
`shell/src/preload.ts` · `renderer/src/lib/ui/useCopy.ts` · `renderer/src/lib/ui/copyHint.ts` ·
`renderer/src/components/common/Toast.tsx` · `renderer/src/components/layout/SidecarBanner.tsx`
**Behaviour**: three things that together made an error unreportable:

1. `index.css`'s `body { user-select: none }` was never reverted on the toast, which is where
   ~51 `pushErrorToast` call sites and the global `unhandledrejection` net land. With no selection
   possible, `params.editFlags.canCopy` is `false`, so `installContextMenu` offered no **Copy** on
   right-click either — a user could not get the text out by any means. `select-text` on the message
   plus a copy `IconButton` (errors only) fixes both. This is the same bug `index.css` records fixing
   for markdown and `AiErrorBanner` for AI failures; the toast was simply missed.
2. `useCopy` writes through `host.clipboardWrite` when a bridge is present. `navigator.clipboard`
   resolves against a permission this app already got wrong once (BOOT-029) and additionally rejects
   on `Document is not focused` or while another process holds the Windows clipboard. The shell path
   is a direct OS call with none of those modes. **Write only** — `clipboard-read` stays denied and no
   read is exposed here either.
3. `common.copyFailed` took a hardcoded `⌘C` **in both locales**. `manualCopyChord` resolves it from
   the platform, so the recovery instruction names a key the user's keyboard has.
**Inputs / outputs**: `clipboardWrite(text: string)`; rejects a non-string rather than coercing.
`openLogs()` takes no path — the main process picks its own log directory, because "reveal whatever
the page names" is a wider capability than the one feature needing it.
**Edge cases**: the toast now pauses its 5s timer on `focus`/`blur` as well as pointer enter/leave;
without that, reaching the new button by keyboard took longer than the toast lived. `openLogs`
creates the directory first, so the button works on a launch that has not written a log yet.
**Frontend dependency**: every `useCopy` call site, and `SidecarBanner`'s two buttons.
**Markers**: none

### BOOT-034 The IPC endpoint is a path, and the pipe name is not the same string
**Implementation**: `src/CodeFlow.App/Platform/AppPaths.cs` (`IpcEndpoint`) ·
`src/CodeFlow.App/Ipc/IpcListener.cs` (`NamedPipeIpcListener.PipeNameFrom`) ·
`shell/src/ipc-client.ts` (`open`)
**Behaviour**: `IpcEndpoint` publishes a **full address** on both platforms — `\\.\pipe\codeflow-{pid}`
on Windows, `{base_dir}/.ipc-{pid}.sock` elsewhere. That exact string is what crosses stdout in the
`codeflow-core ready …` line and what the shell hands to `net.connect` unchanged, which is why the
shell needs no platform branch at all. On Windows the listener derives the **bare name** from it with
`PipeNameFrom`, because `NamedPipeServerStream` prepends the `\\.\pipe\` namespace itself.
**Inputs / outputs**: `PipeNameFrom` strips `\\.\pipe\` or `\\?\pipe\` case-insensitively and returns
anything else untouched — `--ipc-endpoint` may supply a bare name.
**Edge cases**: the two forms are asymmetric by platform convention, not by choice: .NET hides the
namespace and Node requires it. Handing the full path to the constructor as if it were a name does
**not** throw — the documented `NotSupportedException` fires only on a colon — so the process starts,
reports ready, and listens at an address nobody can open. The failure surfaces only as
`connect ENOENT \\.\pipe\codeflow-<pid>` in the shell after a 15s retry loop.
**Frontend dependency**: none directly; every command in the app depends on this connection existing.
**Markers**: `BUG-BOOT-a` — **fixed**. Shipped in v1.12.2 and made the application inert on Windows:
the sidecar ran, opened its database (leaving the `-wal`/`-shm` files behind) and answered nothing.
Reported as three unrelated broken buttons, because no path in the renderer said what had failed —
see BOOT-032, which is what finally surfaced it. The suite could not have caught it: all four IPC
test classes skipped themselves on Windows, stating this listener was "covered by running the app
there". That skip is now gone, `IpcTestClient` opens the platform's real transport, and on Windows it
opens the published endpoint **by its literal path** — a `NamedPipeClientStream` derives the path from
a bare name exactly as the server does, so it would have agreed with the broken server and passed.

### BOOT-030 A startup failure is recorded before it ends the process
**Implementation**: `src/CodeFlow.App/Program.cs` (`Stage`) · `src/CodeFlow.App/Diagnostics/StartupLog.cs`
**Behaviour**: steps 1–3 of `RunAsync` each run through `Stage(name, work)`, which catches, calls
`StartupLog.Record(stage, failure)` and **rethrows unchanged**. Nothing about the failure handling
changes — these steps have no fallback and BOOT-001/BOOT-002's ordering means continuing past a
failed one would corrupt what the next assumes — only that it is now written down first. The stage
name (`reset-marker`, `directories`, `scratch-sweep`, `storage`) is the diagnostic: a permission on
the data directory and a migration against an older schema are not distinguishable from a bare stack.
**Inputs / outputs**: side effect only; the exception continues to the caller.
**Edge cases**: `StartupLog` never throws. Two sinks, because either can be the only one working —
stderr reaches the shell (which now keeps `shell.log`), and the file survives the process. If
`AppPaths.LogsDirectory` cannot be written — which is precisely what a failed `directories` stage
means — it retries in `Path.GetTempPath()`.
**Frontend dependency**: none directly; the renderer learns about this failure through BOOT-032.
**Markers**: none

### BOOT-031 The Electron main process keeps a log a packaged build can be asked for
**Implementation**: `shell/src/shell-log.ts` · `shell/src/main.ts`
**Behaviour**: `record(level, message)` appends to `{base_dir}/logs/shell.log` **and** mirrors to the
console, so a `pnpm -C shell dev` run reads as before. Call sites: the spawn attempt, the core's
stdout and stderr, the `error` and `exit` handlers, the SIGTERM escalation, and both outcomes of the
startup `try`/`catch`. Redaction is identical to `ErrorLog.Redact` and is load-bearing rather than
precautionary — the `[core] …` lines are the sidecar's own stderr, and a failed `fetch` prints the
remote URL it tried.
**Inputs / outputs**: `record(level, message): void`; `recordIn(directory, …)` takes the directory as
a parameter for the same reason `ErrorLog` does — without it the suite files its fixtures in the
user's real log.
**Edge cases**: never throws; a full disk or a refused path is silently no-op. Synchronous writes on
purpose: the most important line it ever records is the last thing the process does before it dies.
An exit is recorded at `info` when `quitting` is set and `error` otherwise — the app going down on
request and the app going quiet mid-session are not the same event.
**Frontend dependency**: none; the path is handed to the renderer by BOOT-032 so the banner can name
it.
**Markers**: none

### BOOT-032 The renderer is told when the core is not running
**Implementation**: `shell/src/main.ts` (`codeflow:sidecarStatus`) · `shell/src/ipc-client.ts`
(`state`) · `shell/src/preload.ts` · `renderer/src/lib/ipc/events.ts` (`onSidecarStatus`) ·
`renderer/src/state/sidecarStore.ts` · `renderer/src/components/layout/SidecarBanner.tsx`
**Behaviour**: the `codeflow:sidecar-status` event has been forwarded since BOOT-018b's transport
existed and **nothing listened to it**. Every command in the app goes through the sidecar, so a core
that is down makes the whole window inert — which is how one backend failure was reported as three
unrelated broken buttons (the folder picker, workspace creation, the git identity field). The store
now subscribes to the event *and* reads `codeflow:sidecarStatus`, and both halves are required: a
core that fails to spawn is `down` within milliseconds, long before this window has a listener, so
the event alone can only ever report a late failure. `status: "starting"` is deliberately not
surfaced — the core takes a moment to bind on every launch, and a banner there would cry wolf on
every run.
**Inputs / outputs**: `codeflow:sidecarStatus` returns `{status, detail?, logsDirectory}`.
**Edge cases**: the getter is wrapped in a `try`/`catch` that swallows — the renderer in a plain
browser (`pnpm dev` with no Electron) has no bridge, and a store that only ever adds an explanation
must not be able to fail startup. The banner is not dismissible and its detail is `select-text` with
a copy button: this is a state the app stays in until it is restarted, and the reason has to still be
on screen when the user gets round to reporting it.
**Frontend dependency**: `App.tsx` mounts `SidecarBanner` directly under `TitleBar`, above every
panel that the outage makes inert.
**Markers**: none

## Markers raised

| Marker | Where | Summary |
|---|---|---|
| `DIVERGENCE-BOOT-a` | BOOT-003 | `base_dir()` hardcodes `C:\CodeFlow` on Windows instead of `%LOCALAPPDATA%`; every derived path depends on it; the uninstaller hardcodes the same literal independently. |
| `DIVERGENCE-BOOT-b` | BOOT-017 | `reset_app_data` (and the deletion it schedules) never touches OS-keychain-stored secrets, matching the Windows uninstaller's identical scope. |
| `BUG-BOOT-a` (fixed) | BOOT-034 | The Windows listener passed the full `\\.\pipe\…` path to `NamedPipeServerStream` as if it were a pipe name, so it listened at an address the shell could not open. Every command failed; the app looked like it had dead buttons. Fixed, with the Windows skip removed from all four IPC suites. |

No `AMBIGUOUS-BOOT-*` or `BUG-BOOT-*` markers were raised: the files in this document's scope
are small, thoroughly commented "why" units — startup glue and native-shell wiring — rather than
business logic with unresolved edge cases. None of them carry extractable cases, so this document
has no `test-vectors/` fixtures: there was simply nothing to extract.
