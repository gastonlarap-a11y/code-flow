import {
  app, BrowserWindow, clipboard, dialog, ipcMain, Menu, nativeTheme, protocol, session, shell, Tray,
  type MenuItemConstructorOptions,
} from "electron";
import { spawn, type ChildProcessByStdio } from "node:child_process";
import type { Readable, Writable } from "node:stream";
import { createReadStream, existsSync } from "node:fs";
import { mkdir } from "node:fs/promises";
import { join, normalize } from "node:path";
import { CONTENT_SECURITY_POLICY, contentTypeFor, isWithinRoot } from "./app-protocol";
import { IpcClient, type SidecarStatus } from "./ipc-client";
import { applyLoginShellPath } from "./login-path";
import { grants } from "./permissions";
import { logsDirectory, record } from "./shell-log";

// Registered first, before anything can throw. Node's default for an uncaught exception in the main
// process is to print a stack and exit, and in a packaged Electron app there is no console to print
// to: the window vanishes mid-action, macOS writes no crash report because nothing died by signal,
// and `shell.log` gets no line because the process is already gone. That is not a hypothetical —
// it is how "the app closes when I press stop" reached us, with both places one would look empty.
//
// Staying alive is the point. An unhandled rejection or a stray `'error'` event from one emitter
// says nothing about whether the rest of the app still works, and the user is mid-task; the
// listeners below leave that judgement to them and make sure the reason is on disk either way.
process.on("uncaughtException", (error) => {
  record("error", `[shell] uncaught: ${error.stack ?? error.message}`);
});

process.on("unhandledRejection", (reason) => {
  const detail = reason instanceof Error ? (reason.stack ?? reason.message) : String(reason);
  record("error", `[shell] unhandled rejection: ${detail}`);
});

// A signal is the one way to end an AppKit app that leaves nothing to find: no crash report, because
// it is a graceful terminate rather than a fault, and no stack, because `before-quit` comes straight
// out of `applicationShouldTerminate:`. Recorded so the next one is a line rather than a mystery.
// The app still ends — refusing to would make it unkillable by ordinary means — but it ends saying so.
for (const signal of ["SIGTERM", "SIGINT", "SIGHUP"] as const) {
  process.on(signal, () => requestQuit(`a ${signal} from outside the app`));
}

// The shell is CommonJS on purpose. Electron's preload script has to be CJS unless the whole
// chain opts into ESM with the right extensions, and that friction buys nothing here — this is
// about a thousand lines of glue across five files, not a library.
const here = __dirname;

// Dev mode is opt-in through the environment rather than inferred from `app.isPackaged`. An
// unpackaged run is the normal way to test a production-shaped build, and inferring dev from it
// means that run silently fails against a Vite server nobody started.
const devServerUrl = process.env.CODEFLOW_DEV_SERVER;
const isDev = devServerUrl !== undefined;

// Resolved the same way as the sidecar: a packaged build carries the renderer as an unpacked
// resource, an unpackaged one reads the sibling package's build output. The `app://` handler
// streams these files off disk, so they stay outside the asar rather than inside it.
const rendererRoot = app.isPackaged
  ? join(process.resourcesPath, "renderer")
  : join(here, "..", "..", "renderer", "dist");

const ipc = new IpcClient();

let mainWindow: BrowserWindow | null = null;
let tray: Tray | null = null;
let sidecar: ChildProcessByStdio<Writable, Readable, Readable> | null = null;

/**
 * Distinguishes "the user closed the window" from "the app is really quitting".
 *
 * CodeFlow 1.7.2 keeps the process alive when the window closes so AI runs and terminals survive,
 * and uses this flag to tell a real quit apart. Everything that sets it deliberately goes through
 * {@link requestQuit}; `before-quit` sets it too, and records a stack when it finds it still down —
 * that is the case where the quit came from outside this file entirely.
 */
let quitting = false;

