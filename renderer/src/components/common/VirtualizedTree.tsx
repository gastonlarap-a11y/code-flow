import type { Virtualizer } from "@tanstack/react-virtual";
import type { HTMLAttributes, ReactNode, RefObject } from "react";

/**
 * The scroll container, the sizer and the row positioning both trees share.
 *
 * `FileTree` and `CollectionTree` had the same virtualizer call (now `lib/useTreeVirtualizer.ts`),
 * the same sizer div, the same absolutely-positioned row wrapper and the same scroll-container
 * classes — down to a comment copied verbatim into both files. That is the part worth sharing, and
 * it is the *only* part: the two trees differ in ways a single component cannot paper over without
 * changing behaviour.
 *
 * What deliberately stays split, so the next person does not try again and find out the hard way:
 *
 * - **Drag semantics.** The file tree moves things *into* a directory; the collection tree moves
 *   them *between* ordered siblings, with edge-fraction hit zones and spring-load-to-expand,
 *   because collections carry a `sort_order` and directories do not. Two stores, two vocabularies.
 * - **Row markup.** A file row is a `<button>` and gets Enter/Space and a focus ring from the
 *   platform. A collection row cannot be — it contains its own buttons — so it is a
 *   `role="treeitem"` div that hand-rolls activation. Picking one for both means either giving the
 *   file tree ARIA it does not implement or taking native semantics away from rows that rely on
 *   them.
 * - **Data loading.** The file tree fetches a directory's children when you expand it, so "not
 *   loaded yet" and "loaded and empty" are different rows. The collection tree always has the whole
 *   tree in a store and has no such state to model.
 *
 * The row-level markup, the flatten and the drop logic therefore stay with their own tree. This
 * owns scrolling, measuring and positioning, and nothing that knows what a row means.
 */
export function VirtualizedTree<Row extends { id: string }>({
  rows,
  virtualizer,
  scrollRef,
  renderRow,
  className,
  sizerProps,
  overlay,
  children,
  ...containerProps
}: {
  rows: readonly Row[];
  /** From `useTreeVirtualizer`. Passed in rather than created here because the collection tree
   * reads `measurementsCache` to place its insertion line. */
  virtualizer: Virtualizer<HTMLDivElement, Element>;
  scrollRef: RefObject<HTMLDivElement | null>;
  renderRow: (row: Row, index: number) => ReactNode;
  /** Appended to the scroll container's own classes — a drop ring, usually. */
  className?: string;
  /** For the marker attributes one tree puts on the sizer to tell "clicked empty space" from
   * "clicked a row". The `data-*` index signature is what lets those be spread rather than
   * written inline — JSX allows unknown `data-` attributes, a typed props object does not. */
  sizerProps?: HTMLAttributes<HTMLDivElement> & { [key: `data-${string}`]: string };
  /** Absolutely-positioned decoration inside the sizer, above the rows: the insertion line. */
  overlay?: ReactNode;
  /** Rendered instead of the rows — a skeleton while the first listing lands, or an empty state. */
  children?: ReactNode;
} & Omit<HTMLAttributes<HTMLDivElement>, "className" | "children"> & {
  [key: `data-${string}`]: string | undefined;
}) {
  return (
    <div
      ref={scrollRef}
      className={`min-h-0 flex-1 overflow-auto py-1${className ? ` ${className}` : ""}`}
      {...containerProps}
    >
      {children ?? (
        <div
          {...sizerProps}
          style={{ height: virtualizer.getTotalSize(), position: "relative", width: "100%" }}
        >
          {virtualizer.getVirtualItems().map((item) => {
            const row = rows[item.index];
            // The index can outlive the row it pointed at for one render, when the list shrinks
            // before the virtualizer re-measures.
            if (!row) return null;
            return (
              <div
                key={item.key}
                ref={virtualizer.measureElement}
                data-index={item.index}
                style={{
                  position: "absolute",
                  top: 0,
                  left: 0,
                  width: "100%",
                  transform: `translateY(${item.start}px)`,
                }}
              >
                {renderRow(row, item.index)}
              </div>
            );
          })}
          {overlay}
        </div>
      )}
    </div>
  );
}
