/**
 * Reordering a list by id, as the two operations a reorderable list actually has.
 *
 * `moveTo` is what a pointer drag produces: an *insertion slot*, meaning "put it before whatever is
 * at this index right now". `moveBy` is what a keyboard produces: one step up or down. They must
 * agree — a list that reorders differently depending on which input you used is a list nobody can
 * predict — so `moveBy` is expressed in terms of `moveTo` rather than reimplementing the splice.
 *
 * This lives in `lib/ui/` and not beside `RunnerModal`, which is where it started, because renderer
 * tests run without a DOM and a function inside a `.tsx` is untestable by construction. The
 * off-by-one below (removing the moved entry shifts every later slot down by one) is exactly the
 * kind of thing that needs a test rather than a comment.
 */

/**
 * `id` moved so that it lands *before* whatever currently sits at `slot`. `slot` may be
 * `list.length`, meaning the end. Returns the list unchanged when `id` is not in it.
 */
export function moveTo(list: readonly string[], id: string, slot: number): string[] {
  const current = list.indexOf(id);
  if (current < 0) return [...list];
  const without = list.filter((entry) => entry !== id);
  // Removing the moved entry first shifts every later slot down by one.
  const insert = slot > current ? slot - 1 : slot;
  without.splice(Math.max(0, Math.min(insert, without.length)), 0, id);
  return without;
}

/**
 * `id` moved one position toward the start (`step: -1`) or the end (`step: 1`).
 *
 * At either end this is a no-op and returns an equal list, so a held arrow key stops rather than
 * wrapping — wrapping would send the first entry to the bottom on a key press meant to do nothing.
 */
export function moveBy(list: readonly string[], id: string, step: 1 | -1): string[] {
  const current = list.indexOf(id);
  if (current < 0) return [...list];
  if (step === -1 ? current === 0 : current === list.length - 1) return [...list];
  // `current + 2` and not `current + 1`: the slot is read against the list *before* the removal.
  return moveTo(list, id, step === 1 ? current + 2 : current - 1);
}