/**
 * Quits, and writes down who asked.
 *
 * Every deliberate exit goes through here rather than setting the flag and calling `app.quit()` on
 * its own. Without it the log records only that the core exited, which is the *consequence* of a
 * quit and says nothing about its cause — and "the app closed by itself" is a report that can only
 * be answered by naming the caller. The four reasons are few enough to enumerate and impossible to
 * tell apart after the fact.
 */
function requestQuit(reason: string): void {
  record("info", `[shell] quitting: ${reason}`);
  quitting = true;
  app.quit();
}

// ---------------------------------------------------------------------------
// Renderer delivery
// ---------------------------------------------------------------------------

/**
 * The renderer is served over a custom protocol rather than `file://`.
 *
 * `lib/monacoSetup.ts` bundles Monaco and wires five language workers through Vite `?worker`
 * imports so the editor works offline. Module and worker loading break under `file://`, so a
 * packaged build that used `loadFile` would fail in exactly the places that are hardest to
 * notice — diff views, conflict resolution, the editor.
 */
function registerAppProtocol(): void {
  protocol.handle("app", async (request) => {
    const url = new URL(request.url);
    const relative = url.pathname === "/" ? "index.html" : decodeURIComponent(url.pathname).slice(1);
    const target = normalize(join(rendererRoot, relative));

    // A path that escapes the renderer root is a bug or an attack; either way it is not served.
    if (!isWithinRoot(rendererRoot, target) || !existsSync(target)) {
      return new Response("not found", { status: 404 });
    }

    const contentType = contentTypeFor(target);

    // The policy rides on the document itself rather than a `webRequest` listener. It is attached
    // at the one point the document is produced, so it cannot be missed by the first navigation —
    // and it needs no `session`, which only exists after `whenReady`.
    const headers: Record<string, string> = { "content-type": contentType };
    if (contentType.startsWith("text/html")) {
      headers["content-security-policy"] = CONTENT_SECURITY_POLICY;
    }

    return new Response(createReadStream(target) as unknown as ReadableStream, { headers });
  });
}

// ---------------------------------------------------------------------------
// Window
// ---------------------------------------------------------------------------

function createWindow(): BrowserWindow {
  // The window shape, including its macOS override. macOS keeps its native traffic
  // lights overlaid on a custom-drawn title bar; Windows and Linux are fully frameless and the
  // React TitleBar draws everything.
  const window = new BrowserWindow({
    title: "CodeFlow",
    // macOS reads the icon from the bundle, so this is for Windows and Linux — and for every
    // unpackaged run on any platform, where there is no bundle to read.
    icon: join(here, "..", "assets", "icon.png"),
    width: 1440,
    height: 900,
    minWidth: 1024,
    minHeight: 640,
    show: false,
    ...(process.platform === "darwin"
      ? { titleBarStyle: "hidden" as const, trafficLightPosition: { x: 20, y: 22 } }
      : { frame: false }),
    webPreferences: {
      preload: join(here, "preload.js"),
      contextIsolation: true,
      nodeIntegration: false,

      // Electron's default since v20, and nothing here needs the exception: the preload uses only
      // contextBridge, ipcRenderer, webUtils and process.platform, all of which a sandboxed
      // preload still gets. What it buys is that a renderer-side V8 exploit meets the OS sandbox
      // rather than a full-privilege process.
      sandbox: true,
    },
  });

  restrictNavigation(window);
  installContextMenu(window);

  window.once("ready-to-show", () => window.show());

  window.on("close", (event) => {
    // Close hides to the tray; only an explicit quit ends the process. Cutting this would change
    // the app's process model, not just its window behaviour.
    if (!quitting) {
      // Recorded because from the outside a hidden window and a quit look identical, and telling
      // them apart is the whole difficulty in any report of the app "closing on its own".
      record("info", "[shell] the window was closed; hiding to the tray, the app keeps running");
      event.preventDefault();
      hideToBackground(window);
    }
  });

  void window.loadURL(isDev ? devServerUrl : "app://codeflow/index.html");

  return window;
}

/**
 * Hands a URL to the system browser, if it is one the system browser should get.
 *
 * Gated to http/https: `shell.openExternal` will happily launch a `file://` or custom-scheme
 * handler, which on every platform is a route to running something. Both callers — the renderer's
 * "what's new" link and the window-open handler — go through this one check.
 */
