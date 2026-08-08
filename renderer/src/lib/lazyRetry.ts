import { lazy } from "react";

/** Marks the one reload this module is allowed to perform, so a chunk that is gone for good does
 * not put the window in a reload loop. */
const RELOADED_KEY = "cf_chunk_reload";

/**
 * Whether an error is a chunk that failed to arrive, rather than a bug in the component.
 *
 * Matched on the message because that is all the platform gives: a dynamic import that 404s rejects
 * with a plain `TypeError` whose text differs per engine. Chromium is the only engine this app runs
 * on, but the Vite-generated preload helper produces its own wording, so both are listed.
 */
export function isChunkLoadError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error);
  return (
    message.includes("Failed to fetch dynamically imported module") ||
    message.includes("error loading dynamically imported module") ||
    message.includes("Importing a module script failed")
  );
}

/**
 * Reloads the window once, to pick up an `index.html` that names chunks which still exist.
 *
 * Returns `false` when it has already been spent, which is the caller's cue to show an error rather
 * than try again — a permanently broken build would otherwise reload forever.
 */
export function reloadForStaleChunk(): boolean {
  if (sessionStorage.getItem(RELOADED_KEY)) return false;
  sessionStorage.setItem(RELOADED_KEY, "1");
  window.location.reload();
  return true;
}

/**
 * `lazy`, but a failed fetch gets a second chance.
 *
 * Chunk filenames carry a content hash, and this app never reloads its window — `shell/src/main.ts`
 * pins it to `app://codeflow/` for the life of the process. So when an update replaces the renderer
 * directory underneath a running window, the entry chunk still asks for hashes that are no longer
 * on disk and the import 404s. That is the failure reported as "the API tool died".
 *
 * Two things make that unrecoverable without this wrapper:
 *
 * 1. **A rejected import is fatal to the component forever.** `lazy` memoizes the *first* promise
 *    its factory returns and caches the rejection; every later render re-throws the cached error
 *    without asking the network again. Retrying by remounting cannot work — the retry has to happen
 *    inside the factory, before `lazy` ever sees a rejection.
 * 2. Nothing else notices. Without the retry the only cure is quitting the app.
 *
 * One retry, after a short pause, covers the genuinely transient case. A hash that is gone will
 * fail twice, and then the error reaches the boundary, which offers a reload — the only thing that
 * can actually fix a stale document.
 */
// Typed as `typeof lazy` rather than with a generic of its own: React's own signature already says
// exactly what a lazy factory is, and borrowing it keeps every call site's props typed without this
// module having to restate — or cast its way around — React's variance rules.
export const lazyRetry: typeof lazy = (load) =>
  lazy(async () => {
    try {
      return await load();
    } catch (error) {
      if (!isChunkLoadError(error)) throw error;
      await new Promise((resolve) => setTimeout(resolve, 400));
      return load();
    }
  });
