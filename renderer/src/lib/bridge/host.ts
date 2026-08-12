/**
 * `invoke` and `listen` over the Electron bridge.
 *
 * This file exists so `lib/ipc/commands.ts`, `apiCommands.ts` and `events.ts` keep their
 * bodies. Each of them imports its primitive exactly once, on line 1 — all 212 wrappers call
 * `invoke<T>("name", argsObject)` and all 11 call `listen<T>("event", e => handler(e.payload))`.
 * Matching those two signatures precisely turns this codebase of 1 174 lines into three import lines,
 * and everything downstream compiles untouched.
 *
 * Which makes fidelity here the thing to get right. `listen` in particular returns a
 * `Promise<UnlistenFn>`, not an `UnlistenFn` — `App.tsx` does `unlisten.then((f) => f())`, so
 * returning the function directly would break cleanup in a way TypeScript would not catch.
 */

/** Matches the event-payload type. */
export type UnlistenFn = () => void;

/** Matches the shape `listen` hands its callback. */
export interface Event<T> {
  payload: T;
}

type Bridge = {
  invoke<T>(method: string, params?: Record<string, unknown>): Promise<T>;
  on(event: string, handler: (payload: unknown) => void): UnlistenFn;
  platform(): "macos" | "windows" | "linux" | "unknown";
  window: {
    minimize(): Promise<unknown>;
    toggleMaximize(): Promise<unknown>;
    close(): Promise<unknown>;
    isMaximized(): Promise<boolean>;
    setTheme(theme: "light" | "dark" | null): Promise<unknown>;
  };
  dialog: {
    openFile(options?: unknown): Promise<string | null>;
    openDirectory(options?: unknown): Promise<string | null>;
    save(options?: unknown): Promise<string | null>;
  };
  openExternal(url: string): Promise<void>;
  /** Write-only, and through the OS rather than `navigator.clipboard` — see `lib/ui/useCopy.ts`. */
  clipboardWrite(text: string): Promise<void>;
  /** Reveals the app's log directory. Takes no path: the shell owns which one. */
  openLogs(): Promise<void>;
  /** `reason` is recorded in `shell.log`; the three callers here mean very different things. */
  quit(reason: string): Promise<void>;
  /** Asked at mount, because the `codeflow:sidecar-status` event fires before this window exists. */
  sidecarStatus(): Promise<{
    status: "starting" | "ready" | "down";
    detail?: string;
    logsDirectory: string;
  }>;
  /** Absolute path of a dropped `File`; only the preload can resolve it. */
  pathForFile(file: File): string;
};

declare global {
  interface Window {
    codeflow?: Bridge;
  }
}

function bridge(): Bridge {
  const value = window.codeflow;
  if (!value) {
    // Running the renderer in a plain browser (`pnpm dev` without the shell) is a legitimate way
    // to work on pure UI, so this fails per call rather than at import time.
    throw new Error("the CodeFlow shell bridge is unavailable — is this running outside Electron?");
  }
  return value;
}

/**
 * Electron's own wrapper around a rejected `ipcMain.handle`.
 *
 * `ipcMain.handle` re-throws every failure as `Error invoking remote method 'codeflow:invoke':
 * <the real message>`. That prefix names an internal channel and says nothing to anyone — and it
 * reaches the screen: the AI panel renders a failed run's message verbatim, so a user analysing a
 * clean working tree was shown `codeflow:invoke` as the explanation.
 *
 * Stripped here rather than at each of the 212 wrappers, because here is where it is added.
 */
const ELECTRON_INVOKE_PREFIX = /^Error invoking remote method '[^']*':\s*/;

/**
 * The bare `Error: ` Electron leaves behind after its own wrapper.
 *
 * It serialises the rejection as `Error invoking remote method '<channel>': Error: <message>` — the
 * inner one is `Error.prototype.toString`, not part of what the sidecar said. Removing only the
 * outer wrapper left it, and a caller that then does `String(error)` adds a third, which is how a
 * user was shown `Something failed unexpectedly: Error: Error: the CodeFlow core is not running`.
 *
 * Anchored and applied once, after the wrapper: a message that legitimately begins with `Error:` in
 * its own text is not something the sidecar produces, and one nested deeper is left alone.
 */
const REDUNDANT_ERROR_PREFIX = /^Error:\s+/;

/** The message a backend failure should carry, with the transport's own noise removed. */
export function unwrapInvokeError(message: string): string {
  const unwrapped = message.replace(ELECTRON_INVOKE_PREFIX, "");
  return unwrapped === message ? unwrapped : unwrapped.replace(REDUNDANT_ERROR_PREFIX, "");
}

/**
 * Calls a backend command.
 *
 * Rejects rather than hangs when the core is unavailable: the shell settles every call, either
 * because the core answered or because it is known to be down.
 */
export function invoke<T>(method: string, params?: Record<string, unknown>): Promise<T> {
  return bridge()
    .invoke<T>(method, params)
    .catch((error: unknown) => {
      // Rethrown rather than mutated: an Error's `message` is writable but its stack already carries
      // the old text, and callers compare messages (`STALE_REVIEW: `, `QUOTA_EXCEEDED::` and the
      // rest) rather than inspect the instance.
      throw error instanceof Error ? new Error(unwrapInvokeError(error.message), { cause: error }) : error;
    });
}

/** Subscribes to a backend event. */
export function listen<T>(event: string, handler: (event: Event<T>) => void): Promise<UnlistenFn> {
  // Wrapped in an already-resolved promise to keep the signature synchronous-looking. The subscription
  // itself is synchronous here, but callers store the promise and `.then` it to unsubscribe.
  return Promise.resolve(bridge().on(event, (payload) => handler({ payload: payload as T })));
}

/** Everything the renderer needs that is not a backend command. */
export const host = {
  platform: () => bridge().platform(),
  window: () => bridge().window,
  dialog: () => bridge().dialog,
  openExternal: (url: string) => bridge().openExternal(url),
  clipboardWrite: (text: string) => bridge().clipboardWrite(text),
  openLogs: () => bridge().openLogs(),
  quit: (reason: string) => bridge().quit(reason),
  sidecarStatus: () => bridge().sidecarStatus(),
  /** True when the shell is present, for the few places that degrade rather than fail. */
  available: () => typeof window !== "undefined" && window.codeflow !== undefined,
};