async function openExternal(url: string): Promise<void> {
  const parsed = new URL(url);
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
    throw new Error(`refusing to open a ${parsed.protocol} URL`);
  }

  await shell.openExternal(url);
}

/**
 * Reveals a directory the app itself wrote, in the OS file manager.
 *
 * The second half of "here is what went wrong": when the clipboard is unavailable — and on Windows
 * it can be, for reasons no code here controls — attaching `shell.log` beats retyping a banner.
 *
 * Gated to the app's own base directory rather than accepting any path. The renderer is not the
 * threat model, but this is the preload's side of a boundary, and "open anything the page names in
 * the file manager" is a wider capability than the one feature that needs it.
 */
async function openLogsDirectory(): Promise<void> {
  const directory = logsDirectory();
  await mkdir(directory, { recursive: true }).catch(() => undefined);

  const failure = await shell.openPath(directory);
  if (failure !== "") throw new Error(failure);
}

/**
 * Pins the window to the renderer it was built with.
 *
 * The preload rides on whatever this window loads, so a navigation away from `app://` would hand
 * `window.codeflow` — and through it the whole command surface — to a page nobody vetted. Electron
 * denies `window.open` by default and nothing in the app navigates deliberately, so neither of
 * these fires today; they exist so that stays true after a change that would otherwise make it
 * quietly untrue.
 *
 * External links are not blocked, they are routed: `codeflow:openExternal` already validates the
 * scheme and hands the URL to the system browser, which is where a link out of rendered markdown
 * belongs.
 */
function restrictNavigation(window: BrowserWindow): void {
  const allowedPrefix = isDev ? devServerUrl : "app://codeflow/";

  window.webContents.on("will-navigate", (event, url) => {
    if (!url.startsWith(allowedPrefix)) {
      event.preventDefault();
      console.warn(`[shell] refused to navigate the window to ${url}`);
    }
  });

  window.webContents.setWindowOpenHandler(({ url }) => {
    void openExternal(url).catch((error: unknown) => {
      console.warn(`[shell] refused to open ${url}: ${error instanceof Error ? error.message : String(error)}`);
    });

    return { action: "deny" };
  });
}

/**
 * Right-click → Copy, which Electron does not provide on its own.
 *
 * A Chromium page in Electron has no context menu at all unless one is built, so every view in this
 * app answered a right-click with nothing. The Edit menu's ⌘C was wired up long ago and works, but
 * nobody reaches for a menu bar to copy an error message — they right-click it, find nothing, and
 * retype it by hand. That happened.
 *
 * Deliberately minimal: the reading and writing actions a text surface needs, each shown only when
 * it applies. Roles rather than hand-written handlers, so the platform's own labels and shortcuts
 * come with them in whatever language the OS is set to.
 */
function installContextMenu(window: BrowserWindow): void {
  window.webContents.on("context-menu", (_event, params) => {
    const items: MenuItemConstructorOptions[] = [];

    if (params.editFlags.canCut && params.isEditable) items.push({ role: "cut" });
    if (params.editFlags.canCopy) items.push({ role: "copy" });
    if (params.editFlags.canPaste && params.isEditable) items.push({ role: "paste" });

    if (items.length > 0) items.push({ type: "separator" });
    items.push({ role: "selectAll" });

    Menu.buildFromTemplate(items).popup({ window });
  });
}

/**
 * Hides the window for the "keep running in the background" path.
 *
 * macOS gives a fullscreened window its own Space, and hiding it there leaves that Space standing
 * but empty — the user lands on a black screen with nothing to click. So the window has to leave
 * fullscreen first and only hide once AppKit finishes the transition.
 */
function hideToBackground(window: BrowserWindow): void {
  if (process.platform === "darwin" && window.isFullScreen()) {
    window.once("leave-full-screen", () => window.hide());
    window.setFullScreen(false);
    return;
  }

  window.hide();
}

