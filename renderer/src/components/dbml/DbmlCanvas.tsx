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
import { routeRelations, type RoutedRelation } from "../../lib/dbml/routing";
import { guessLanguage } from "../../lib/dbml/inflect";
import { describeRelation, renderSentence } from "../../lib/dbml/relationPhrase";
import { IDENTITY, fitBounds, zoomAt, type Viewport } from "../../lib/dbml/viewport";
import { DRAG_THRESHOLD, setDragCursor } from "../../lib/pointerDrag";
import { useLanguageStore, useT } from "../../state/languageStore";

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
/** Room around the cards for lines that run outside them. */
const SVG_MARGIN = 240;
/** How wide a line is to the pointer, as opposed to how wide it is drawn. */
const HIT_WIDTH = 14;
const TOOLTIP_OFFSET = 14;
const TOOLTIP_WIDTH = 320;
const TOOLTIP_HEIGHT_ESTIMATE = 120;

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

/** The relationship being explained, and where on screen its tooltip belongs. */
interface ActiveRelation {
  id: string;
  at: Point;
}

/**
 * The schema diagram: cards placed by `computeLayout`, orthogonal relationship lines that explain
 * themselves on hover or focus, pan and zoom, and tables a person can drag (DBML-008, DBML-013).
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
  const interfaceLanguage = useLanguageStore((s) => s.language);
  const containerRef = useRef<HTMLDivElement>(null);
  const [view, setView] = useState<Viewport>(IDENTITY);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const [dragPoint, setDragPoint] = useState<{ key: string; point: Point } | null>(null);
  const [active, setActive] = useState<ActiveRelation | null>(null);
  const cardDrag = useRef<CardDrag | null>(null);
  const pan = useRef<Pan | null>(null);

  const tablesByKey = useMemo(() => new Map(model.tables.map((table) => [table.key, table])), [model]);
  const refsById = useMemo(() => new Map(model.refs.map((r) => [r.id, r])), [model.refs]);
  const layout = useMemo(() => computeLayout(model, new Map(Object.entries(positions))), [model, positions]);

  const drawn = useMemo(() => {
    if (dragPoint === null) return layout;
    const rect = layout.get(dragPoint.key);
    if (!rect) return layout;
    return new Map(layout).set(dragPoint.key, { ...rect, ...dragPoint.point });
  }, [layout, dragPoint]);

  const routes = useMemo(() => routeRelations(model.refs, tablesByKey, drawn), [model.refs, tablesByKey, drawn]);

  const svgBox = useMemo(() => {
    const bounds = boundsOf(drawn.values());
    if (!bounds) return null;
    return {
      x: bounds.x - SVG_MARGIN,
      y: bounds.y - SVG_MARGIN,
      width: bounds.width + SVG_MARGIN * 2,
      height: bounds.height + SVG_MARGIN * 2,
    };
  }, [drawn]);

  // Nouns follow the language the schema is *named* in; the sentence around them follows the
  // interface. Column names join the table names because they carry most of the signal.
  const namesLanguage = useMemo(
    () =>
      guessLanguage(
        model.tables.flatMap((table) => [table.name, ...table.columns.map((column) => column.name)]),
        interfaceLanguage === "es" ? "es" : "en",
      ),
    [model, interfaceLanguage],
  );

  // Every line's two sentences: shown in the tooltip, and read by a screen reader from the line itself.
  const sentencesById = useMemo(
    () =>
      new Map(
        model.refs.map((r) => {
          const description = describeRelation(r, tablesByKey, namesLanguage);
          return [r.id, description ? description.sentences.map((s) => renderSentence(s, t)) : []] as const;
        }),
      ),
    [model.refs, tablesByKey, namesLanguage, t],
  );

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

  // The canvas size, for keeping the tooltip inside it.
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;
    const observer = new ResizeObserver(([entry]) => {
      if (entry) setSize({ width: entry.contentRect.width, height: entry.contentRect.height });
    });
    observer.observe(container);
    return () => observer.disconnect();
  }, []);

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
    setActive(null);
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
      setActive(null);
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

  // ---------- explaining a relationship ----------

  const showAtPointer = (id: string) => (event: PointerEvent<SVGPathElement>) => {
    // Lines sweep under the pointer while a card is dragged; explaining each one would be noise.
    if (cardDrag.current?.moved || pan.current) return;
    const box = containerRef.current?.getBoundingClientRect();
    if (!box) return;
    setActive({ id, at: { x: event.clientX - box.left, y: event.clientY - box.top } });
  };

  const showAtLabel = (route: RoutedRelation) => () => {
    // Keyboard focus has no pointer position, so the tooltip goes to the middle of the line's lane.
    setActive({
      id: route.id,
      at: { x: route.label.x * view.scale + view.x, y: route.label.y * view.scale + view.y },
    });
  };

  const hide = (id: string) => () => setActive((current) => (current?.id === id ? null : current));

  // A reference edited away while its tooltip was open simply has nothing to show.
  const activeRef = active ? refsById.get(active.id) : undefined;
  const activeSentences = active ? (sentencesById.get(active.id) ?? []) : [];
  const highlighted = activeRef ? new Set([activeRef.from.tableKey, activeRef.to.tableKey]) : null;

  const tooltipPosition = active
    ? {
        left: Math.max(8, Math.min(active.at.x + TOOLTIP_OFFSET, size.width - TOOLTIP_WIDTH - 8)),
        top:
          active.at.y + TOOLTIP_OFFSET + TOOLTIP_HEIGHT_ESTIMATE > size.height
            ? Math.max(8, active.at.y - TOOLTIP_OFFSET - TOOLTIP_HEIGHT_ESTIMATE)
            : active.at.y + TOOLTIP_OFFSET,
      }
    : null;

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
        {svgBox && (
          <svg
            className="absolute overflow-visible"
            style={{ left: svgBox.x, top: svgBox.y }}
            width={svgBox.width}
            height={svgBox.height}
          >
            <g transform={`translate(${-svgBox.x} ${-svgBox.y})`}>
              {routes.map((route) => {
                const isActive = active?.id === route.id;
                const dimmed = active !== null && !isActive;
                const r = refsById.get(route.id);
                const label = r
                  ? `${r.from.table}.${r.from.columns.join(", ")} → ${r.to.table}.${r.to.columns.join(", ")}. ${(sentencesById.get(route.id) ?? []).join(". ")}`
                  : route.id;
                return (
                  <g
                    key={route.id}
                    className="transition-opacity duration-150 motion-reduce:transition-none"
                    style={{ opacity: dimmed ? 0.25 : 1 }}
                  >
                    <path
                      d={route.d}
                      fill="none"
                      stroke="var(--cf-accent)"
                      strokeOpacity={isActive ? 1 : 0.8}
                      strokeWidth={isActive ? 2.5 : 1.5}
                    />
                    {route.markers.map((marker, i) => (
                      <path
                        key={i}
                        d={marker}
                        fill="none"
                        stroke="var(--cf-accent)"
                        strokeWidth={isActive ? 2 : 1.5}
                        strokeLinecap="round"
                      />
                    ))}
                    {/* The target the pointer and the keyboard reach: invisible, and wider than the line. */}
                    <path
                      d={route.d}
                      fill="none"
                      stroke="transparent"
                      strokeWidth={HIT_WIDTH}
                      tabIndex={0}
                      role="img"
                      aria-label={label}
                      data-relation={route.id}
                      style={{ pointerEvents: "stroke", cursor: "help", outline: "none" }}
                      onPointerEnter={showAtPointer(route.id)}
                      onPointerMove={showAtPointer(route.id)}
                      onPointerLeave={hide(route.id)}
                      onFocus={showAtLabel(route)}
                      onBlur={hide(route.id)}
                    />
                  </g>
                );
              })}
            </g>
          </svg>
        )}

        {model.tables.map((table) => {
          const rect = drawn.get(table.key);
          if (!rect) return null;
          const isRelated = highlighted?.has(table.key) ?? false;
          return (
            <div
              key={table.key}
              className={`pointer-events-auto absolute overflow-hidden rounded-lg border bg-[var(--cf-surface-raised)] shadow-sm ${
                isRelated ? "border-[var(--cf-accent)]" : "border-[var(--cf-border)]"
              }`}
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

      {activeRef && tooltipPosition && (
        <div
          role="tooltip"
          className="pointer-events-none absolute rounded-control border border-[var(--cf-border)] bg-[var(--cf-surface)] px-3 py-2 shadow-[var(--cf-shadow)]"
          style={{ ...tooltipPosition, maxWidth: TOOLTIP_WIDTH }}
        >
          <p className="mb-1 font-mono text-badge text-[var(--cf-text-muted)]">
            {activeRef.from.table}.{activeRef.from.columns.join(", ")} → {activeRef.to.table}.
            {activeRef.to.columns.join(", ")}
          </p>
          {activeSentences.map((sentence) => (
            <p key={sentence} className="text-ui text-[var(--cf-text)]">
              {sentence}
            </p>
          ))}
          {activeRef.onDelete && (
            <p className="mt-1 text-badge text-[var(--cf-text-muted)]">
              {t("dbml.relation.onDelete", { action: activeRef.onDelete })}
            </p>
          )}
          {activeRef.onUpdate && (
            <p className="text-badge text-[var(--cf-text-muted)]">
              {t("dbml.relation.onUpdate", { action: activeRef.onUpdate })}
            </p>
          )}
        </div>
      )}
    </div>
  );
});
