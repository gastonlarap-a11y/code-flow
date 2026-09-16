/** What `normalizeDocumentPath` answers: either the path to create, or why it cannot be one. */
export type DocumentPathResult =
  | { ok: true; relPath: string }
  | { ok: false; reason: DocumentPathError };

/**
 * Why a typed name is not a usable document path.
 *
 * A discriminated reason rather than a message, so the caller renders it through `translations.ts`
 * in the user's own language instead of the store inventing English prose.
 */
export type DocumentPathError = "empty" | "escapes" | "absolute" | "invalidChar";

/** Rejected outright: the printable characters a Windows file name cannot hold. */
const INVALID_CHARACTERS = /[<>:"|?*]/;

/**
 * Control characters, checked by code point rather than folded into the pattern above.
 *
 * A control-character range inside a character class is exactly what ESLint's `no-control-regex`
 * flags, and that rule is right often enough to be worth not silencing — one in a pattern is
 * usually a mistake. Here it is deliberate, since no file name holds one, so it says so in code.
 */
function hasControlCharacter(value: string): boolean {
  return [...value].some((character) => {
    const code = character.codePointAt(0)!;
    return code < 0x20 || code === 0x7f;
  });
}

/**
 * Turns what the user typed into the project-relative path of a new `.dbml` document.
 *
 * Three things happen here, and all three are the reason this is not inline in the modal:
 * - `.dbml` is appended when it is missing, so "orders" and "orders.dbml" name one file. The
 *   extension is what the sidecar's walk matches on, so a document saved without it would be
 *   invisible the moment the picker reloaded.
 * - Separators are normalised to `/`, because this path is half of the layout key and the same
 *   document typed as `db\orders` and `db/orders` has to be one row.
 * - Anything that could leave the project folder is refused *here*, not only at the boundary.
 *   `create_file` guards its own path (`PathGuards.ResolveNewPath`), but a refusal arriving from
 *   the sidecar reaches the user as a raw error string; this one reaches them as a labelled field.
 */
export function normalizeDocumentPath(input: string): DocumentPathResult {
  const trimmed = input.trim();
  if (trimmed.length === 0) return { ok: false, reason: "empty" };
  if (INVALID_CHARACTERS.test(trimmed) || hasControlCharacter(trimmed)) {
    return { ok: false, reason: "invalidChar" };
  }

  const slashed = trimmed.replace(/\\/g, "/");

  // A drive letter (`C:/…`) is caught by the invalid-character check above, which rejects `:`.
  if (slashed.startsWith("/")) return { ok: false, reason: "absolute" };

  const segments = slashed.split("/").filter((segment) => segment.length > 0);
  if (segments.length === 0) return { ok: false, reason: "empty" };
  if (segments.some((segment) => segment === "." || segment === "..")) {
    return { ok: false, reason: "escapes" };
  }

  const last = segments[segments.length - 1]!;
  const named = last.toLowerCase().endsWith(".dbml") ? last : `${last}.dbml`;
  // A name that is nothing but the extension (".dbml") names no document.
  if (named === ".dbml") return { ok: false, reason: "empty" };

  return { ok: true, relPath: [...segments.slice(0, -1), named].join("/") };
}

/** The file name a document path ends in, for titles and tabs. */
export function documentName(relPath: string): string {
  return relPath.split("/").pop() ?? relPath;
}
