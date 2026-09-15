import {
  forwardRef,
  useCallback,
  useEffect,
  useImperativeHandle,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
  type PointerEvent,
} from "react";
import { KeyRound, Table2 } from "lucide-react";
import type { DbmlSchemaModel } from "../../lib/dbml/model";
import { CARD_PADDING, HEADER_HEIGHT, ROW_HEIGHT, boundsOf, computeLayout, type Point, type Rect } from "../../lib/dbml/layout";
import { relationSegments } from "../../lib/dbml/edges";
import { IDENTITY, fitBounds, zoomAt, type Viewport } from "../../lib/dbml/viewport";
import { DRAG_THRESHOLD, setDragCursor } from "../../lib/pointerDrag";
import { useT } from "../../state/languageStore";

/** What the toolbar outside the canvas can ask of it. */
export interface DbmlCanvasHandle {
  fit: () => void;
  zoomBy: (factor: number) => void;
}

interface DbmlCanvasProps {
  model: DbmlSchemaModel;
  /** Changes when a different document is shown, so the canvas fits itself once for it. */
  documentKey: string;
  positions: Readonly<Record<string, Point>>;
  /** A table was dropped, or nudged from the keyboard. */
  onPlace: (tableKey: string, point: Point) => void;
}

/** Arrow-key nudge, and with Shift held. */
const NUDGE = 16;
const NUDGE_FAST = 64;
/** One wheel notch of zoom. */
const WHEEL_ZOOM = 1.1;
/** Background dot spacing at scale 1. */
const GRID = 24;

interface CardDrag {
  key: string;
  pointerId: number;
  start: Point;
  origin: Point;
  scale: number;
  moved: boolean;
}

interface Pan {
  pointerId: number;
  start: Point;
  origin: Viewport;
}

/**
 * The schema diagram: cards placed by `computeLayout`, relationship lines between the columns they
 * name, pan and zoom, and tables a person can drag (DBML-008).
 *
 * While a card is being dragged only that card moves; the layout is recomputed once, on drop. Doing
 * it on every pointer move would let the push-down rule shove the other cards around under the
 * cursor, which reads as the diagram fighting the user.
 */