function showWindow(): void {
  if (!mainWindow || mainWindow.isDestroyed()) {
    mainWindow = createWindow();
    return;
  }

  if (mainWindow.isMinimized()) mainWindow.restore();
  mainWindow.show();
  mainWindow.focus();
}

// ---------------------------------------------------------------------------
// Tray and menu
// ---------------------------------------------------------------------------

function createTray(): void {
  const icon = join(here, "..", "assets", "tray.png");
  if (!existsSync(icon)) return;

  tray = new Tray(icon);
  tray.setToolTip("CodeFlow");
  tray.setContextMenu(
    Menu.buildFromTemplate([
      { label: "Show CodeFlow", click: showWindow },
      { type: "separator" },
      { label: "Quit CodeFlow", click: () => requestQuit("the tray's Quit item") },
    ]),
  );
  tray.on("click", showWindow);
}

/**
 * Installs the native application menu.
 *
 * This is not decoration. Without a native Edit menu, AppKit resolves ⌘X/⌘C/⌘V/⌘A against the
 * menu bar before the webview ever sees them, and **the clipboard silently stops working** — a
 * failure no "does the window open" check would catch. Quit is a custom item rather than the
 * predefined role so it routes through the same flag as the tray, keeping quit and close
 * distinguishable.
 */
function installMenu(): void {
  if (process.platform !== "darwin") {
    Menu.setApplicationMenu(null);
    return;
  }

  Menu.setApplicationMenu(
    Menu.buildFromTemplate([
      {
        label: "CodeFlow",
        submenu: [
          { role: "about" },
          { type: "separator" },
          { role: "hide" },
          { role: "hideOthers" },
          { role: "unhide" },
          { type: "separator" },
          {
            label: "Quit CodeFlow",
            accelerator: "Command+Q",
            click: () => requestQuit("the ⌘Q menu item"),
          },
        ],
      },
      {
        label: "Edit",
        submenu: [
          { role: "undo" },
          { role: "redo" },
          { type: "separator" },
          { role: "cut" },
          { role: "copy" },
          { role: "paste" },
          { role: "selectAll" },
        ],
      },
      { label: "Window", submenu: [{ role: "minimize" }, { role: "zoom" }, { role: "close" }] },
    ]),
  );
}

// ---------------------------------------------------------------------------
// Sidecar
// ---------------------------------------------------------------------------

function sidecarPath(): string {
  const name = process.platform === "win32" ? "codeflow-core.exe" : "codeflow-core";
  return app.isPackaged
    ? join(process.resourcesPath, "core", name)
    : join(here, "..", "..", "src", "CodeFlow.App", "bin", "Debug", "net10.0", name);
}

/**
 * Spawns the .NET core and waits for it to report its endpoint.
 *
 * The endpoint is read from the child's first stdout line rather than guessed, so the two sides
 * cannot disagree about where to meet. Everything after that crosses the pipe; stdout stays free
 * for logs, which is why stdio was rejected as the transport in the first place.
 */
