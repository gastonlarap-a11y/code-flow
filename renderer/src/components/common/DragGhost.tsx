import { createPortal } from "react-dom";
import type { RefObject } from "react";

/**
 * The label that follows the cursor while a tree row is being dragged.
 *
 * Portalled so no ancestor's `overflow` clips it, and click-through so it never becomes the element
 * `elementFromPoint` finds under the cursor — which is how the drop target is resolved, so a ghost
 * that could be hit would make every drop land on itself.
 *
 * The `ref` stays with the caller: both drags move the ghost by writing `transform` directly on the
 * node during `pointermove`, rather than re-rendering a component sixty times a second.
 */
export function DragGhost({
  ghostRef,
  x,
  y,
  label,
}: {
  ghostRef: RefObject<HTMLDivElement | null>;
  x: number;
  y: number;
  label: string;
}) {
  return createPortal(
    <div
      ref={ghostRef}
      // Offset off the cursor so the pointer itself stays over the row underneath.
      style={{ transform: `translate(${x + 12}px, ${y + 12}px)` }}
      className="pointer-events-none fixed left-0 top-0 z-[100] rounded-md border border-[var(--cf-accent)] bg-[var(--cf-surface)] px-2 py-1 text-badge text-[var(--cf-text)] shadow-lg"
    >
      {label}
    </div>,
    document.body,
  );
}
