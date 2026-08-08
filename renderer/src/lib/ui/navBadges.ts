import type { ModuleId } from "../modules";

/**
 * What each navigation entry carries as a count.
 *
 * A function rather than a field on `AppModule` because a badge is a *reading of live state*, and
 * putting it in the registry would drag `repoStore` into `lib/modules.ts` — a file `uiStore` and
 * `shortcuts.ts` both import precisely because it pulls nothing in. The sidebar reads its stores
 * with hooks, in a fixed order, and hands the numbers here; this decides what becomes a badge.
 *
 * The rule that makes it worth a function at all: **absent, not zero.** A badge reading `0` on a
 * row that is quiet is noise on every row, every time — so a count of nothing produces no key, and
 * the component renders no pill.
 */
export function navBadges(input: {
  uncommittedChanges: number;
  /** Open and draft pull requests across the workspace's loaded projects. */
  openPrs: number;
}): Partial<Record<ModuleId, number>> {
  const badges: Partial<Record<ModuleId, number>> = {};
  if (input.uncommittedChanges > 0) badges.changes = input.uncommittedChanges;
  // On Home, not Graph: Home is the screen with the "open pull requests" card, so the number and
  // the list it counts are one click apart. A badge that leads somewhere else is a riddle.
  if (input.openPrs > 0) badges.home = input.openPrs;
  return badges;
}