function startSidecar(): Promise<string> {
  return new Promise((resolve, reject) => {
    const executable = sidecarPath();
    if (!existsSync(executable)) {
      reject(new Error(`the CodeFlow core was not found at ${executable}`));
      return;
    }

    // stdin is piped for exactly one line: the IPC token. It used to be an argument, which put a
    // secret on a command line every process the user runs can read (`ps`, `/proc/<pid>/cmdline`).
    // An environment variable would be no good either — the core spawns AI CLIs, `git` and `npx`,
    // and they inherit its environment. A closed pipe leaves nothing behind.
    // The version stays an argument: it is not a secret, and it lives in `package.json`, which is
    // this process's business — `update_check` has to compare against the build actually running.
    record("info", `[shell] starting the CodeFlow core at ${executable}`);

    const child = spawn(
      executable,
      ["--app-version", app.getVersion()],
      {
        stdio: ["pipe", "pipe", "pipe"],
        // `detached` is `setsid()`: the core leads its own process group, and so does everything
        // under it. Without it the whole tree — this process, the core, the AI CLI it spawns and
        // that CLI's own MCP servers — shares **one** group, and a group-wide signal from anywhere
        // in it reaches the app. That is not hypothetical: stopping a run kills the CLI's tree, and
        // one millisecond later AppKit began a graceful terminate here, with no `app.quit()`
        // anywhere in the stack. A `SIGTERM` does not crash an AppKit app, it asks it to quit — so
        // the app closed mid-action leaving no crash report and no exception to find. `BOOT-037`.
        //
        // Nothing about lifetime changes: `stopSidecar` signals this child by pid and escalates to
        // SIGKILL, and Node never killed children on its own anyway.
        detached: true,
      },
    );
    sidecar = child;

    child.stdin.end(`${ipc.token}\n`);

    // The three pipes need the same guard the `spawn` itself got below, and for the same reason: a
    // stream whose peer went away emits `'error'`, and an `'error'` with no listener is thrown.
    // `stdin` is the likeliest of the three — it is written once and closed, so a core that dies
    // early turns that write into an EPIPE — but a killed process can break a read just as well.
    // Recorded and otherwise ignored: the exit handler below is what decides what a dead core means.
    for (const [name, stream] of [
      ["stdin", child.stdin], ["stdout", child.stdout], ["stderr", child.stderr],
    ] as const) {
      stream.on("error", (error: Error) =>
        record("warn", `[shell] the CodeFlow core's ${name} failed: ${error.message}`));
    }

    let settled = false;
    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk: string) => {
      for (const line of chunk.split("\n")) {
        const match = /^codeflow-core ready (.+)$/.exec(line.trim());
        const endpoint = match?.[1];
        if (endpoint && !settled) {
          settled = true;
          resolve(endpoint);
        } else if (line.trim()) {
          record("info", `[core] ${line.trim()}`);
        }
      }
    });

    // The core's stderr is where a startup failure says what went wrong — a permission on the data
    // directory, a migration that threw. It used to go to a console a packaged build discards, which
    // is why a Windows install that could not open its database looked like an app whose buttons do
    // nothing.
    child.stderr.setEncoding("utf8");
    child.stderr.on("data", (chunk: string) => record("error", `[core] ${chunk.trimEnd()}`));

    // Without a listener, an 'error' event throws — Node's EventEmitter contract — and takes the
    // main process with it. `existsSync` above rules out a missing binary, not one that cannot be
    // executed: a quarantined download or a lost +x bit reaches here instead.
    child.on("error", (error) => {
      sidecar = null;
      const detail = `it could not be started: ${error.message}`;
      record("error", `[shell] the CodeFlow core ${detail}`);
      if (!settled) {
        settled = true;
        reject(new Error(detail));
      } else {
        ipc.markDown(detail);
      }
    });

    child.on("exit", (code) => {
      sidecar = null;
      const detail = `it exited with code ${code ?? "unknown"}`;
      // Recorded whether or not the app is on its way out: an exit before readiness is the failure
      // being diagnosed, and one after it is the app going quiet mid-session.
      record(quitting ? "info" : "error", `[shell] the CodeFlow core ${detail}`);
      if (!settled) {
        settled = true;
        reject(new Error(detail));
      } else {
        // No silent respawn: a restart would risk losing or duplicating in-flight AI runs,
        // terminals and half-written transactions. Surface it and let the user decide.
        ipc.markDown(detail);
      }
    });
  });
}

/** How long a sidecar gets to exit on its own before it is killed outright. */
const SIGKILL_GRACE_MS = 3_000;

/**
 * Ends the sidecar, and makes sure it actually ended.
 *
 * `kill()` sends SIGTERM, which a process wedged in a native call — libgit2 mid-clone, a PTY
 * read — can ignore indefinitely. The app would then quit and leave it holding the database. The
 * escalation is unref'd so it never keeps the event loop alive by itself.
 */
