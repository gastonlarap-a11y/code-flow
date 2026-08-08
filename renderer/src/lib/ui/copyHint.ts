import type { Platform } from "../platform";

/**
 * The manual-copy chord to name when the clipboard refuses.
 *
 * `common.copyFailed` used to end with a hardcoded `⌘C` — in **both** locales. On Windows that told
 * a user to press a key their keyboard does not have, inside a message that (until the toast became
 * selectable) they could not copy either. The worst possible recovery instruction is one that names
 * a key you do not have.
 *
 * A pure function taking the platform rather than reading it, so it is testable under
 * `environment: "node"`, where `lib/platform.ts`'s bridge does not exist. The app passes
 * `currentPlatform()`.
 */
export function manualCopyChord(platform: Platform): string {
  return platform === "macos" ? "⌘C" : "Ctrl+C";
}
