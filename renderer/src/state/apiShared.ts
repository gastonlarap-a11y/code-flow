/**
 * Helpers shared by the API domain stores (`apiTreeStore`, `apiTabsStore`, …). Nothing here
 * holds state — this is the plumbing each split store would otherwise have to duplicate.
 */

import { pushErrorToast } from "./toastStore";

/** Every store action funnels its failure into one toast; nothing here is worth a modal. */
export async function guarded<T>(fn: () => Promise<T>): Promise<T | null> {
  try {
    return await fn();
  } catch (e) {
    pushErrorToast(String(e));
    return null;
  }
}

export function parseJson<T>(raw: string | null, fallback: T): T {
  if (raw === null || raw === "") return fallback;
  try {
    return JSON.parse(raw) as T;
  } catch {
    return fallback;
  }
}

/** The `tab-` prefix is historical — variables reuse this generator and carry it too. */
export function newId(): string {
  return `tab-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}