function stopSidecar(): void {
  const child = sidecar;
  if (!child) return;

  sidecar = null;
  child.kill();

  if (child.exitCode !== null || child.signalCode !== null) return;

  setTimeout(() => {
    if (child.exitCode === null && child.signalCode === null) {
      record("warn", "[shell] the CodeFlow core did not exit on SIGTERM; killing it");
      child.kill("SIGKILL");
    }
  }, SIGKILL_GRACE_MS).unref();
}

// ---------------------------------------------------------------------------
// Renderer bridge
// ---------------------------------------------------------------------------

function registerBridge(): void {
  ipcMain.handle("codeflow:invoke", (_event, method: string, params: Record<string, unknown>) =>
    ipc.invoke(method, params),
  );

  // Events are broadcast to every window, exactly as 1.7.2 does: it emits with
  // `app.emit` and never `emit_to`, so filtering is the renderer's job and each payload carries
  // the id it belongs to. Reproducing the broadcast is a correctness requirement.
  const forward = (event: string, payload: unknown) => {
    for (const window of BrowserWindow.getAllWindows()) {
      window.webContents.send("codeflow:event", event, payload);
    }
  };

  for (const name of [
    "ai:output",
    "git:progress",
    "git:done",
    "terminal:output",
    "terminal:exit",
    "debug:paused",
    "debug:resumed",
    "debug:output",
    "debug:terminated",
    "repo:fs-changed",
    "skills:progress",
    "api:stream-message",
    "api:stream-status",
  ]) {
    ipc.on(name, (payload) => forward(name, payload));
  }

  ipc.onStatusChange = (status: SidecarStatus, detail?: string) =>
    forward("codeflow:sidecar-status", { status, detail });

  // Asked as well as announced. A core that fails to spawn is `down` before the renderer has loaded
  // a listener, so the event that was already being forwarded could only ever report a failure that
  // happened late — which is not the one this exists for. The logs directory rides along so the
  // banner can name a path the user can actually open.
  ipcMain.handle("codeflow:sidecarStatus", () => ({ ...ipc.state, logsDirectory: logsDirectory() }));

  // Window controls — the capabilities 1.7.2 granted in capabilities/default.json.
  ipcMain.handle("codeflow:window", (event, action: string) => {
    const window = BrowserWindow.fromWebContents(event.sender);
    if (!window) return false;

    switch (action) {
      case "minimize":
        window.minimize();
        return true;
      case "toggleMaximize":
        if (window.isMaximized()) {
          window.unmaximize();
        } else {
          window.maximize();
        }
        return true;
      case "close":
        window.close();
        return true;
      case "isMaximized":
        return window.isMaximized();
      default:
        return false;
    }
  });

  ipcMain.handle("codeflow:setTheme", (_event, theme: "light" | "dark" | null) => {
    nativeTheme.themeSource = theme ?? "system";
  });

  ipcMain.handle("codeflow:openExternal", (_event, url: string) => openExternal(url));

  ipcMain.handle("codeflow:openLogs", () => openLogsDirectory());

  /**
   * Writes text to the clipboard without going through `navigator.clipboard`.
   *
   * The web API is not a reliable floor. It resolves against a permission this app already got
   * wrong once — `BOOT-029`: every copy button in the app was dead for months and reported success
   * regardless — and it additionally rejects with `Document is not focused`, or fails outright when
   * another process is holding the Windows clipboard or a remote session has no redirection. None of
   * that reaches `clipboard.writeText`, which is a direct OS call from this process.
   *
   * Write only. `clipboard-read` stays denied (`permissions.ts`), and reading is not exposed here
   * either: this closes a gap in copying, it does not widen what the renderer can see.
   */
  ipcMain.handle("codeflow:clipboardWrite", (_event, text: string) => {
    if (typeof text !== "string") throw new Error("clipboard writes take a string");
    clipboard.writeText(text);
  });

  ipcMain.handle("codeflow:dialog", async (event, kind: "openFile" | "openDirectory" | "save", options) => {
    const window = BrowserWindow.fromWebContents(event.sender) ?? undefined;

    if (kind === "save") {
      const result = await dialog.showSaveDialog(window!, options ?? {});
      return result.canceled ? null : result.filePath;
    }

    const result = await dialog.showOpenDialog(window!, {
      ...(options ?? {}),
      properties: [kind === "openDirectory" ? "openDirectory" : "openFile"],
    });
    return result.canceled ? null : (result.filePaths[0] ?? null);
  });

  // The renderer's three callers are `quit_app` (Settings), `reset_app_data` and the updater's
  // relaunch. Which one it was matters enough to be worth the round trip, because they mean very
  // different things: the last two are quits the user did not ask for in so many words.
  ipcMain.handle("codeflow:quit", (_event, reason?: string) =>
    requestQuit(`the renderer asked (${reason ?? "no reason given"})`));
}

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

