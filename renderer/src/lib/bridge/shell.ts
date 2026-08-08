import { host } from "./host";

/**
 * Opening a URL, reading the platform, and the current-window handle, backed by the Electron shell.
 *
 * Grouped in one file because they are the same thing — the small non-command surface the
 * renderer needs from the host — and each is a handful of lines.
 */

/** Opens a URL in the system browser. The shell rejects anything that is not http or https. */
export function openUrl(url: string): Promise<void> {
  return host.openExternal(url);
}

export type Platform = "macos" | "windows" | "linux" | "unknown";

/**
 * The current OS, resolved synchronously.
 *
 * `lib/platform.ts` memoises this and calls it from non-React code — key handling, stores — so
 * it cannot become a promise. The preload resolves it at load time for exactly this reason.
 */
export function platform(): Platform {
  return host.platform();
}

/** The subset of the window handle the title bar actually uses. */
export interface CurrentWindow {
  minimize(): Promise<unknown>;
  toggleMaximize(): Promise<unknown>;
  close(): Promise<unknown>;
  isMaximized(): Promise<boolean>;
  setTheme(theme: "light" | "dark" | null): Promise<unknown>;
}

export function getCurrentWindow(): CurrentWindow {
  return host.window();
}
