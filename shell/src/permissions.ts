/**
 * Which web permissions this app grants, and the one it needs.
 *
 * Denying everything was the posture until it turned out to be a lie: `navigator.clipboard`
 * resolves against `clipboard-sanitized-write`, so a blanket `false` killed every copy button in
 * the app — the five behind `useCopy`, the seven elsewhere, and the image copy in CodeSnap. The
 * renderer reported success anyway, which is why it went unnoticed for as long as it did.
 *
 * `clipboard-read` stays denied. Writing is something the user asked for by clicking a copy
 * button; reading is the app helping itself to whatever they happen to be carrying. The one
 * feature that wants it — the PR-link modal's autofill — is already written to treat a refusal as
 * a no-op, and typing ⌘V still works because the paste shortcut is the OS's, not the page's.
 *
 * A module of its own, and a set rather than a condition, because the decision is worth a test
 * that names each permission: getting a literal wrong here fails silently, exactly as it did.
 */
const GRANTED = new Set(["clipboard-sanitized-write"]);

/** Whether a permission request or check should be answered yes. */
export function grants(permission: string): boolean {
  return GRANTED.has(permission);
}
