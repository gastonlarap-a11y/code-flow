/**
 * Keyboard navigation for a list of options, as a pure function.
 *
 * `RowActions` (a vertical menu) and `Tabs` (a horizontal strip) need the same behaviour with
 * different arrow keys, and getting it wrong is the kind of bug nobody using a mouse ever reports:
 * an arrow key that stops dead on a disabled entry, or that refuses to wrap at the end of the list.
 *
 * Splitting it out is also the only way to test it. Renderer tests run without a DOM, so the
 * component cannot be rendered — but a reducer over `(state, key) -> state` can be driven through
 * every branch, which is what `menuNavigation.test.ts` does.
 *
 * `Select.tsx` already solves this inline for its own listbox. It is deliberately left alone: it is
 * the app's most mature primitive and rewriting it to route through here would be churn without a
 * defect to point at. If it ever needs a fix, this is where the fix should land.
 */

/** Which pair of arrow keys moves between options. */
export type MenuOrientation = "vertical" | "horizontal";

export interface MenuItemState {
  /** Disabled items are skipped by every movement and cannot be activated. */
  disabled?: boolean;
}

/** What the component should do after a key. `none` means the key was not ours — let it through. */
export type MenuAction =
  | { kind: "none" }
  | { kind: "move"; index: number }
  | { kind: "activate"; index: number }
  | { kind: "close" };

const NEXT_KEY: Record<MenuOrientation, string> = {
  vertical: "ArrowDown",
  horizontal: "ArrowRight",
};

const PREVIOUS_KEY: Record<MenuOrientation, string> = {
  vertical: "ArrowUp",
  horizontal: "ArrowLeft",
};

/**
 * The first selectable index at or after `from`, walking in `step` and wrapping once around.
 * Returns `-1` when every item is disabled — a menu that exists but has nothing to offer, which
 * happens for real (a row whose every action is unavailable in the current state).
 */
export function nextEnabledIndex(
  items: readonly MenuItemState[],
  from: number,
  step: 1 | -1,
): number {
  if (items.length === 0) return -1;

  const count = items.length;
  for (let hop = 1; hop <= count; hop++) {
    // The double modulo is what makes stepping backwards past zero wrap instead of going negative.
    const index = (((from + step * hop) % count) + count) % count;
    if (!items[index]?.disabled) return index;
  }
  return -1;
}

/** The first selectable index from the start (`step: 1`) or the end (`step: -1`). */
export function edgeEnabledIndex(items: readonly MenuItemState[], step: 1 | -1): number {
  const start = step === 1 ? -1 : items.length;
  return nextEnabledIndex(items, start, step);
}

/**
 * Maps a key press to what the menu should do. The caller keeps `activeIndex` and applies the
 * result; nothing here touches the DOM or the event.
 */
export function menuKeyAction(
  key: string,
  items: readonly MenuItemState[],
  activeIndex: number,
  orientation: MenuOrientation = "vertical",
): MenuAction {
  if (key === "Escape" || key === "Tab") return { kind: "close" };

  if (key === NEXT_KEY[orientation]) {
    const index = nextEnabledIndex(items, activeIndex, 1);
    return index === -1 ? { kind: "none" } : { kind: "move", index };
  }

  if (key === PREVIOUS_KEY[orientation]) {
    const index = nextEnabledIndex(items, activeIndex, -1);
    return index === -1 ? { kind: "none" } : { kind: "move", index };
  }

  if (key === "Home" || key === "End") {
    const index = edgeEnabledIndex(items, key === "Home" ? 1 : -1);
    return index === -1 ? { kind: "none" } : { kind: "move", index };
  }

  if (key === "Enter" || key === " ") {
    // Nothing focused, or focused on something unavailable: swallow it rather than fire the wrong
    // item. An empty menu answering Enter with its first action is exactly the sort of surprise a
    // keyboard user cannot undo.
    const item = activeIndex >= 0 ? items[activeIndex] : undefined;
    return item && !item.disabled ? { kind: "activate", index: activeIndex } : { kind: "none" };
  }

  return { kind: "none" };
}