export const DbmlCanvas = forwardRef<DbmlCanvasHandle, DbmlCanvasProps>(function DbmlCanvas(
  { model, documentKey, positions, onPlace },
  ref,
) {
  const t = useT();
  const containerRef = useRef<HTMLDivElement>(null);
  const [view, setView] = useState<Viewport>(IDENTITY);
  const [dragPoint, setDragPoint] = useState<{ key: string; point: Point } | null>(null);
  const cardDrag = useRef<CardDrag | null>(null);
  const pan = useRef<Pan | null>(null);

  const tablesByKey = useMemo(() => new Map(model.tables.map((table) => [table.key, table])), [model]);
  const layout = useMemo(() => computeLayout(model, new Map(Object.entries(positions))), [model, positions]);

  const drawn = useMemo(() => {
    if (dragPoint === null) return layout;
    const rect = layout.get(dragPoint.key);
    if (!rect) return layout;
    return new Map(layout).set(dragPoint.key, { ...rect, ...dragPoint.point });
  }, [layout, dragPoint]);

  const segments = useMemo(() => relationSegments(model.refs, tablesByKey, drawn), [model.refs, tablesByKey, drawn]);

  const fit = useCallback(() => {
    const container = containerRef.current;
    if (!container) return;
    const { width, height } = container.getBoundingClientRect();
    setView(fitBounds(boundsOf(layout.values()), { width, height }));
  }, [layout]);

  const zoomBy = useCallback((factor: number) => {
    const container = containerRef.current;
    if (!container) return;
    const { width, height } = container.getBoundingClientRect();
    setView((current) => zoomAt(current, { x: width / 2, y: height / 2 }, factor));
  }, []);

  useImperativeHandle(ref, () => ({ fit, zoomBy }), [fit, zoomBy]);

  // Fit once per document, as soon as it has something to show — not on every keystroke, which
  // would yank the view around while the user types.
  const fittedFor = useRef<string | null>(null);
  useEffect(() => {
    if (layout.size === 0 || fittedFor.current === documentKey) return;
    fittedFor.current = documentKey;
    fit();
  }, [documentKey, layout.size, fit]);

  // A native, non-passive listener: React registers `onWheel` as passive, so `preventDefault` there
  // cannot stop Ctrl+wheel from zooming the whole window instead of the diagram.
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;
    const onWheel = (event: WheelEvent) => {
      event.preventDefault();
      const box = container.getBoundingClientRect();
      if (event.ctrlKey || event.metaKey) {
        const factor = event.deltaY < 0 ? WHEEL_ZOOM : 1 / WHEEL_ZOOM;
        setView((current) => zoomAt(current, { x: event.clientX - box.left, y: event.clientY - box.top }, factor));
        return;
      }
      // A trackpad's two-finger scroll pans, the way every canvas tool behaves.
      setView((current) => ({ ...current, x: current.x - event.deltaX, y: current.y - event.deltaY }));
    };
    container.addEventListener("wheel", onWheel, { passive: false });
    return () => container.removeEventListener("wheel", onWheel);
  }, []);

  // ---------- panning the background ----------

  const onBackgroundPointerDown = (event: PointerEvent<HTMLDivElement>) => {
    if (event.button !== 0 || event.target !== event.currentTarget) return;
    event.currentTarget.setPointerCapture(event.pointerId);
    pan.current = { pointerId: event.pointerId, start: { x: event.clientX, y: event.clientY }, origin: view };
    setDragCursor(true);
  };

  const onBackgroundPointerMove = (event: PointerEvent<HTMLDivElement>) => {
    const current = pan.current;
    if (!current || current.pointerId !== event.pointerId) return;
    setView({
      ...current.origin,
      x: current.origin.x + event.clientX - current.start.x,
      y: current.origin.y + event.clientY - current.start.y,
    });
  };

  const endPan = (event: PointerEvent<HTMLDivElement>) => {
    if (pan.current?.pointerId !== event.pointerId) return;
    pan.current = null;
    setDragCursor(false);
  };

  // ---------- dragging a card ----------

  const onCardPointerDown = (key: string, rect: Rect) => (event: PointerEvent<HTMLButtonElement>) => {
    if (event.button !== 0) return;
    event.stopPropagation();
    event.currentTarget.setPointerCapture(event.pointerId);
    cardDrag.current = {
      key,
      pointerId: event.pointerId,
      start: { x: event.clientX, y: event.clientY },
      origin: { x: rect.x, y: rect.y },
      scale: view.scale,
      moved: false,
    };
  };

  const onCardPointerMove = (event: PointerEvent<HTMLButtonElement>) => {
    const drag = cardDrag.current;
    if (!drag || drag.pointerId !== event.pointerId) return;
    const dx = event.clientX - drag.start.x;
    const dy = event.clientY - drag.start.y;
    // Below the threshold a press is still a click (focus), not a move.
    if (!drag.moved && Math.hypot(dx, dy) < DRAG_THRESHOLD) return;
    if (!drag.moved) {
      drag.moved = true;
      setDragCursor(true);
    }
    setDragPoint({ key: drag.key, point: { x: drag.origin.x + dx / drag.scale, y: drag.origin.y + dy / drag.scale } });
  };

  const onCardPointerUp = (event: PointerEvent<HTMLButtonElement>) => {
    const drag = cardDrag.current;
    if (!drag || drag.pointerId !== event.pointerId) return;
    cardDrag.current = null;
    if (!drag.moved) return;
    setDragCursor(false);
    const dx = event.clientX - drag.start.x;
    const dy = event.clientY - drag.start.y;
    setDragPoint(null);
    onPlace(drag.key, { x: drag.origin.x + dx / drag.scale, y: drag.origin.y + dy / drag.scale });
  };

  const onCardKeyDown = (key: string, rect: Rect) => (event: KeyboardEvent<HTMLButtonElement>) => {
    // The keyboard route to what a drag does: a pointer-only control cannot be reached by keyboard.
    const step = event.shiftKey ? NUDGE_FAST : NUDGE;
    const delta: Record<string, Point> = {
      ArrowLeft: { x: -step, y: 0 },
      ArrowRight: { x: step, y: 0 },
      ArrowUp: { x: 0, y: -step },
      ArrowDown: { x: 0, y: step },
    };
    const move = delta[event.key];
    if (!move) return;
    event.preventDefault();
    onPlace(key, { x: rect.x + move.x, y: rect.y + move.y });
  };

  const gridSize = GRID * view.scale;

  return (
    <div
      ref={containerRef}
      className="relative h-full w-full touch-none overflow-hidden"
      style={{
        backgroundImage: "radial-gradient(circle, var(--cf-border) 1px, transparent 1px)",
        backgroundSize: `${gridSize}px ${gridSize}px`,
        backgroundPosition: `${view.x}px ${view.y}px`,
      }}
      onPointerDown={onBackgroundPointerDown}
      onPointerMove={onBackgroundPointerMove}
      onPointerUp={endPan}
      onPointerCancel={endPan}
    >
      <div
        className="pointer-events-none absolute left-0 top-0"
        style={{ transform: `translate(${view.x}px, ${view.y}px) scale(${view.scale})`, transformOrigin: "0 0" }}
      >
        <svg className="absolute left-0 top-0 overflow-visible" width={1} height={1} aria-hidden="true">
          {segments.map((segment) => (
            <line
              key={segment.id}
              x1={segment.from.x}
              y1={segment.from.y}
              x2={segment.to.x}
              y2={segment.to.y}
              stroke="var(--cf-accent)"
              strokeOpacity={0.75}
              strokeWidth={1.5}
            />
          ))}
        </svg>

        {model.tables.map((table) => {
          const rect = drawn.get(table.key);
          if (!rect) return null;
          return (
            <div
              key={table.key}
              className="pointer-events-auto absolute overflow-hidden rounded-lg border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] shadow-sm"
              style={{ left: rect.x, top: rect.y, width: rect.width, height: rect.height }}
            >
              <button
                type="button"
                aria-label={`${t("dbml.moveTable")}: ${table.name}`}
                className="cf-focusable flex w-full cursor-grab items-center gap-1.5 border-b border-[var(--cf-border)] bg-[var(--cf-accent-soft)] px-2.5 text-left active:cursor-grabbing"
                style={{ height: HEADER_HEIGHT }}
                onPointerDown={onCardPointerDown(table.key, rect)}
                onPointerMove={onCardPointerMove}
                onPointerUp={onCardPointerUp}
                onPointerCancel={onCardPointerUp}
                onKeyDown={onCardKeyDown(table.key, rect)}
              >
                <Table2 size={14} className="shrink-0 text-[var(--cf-accent)]" />
                <span className="truncate text-ui font-semibold text-[var(--cf-text)]">{table.name}</span>
                {table.schema !== "public" && (
                  <span className="ml-auto shrink-0 text-badge text-[var(--cf-text-muted)]">{table.schema}</span>
                )}
              </button>
              <div style={{ paddingBottom: CARD_PADDING }}>
                {table.columns.map((column) => (
                  <div
                    key={column.name}
                    className="flex items-center justify-between gap-2 px-2.5 text-badge"
                    style={{ height: ROW_HEIGHT }}
                  >
                    <span className="flex min-w-0 items-center gap-1 font-mono text-[var(--cf-text)]">
                      {column.pk && <KeyRound size={12} className="shrink-0 text-[var(--cf-warning)]" />}
                      <span className="truncate">{column.name}</span>
                      {column.notNull && <span className="shrink-0 text-[var(--cf-text-muted)]">*</span>}
                    </span>
                    <span className="shrink-0 truncate font-mono text-[var(--cf-text-muted)]">{column.type}</span>
                  </div>
                ))}
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
});