/**
 * Answers every web permission question from one list — camera, microphone, geolocation,
 * notifications and the rest are refused; the clipboard write behind every copy button is not.
 *
 * This owns the decision rather than leaving it to a default someone else maintains, and a future
 * Electron that flips one to "prompt" would not flip it here. It used to refuse *everything*, on
 * the premise that the app asks for none of them — and that premise was wrong in exactly one
 * place, which is why copying silently did nothing for as long as it did. The list, and why it has
 * one entry, live in `permissions.ts` next to their test.
 */
function answerPermissions(): void {
  session.defaultSession.setPermissionRequestHandler((_contents, permission, callback) => {
    if (!grants(permission)) console.warn(`[shell] denied a ${permission} permission request`);
    callback(grants(permission));
  });

  // Chromium asks through *this* one for `navigator.clipboard`, without a prior request — so a
  // handler that only got the request path right would still leave every copy button dead.
  session.defaultSession.setPermissionCheckHandler((_contents, permission) => grants(permission));
}

protocol.registerSchemesAsPrivileged([
  { scheme: "app", privileges: { standard: true, secure: true, supportFetchAPI: true, stream: true } },
]);

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on("second-instance", showWindow);

  void app.whenReady().then(async () => {
    // Started here and awaited below, so it runs while the window is being built rather than after.
    // It costs half a second on a profile with a version manager in it — measured, not guessed —
    // and that is worth overlapping.
    const widenedPath = applyLoginShellPath();

    answerPermissions();
    registerAppProtocol();
    registerBridge();
    installMenu();
    createTray();

    mainWindow = createWindow();

    try {
      // Before the sidecar, and only before it: the sidecar inherits `process.env` as it stands at
      // spawn time, and every CLI the app shells out to — the AI engines, `npx`, `code`, `gh` — is
      // found through it.
      await widenedPath;

      const endpoint = await startSidecar();
      await ipc.connect(endpoint);
      record("info", `[shell] the CodeFlow core is ready on ${endpoint}`);
    } catch (error) {
      const detail = error instanceof Error ? error.message : String(error);
      ipc.markDown(detail);
      record("error", `[shell] the CodeFlow core is unavailable: ${detail}`);
    }
  });

  // macOS keeps the app running with no windows; the Dock icon reopens it.
  app.on("activate", showWindow);

  app.on("window-all-closed", () => {
    if (process.platform !== "darwin") requestQuit("the last window closed on a platform with no tray");
  });

  // A renderer that dies takes the window with it and nothing else — recorded because a blank or
  // vanished window is otherwise indistinguishable from the app having quit, which is exactly the
  // confusion this whole trail exists to settle.
  app.on("render-process-gone", (_event, _contents, details) =>
    record("error", `[shell] the window's process is gone: ${details.reason} (exit ${details.exitCode})`));

  app.on("child-process-gone", (_event, details) =>
    record("error", `[shell] a ${details.type} process is gone: ${details.reason}`));

  app.on("before-quit", () => {
    // Reaching here with the flag still down means the quit came from somewhere that is not one of
    // the four — macOS asking the app to terminate, a signal, or Electron itself. The stack names
    // it, and it is the one case no amount of reading the source could have narrowed down.
    if (!quitting) {
      record("warn", `[shell] quitting: not through any of our own paths\n${new Error("quit").stack ?? ""}`);
    }

    quitting = true;
    stopSidecar();
  });
}
