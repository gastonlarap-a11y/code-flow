/**
 * The most-recently-opened project ids, newest first.
 *
 * Home wants "recent projects" and nothing in the app knows what recent means: the `projects` table
 * carries `sort_order` and `created_at`, the store persists a single `last_active_project_id`, and
 * neither answers "which four did I work on this week". The honest options were a schema migration
 * or a client-side list; this is the list, kept in one app-setting through the same `setSetting`
 * the active-project id already uses, so no table changes and nothing new in the sidecar.
 *
 * What that buys and what it costs: recency is real and survives a restart, but it lives on this
 * machine, so a second install starts with an empty list. For a landing page that is the right
 * trade — a wrong order is a worse answer than an order that has to be earned.
 */
const MAX_RECENT = 8;

/** Moves `projectId` to the front, keeping every other entry in order and dropping the overflow.
 * Re-opening a project already at the front returns the same list, so callers can skip the write. */
export function pushRecent(recent: readonly string[], projectId: string): string[] {
  return [projectId, ...recent.filter((id) => id !== projectId)].slice(0, MAX_RECENT);
}

/**
 * Reads the list back, keeping only ids that still name a project.
 *
 * A deleted repository leaves its id behind in the setting, and a Home card is exactly where a row
 * pointing at nothing would be clicked. Filtering on read rather than on delete means no listener
 * has to stay in sync with every path that removes a project.
 */
export function resolveRecent<T extends { id: string }>(recent: readonly string[], projects: readonly T[]): T[] {
  const byId = new Map(projects.map((p) => [p.id, p]));
  return recent.flatMap((id) => {
    const project = byId.get(id);
    return project ? [project] : [];
  });
}

/** Parses the stored setting. Anything that is not a list of strings reads as no history — a
 * corrupt value should cost an empty card, never a crash on the landing page. */
export function parseRecent(raw: string | null): string[] {
  if (!raw) return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((id): id is string => typeof id === "string").slice(0, MAX_RECENT);
  } catch {
    return [];
  }
}
