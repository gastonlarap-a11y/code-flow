import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
// Monaco is deliberately absent from this file. Its setup used to be imported here, which put the
// whole editor in the entry chunk and made every launch pay for it. It now travels with the first
// component that actually needs an editor, through `lib/monacoEditor.ts`.

// The two typefaces the app names in `--font-sans` / `--font-mono`, self-hosted. Before this they
// were named and never loaded, so the app fell back to Segoe UI on Windows and -apple-system on
// macOS and rendered differently on each. Bundled rather than fetched for the same reason Monaco is:
// this app works offline. Only the upright weights ship — nothing here renders italic UI text — and
// each file carries a `unicode-range`, so a latin-script session never touches the greek or
// cyrillic subsets. These must precede `index.css`: the `@font-face` rules have to be registered
// before the rule that asks for the family.
import "@fontsource-variable/inter";
import "@fontsource-variable/jetbrains-mono";
import "./index.css";
import { pushErrorToast } from "./state/toastStore";
import { translate } from "./state/languageStore";
import { reloadForStaleChunk } from "./lib/lazyRetry";

/** Vite's own event, which it does not put in `WindowEventMap` (vitejs/vite#17508). The error is on
 * `payload`, not on `detail`. */
interface VitePreloadError extends Event {
  payload: Error;
}
declare global {
  interface WindowEventMap {
    "vite:preloadError": VitePreloadError;
  }
}

/**
 * The last resort for a promise nobody caught.
 *
 * `eslint.config.js` turns off `no-misused-promises`' `checksVoidReturn` for JSX attributes, on the
 * stated grounds that rewriting `onClick={asyncFn}` to `onClick={() => void asyncFn()}` leaves the
 * rejection exactly as unhandled as before. That reasoning holds, and it is what makes a net at the
 * edge the right place rather than a `try`/`catch` at each of the ninety-odd call sites: with none,
 * a backend failure reached by one of those handlers produced no message anywhere at all.
 *
 * Deliberately not a replacement for handling an error where it happens — a caller that knows what
 * failed can say something better than this can, and the four paths this bug was reported through
 * now do.
 */
window.addEventListener("unhandledrejection", (event) => {
  const reason: unknown = event.reason;
  // `.message`, not `String(reason)`: the latter prepends `Error: ` to text that already reads as a
  // sentence, and the transport had already left one of its own in there.
  pushErrorToast(
    translate("toast.unexpected", { error: reason instanceof Error ? reason.message : String(reason) }),
  );
});

/**
 * A chunk that no longer exists, caught before it reaches a component.
 *
 * Vite fires this from the preload helper it wraps every built dynamic import in — production only;
 * the dev server serves modules directly and has nothing to preload. The cause is always the same:
 * chunk names carry a content hash, an update replaced `renderer/dist`, and this window is still
 * running the `index.html` it started with, asking for hashes that are gone.
 *
 * Reloading is the only fix — there is no way to rewrite the running document's idea of the chunk
 * map — and `reloadForStaleChunk` allows exactly one, so a build that is broken rather than merely
 * stale surfaces as an error instead of an endless reload.
 */
window.addEventListener("vite:preloadError", (event) => {
  // Without this the rejection also reaches `window.onerror` as an unhandled failure.
  event.preventDefault();
  if (!reloadForStaleChunk()) {
    pushErrorToast(translate("toast.unexpected", { error: String((event as VitePreloadError).payload) }));
  }
});

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
