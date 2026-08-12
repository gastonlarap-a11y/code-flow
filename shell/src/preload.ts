import { contextBridge, ipcRenderer, webUtils } from "electron";

/**
 * The only surface the renderer sees.
 *
 * Two primitives carry the whole 218-command, 13-event contract: `invoke` and `on`. That is what
 * lets `lib/ipc/commands.ts`, `apiCommands.ts` and `events.ts` keep their bodies — each imports
 * its primitive on line 1, so pointing that import at a the shell-shaped shim is the entire port of
 * those 1 116 lines.
 *
 * Raw `ipcRenderer` is never exposed, and there is no bridged function per command. Anything the
 * renderer needs beyond the command surface is a named capability below, matching exactly what
 * 1.7.2 granted in the preload bridge.
 */

type EventPayload = unknown;
type EventHandler = (payload: EventPayload) => void;

const listeners = new Map<string, Set<EventHandler>>();

ipcRenderer.on("codeflow:event", (_event, name: string, payload: EventPayload) => {
  const handlers = listeners.get(name);
  if (!handlers) return;
  for (const handler of handlers) handler(payload);
});

const api = {
  invoke<T>(method: string, params?: Record<string, unknown>): Promise<T> {
    return ipcRenderer.invoke("codeflow:invoke", method, params ?? {}) as Promise<T>;
  },

  on(event: string, handler: EventHandler): () => void {
    let handlers = listeners.get(event);
    if (!handlers) {
      handlers = new Set();
      listeners.set(event, handlers);
    }
    handlers.add(handler);
    return () => {
      handlers.delete(handler);
      if (handlers.size === 0) listeners.delete(event);
    };
  },

  /**
   * Resolved once, synchronously, at preload time.
   *
   * `lib/platform.ts` memoises this and calls it from non-React code — key handling, stores —
   * so it cannot be a promise. Returning one would make every modifier-key decision in the app
   * asynchronous.
   */
  platform: (): "macos" | "windows" | "linux" | "unknown" => {
    switch (process.platform) {
      case "darwin":
        return "macos";
      case "win32":
        return "windows";
      case "linux":
        return "linux";
      default:
        return "unknown";
    }
  },

  window: {
    minimize: () => ipcRenderer.invoke("codeflow:window", "minimize"),
    toggleMaximize: () => ipcRenderer.invoke("codeflow:window", "toggleMaximize"),
    close: () => ipcRenderer.invoke("codeflow:window", "close"),
    isMaximized: (): Promise<boolean> => ipcRenderer.invoke("codeflow:window", "isMaximized"),
    setTheme: (theme: "light" | "dark" | null) => ipcRenderer.invoke("codeflow:setTheme", theme),
  },

  dialog: {
    openFile: (options?: unknown): Promise<string | null> =>
      ipcRenderer.invoke("codeflow:dialog", "openFile", options),
    openDirectory: (options?: unknown): Promise<string | null> =>
      ipcRenderer.invoke("codeflow:dialog", "openDirectory", options),
    save: (options?: unknown): Promise<string | null> =>
      ipcRenderer.invoke("codeflow:dialog", "save", options),
  },

  openExternal: (url: string): Promise<void> => ipcRenderer.invoke("codeflow:openExternal", url),

  /**
   * Puts text on the clipboard through the OS rather than through `navigator.clipboard`.
   *
   * Write only, and it takes no argument beyond the text — the main process decides everything else.
   */
  clipboardWrite: (text: string): Promise<void> =>
    ipcRenderer.invoke("codeflow:clipboardWrite", text),

  /** Reveals the app's own log directory. Takes no path: the main process owns which one. */
  openLogs: (): Promise<void> => ipcRenderer.invoke("codeflow:openLogs"),

  /**
   * Whether the .NET core is answering, for the renderer to say so.
   *
   * A getter as well as the `codeflow:sidecar-status` event, because the failure worth reporting —
   * a core that never started — resolves before this window has a listener attached.
   */
  sidecarStatus: (): Promise<{
    status: "starting" | "ready" | "down";
    detail?: string;
    logsDirectory: string;
  }> => ipcRenderer.invoke("codeflow:sidecarStatus"),

  /**
   * The absolute path of a dropped `File`.
   *
   * CodeFlow 1.7.2 reads dropped paths through the shell's webview channel because the shell suppresses
   * DOM drop events. Electron delivers them normally, but a `File` in the renderer has no path —
   * `webUtils.getPathForFile` is the supported way to recover one, and it only works from the
   * preload. Same contract, different mechanism.
   */
  pathForFile: (file: File): string => webUtils.getPathForFile(file),

  /**
   * Ends the app, naming the caller for the log.
   *
   * The reason is validated here rather than trusted: this is the security boundary, and what it
   * carries goes straight into a file. A non-string is dropped instead of stringified, so nothing
   * the renderer can construct decides the shape of a log line.
   */
  quit: (reason?: unknown): Promise<void> =>
    ipcRenderer.invoke("codeflow:quit", typeof reason === "string" ? reason.slice(0, 120) : undefined),
} as const;

contextBridge.exposeInMainWorld("codeflow", api);

export type CodeFlowBridge = typeof api;
