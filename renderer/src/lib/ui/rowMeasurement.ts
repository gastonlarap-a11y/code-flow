/** The shape `measureElement` gets handed, narrowed to what the decision actually reads. */
interface Measurable {
  getBoundingClientRect(): { height: number };
}

/**
 * How tall a virtualized row is, given what the observer saw.
 *
 * Two rules, and the second is the one worth having a test for.
 *
 * **Prefer the observer's box.** `ResizeObserver` reports sub-pixel sizes; a rounded
 * `getBoundingClientRect` fallback is only for the call that happens before any observation. Rows
 * here measure 23.5px, so rounding accumulates into a visibly wrong scroll height over a long tree.
 *
 * **A zero is not a height, it is an absence.** `App.tsx` keeps a view mounted and hides it with
 * `display: none` when you switch tabs, so a tree stays observed while off screen and every row
 * reports 0. Those zeros used to land in the virtualizer's size cache, and coming back the offsets
 * were computed from them — the list collapsed and the rows at the top went missing. Since
 * directories sort first, that read as "the folders disappeared", and only in the views you can
 * switch away from: the side panel unmounts its tree instead, so that one always came back right.
 */
export function measuredRowHeight(
  element: Measurable,
  entry: ResizeObserverEntry | undefined,
  rowHeight: number,
): number {
  const measured = entry?.borderBoxSize?.[0]?.blockSize ?? element.getBoundingClientRect().height;
  return measured > 0 ? measured : rowHeight;
}
