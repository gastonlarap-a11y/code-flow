import { useVirtualizer, type Virtualizer } from "@tanstack/react-virtual";
import type { RefObject } from "react";
import { useDensityStore } from "../state/densityStore";

/**
 * The windowing both trees run on, set up once.
 *
 * `FileTree` and `CollectionTree` had this call twice, identical down to a comment copied into
 * both files. It sits in `lib/` beside `useDialog`/`useFocusTrap` rather than next to
 * `VirtualizedTree`, which renders what it measures — the component file exports a component and
 * nothing else, which is what keeps fast refresh working on it.
 */
export function useTreeVirtualizer<Row extends { id: string }>(
  rows: readonly Row[],
  scrollRef: RefObject<HTMLDivElement | null>,
): Virtualizer<HTMLDivElement, Element> {
  // Subscribed rather than read off the CSS variable: the row height is a user preference, and the
  // virtualizer has to re-measure when it changes, which means it has to re-render. The variable
  // alone would repaint the rows while the scroll offsets still assumed the old height.
  const rowHeight = useDensityStore((s) => s.rowHeight);

  return useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    // Rows declare their height (`--cf-row-height`), so this is exact rather than a seed: it used
    // to guess 24 for rows that measured 23.5px because their height fell out of `py-0.5` around
    // 13px text. `measureElement` still corrects it, but there is nothing left to correct.
    estimateSize: () => rowHeight,
    // Guarded rather than asserted with `!`. The virtualizer can ask for a key at an index the
    // list no longer reaches, for the render between a collapse shrinking `rows` and the
    // re-measure — the same window `VirtualizedTree` already guards when it renders. Throwing
    // there would take the whole tree down; falling back to the index only costs that one row its
    // identity for one frame.
    getItemKey: (index) => rows[index]?.id ?? index,
    overscan: 10,
  });
}
