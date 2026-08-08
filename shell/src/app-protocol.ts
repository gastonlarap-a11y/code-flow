import { isAbsolute, relative, sep } from "node:path";

/**
 * The parts of the `app://` handler that are decidable without Electron.
 *
 * `main.ts` cannot be unit-tested — it imports `electron`, which outside a running Electron process
 * resolves to a string, not an API. So the two decisions in `protocol.handle` that can actually be
 * wrong in a way nobody notices — is this path inside the renderer root, and what content type does
 * it get — live here instead, where `node --test` can reach them. The same split `login-path.ts`
 * already uses.
 */

/**
 * Whether <paramref name="target" /> resolves inside <paramref name="root" />.
 *
 * A plain `target.startsWith(root)` is the obvious version and it is wrong: with a root of
 * `/a/renderer` it also accepts `/a/renderer-evil/secret`, because the prefix matches without a
 * separator behind it. Comparing the relative path instead has no such edge — anything outside
 * produces a `..` segment or an absolute path, and both are rejected.
 *
 * The root itself counts as inside, so a request for the directory is not treated as an escape.
 */
export function isWithinRoot(root: string, target: string): boolean {
  const rel = relative(root, target);

  if (rel === "") return true;
  if (isAbsolute(rel)) return false;

  return rel !== ".." && !rel.startsWith(`..${sep}`);
}

/**
 * The `content-type` for a file served over `app://`.
 *
 * Text types carry `charset=utf-8` explicitly. Without it the browser falls back to a heuristic,
 * and the renderer ships 2 870 lines of translations whose Spanish accents are exactly what such a
 * heuristic gets wrong — as mojibake in the UI, not as an error anyone would trace back to a
 * missing header.
 */
export function contentTypeFor(path: string): string {
  if (path.endsWith(".html")) return "text/html; charset=utf-8";
  if (path.endsWith(".js") || path.endsWith(".mjs")) return "text/javascript; charset=utf-8";
  if (path.endsWith(".css")) return "text/css; charset=utf-8";
  if (path.endsWith(".json")) return "application/json; charset=utf-8";
  if (path.endsWith(".svg")) return "image/svg+xml; charset=utf-8";
  if (path.endsWith(".woff2")) return "font/woff2";
  if (path.endsWith(".ttf")) return "font/ttf";
  if (path.endsWith(".png")) return "image/png";
  return "application/octet-stream";
}

/**
 * The policy the renderer document is served under.
 *
 * Two directives are looser than they look, and both are load-bearing rather than lazy:
 *
 * `script-src` carries `'unsafe-eval'` because the API client compiles the user's own pre-request
 * and post-response scripts with `new Function` (`renderer/src/lib/api/sandbox.ts`). CSP has no
 * per-module scope, so allowing it for that one feature means allowing it for the document. What
 * the directive still buys is the part that matters here: an injected `<script>` tag or an
 * `onerror=` handler — the shape an XSS out of rendered repo markdown would take — is refused,
 * because neither `'self'` nor `'unsafe-eval'` permits inline script.
 *
 * `style-src` carries `'unsafe-inline'` for two independent reasons. Monaco builds its theme and
 * decoration rules as real `<style>` elements at runtime, and DOMPurify's default allow-list keeps
 * the `style` attribute, so markdown from a repo can legitimately carry one.
 *
 * Everything else is as tight as it goes. `connect-src 'none'` in particular is not a compromise:
 * the renderer opens no sockets and issues no `fetch` — every byte of remote IO goes through the
 * sidecar — so there is no origin to allow.
 *
 * This is defence in depth, not immunity. With `'unsafe-eval'` present, a DOMPurify bypass still
 * reaches script execution; what the policy removes is remote script and style loading, plugin
 * content, `<base>` rewriting and form-action exfiltration.
 */
export const CONTENT_SECURITY_POLICY = [
  "default-src 'self'",
  "script-src 'self' 'unsafe-eval'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data:",
  "font-src 'self'",
  "worker-src 'self'",
  "connect-src 'none'",
  "object-src 'none'",
  "base-uri 'none'",
  "form-action 'none'",
  "frame-src 'none'",
].join("; ");
