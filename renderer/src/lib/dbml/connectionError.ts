/**
 * The sidecar's marker for "that database refused, or could not be reached" (DBML-024).
 *
 * `VERBATIM`. Composed in `Dbml/IDbmlIntrospector.cs` and matched here by text, the way
 * `CREDENTIAL_REFUSED: `, `STALE_REVIEW: ` and the rest are — see
 * `docs/business-rules/13-cross-language-contracts.md`. Changing one side alone breaks the feature
 * silently: the message still reaches the screen, it just arrives wearing its marker.
 */
export const DB_CONNECTION_REFUSED = "DB_CONNECTION_REFUSED: ";

/**
 * The driver's own sentence, with the marker taken off.
 *
 * Returns `null` when the failure is something else, so a caller can tell "the database said no"
 * from "the command blew up", which read identically before and lead to different next steps: one
 * is a wrong port, the other is a bug.
 */
export function connectionRefusal(error: unknown): string | null {
  const text = String(error);
  const at = text.indexOf(DB_CONNECTION_REFUSED);

  return at < 0 ? null : text.slice(at + DB_CONNECTION_REFUSED.length).trim();
}
