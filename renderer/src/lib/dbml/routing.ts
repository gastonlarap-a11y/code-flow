import { columnAnchorY } from "./edges";
import type { Point, Rect } from "./layout";
import type { DbmlRefModel, DbmlTableModel, Relation } from "./model";

/**
 * Orthogonal routes for relationship lines (DBML-012).
 *
 * The straight segments of `edges.ts` cross cards and each other as soon as tables are moved by hand.
 * These leave a card horizontally from the column's row, turn once into a vertical lane, and enter
 * the other card horizontally — the shape ER tools use because it can be followed by eye. Lines
 * sharing a gap get lanes of their own, and two cards that overlap horizontally are joined around
 * the outside rather than through each other. Pure, so every one of those decisions is a test.
 */

/** How far a line runs straight out of a card before it may turn. */
export const STUB = 24;
export const CORNER_RADIUS = 8;
/** Space between parallel lanes in one gap. */
export const LANE_SPACING = 12;

const MARKER_REACH = 12;
const MARKER_SPREAD = 6;
const BAR_OFFSET = 8;

export type Side = "left" | "right";

export interface RoutedEnd {
  point: Point;
  /** The card edge the line touches. */
  side: Side;
  relation: Relation;
}

export interface RoutedRelation {
  id: string;
  from: RoutedEnd;
  to: RoutedEnd;
  /** Corner points, start to end; consecutive points always share an x or a y. */
  points: Point[];
  /** An SVG path through `points` with rounded corners. */
  d: string;
  /** Where something about this line belongs — the middle of its vertical run. */
  label: Point;
  /** One SVG path per cardinality mark: a bar at a `1` end, a crow's foot at a `*` end. */
  markers: string[];
}

interface Draft {
  ref: DbmlRefModel;
  y0: number;
  y1: number;
  start: { x: number; side: Side };
  end: { x: number; side: Side };
  /** A lane between two cards, or around the outside of both. */
  corridor: { kind: "between"; lo: number; hi: number } | { kind: "around"; lo: number };
  key: string;
}

export function routeRelations(
  refs: readonly DbmlRefModel[],
  tables: ReadonlyMap<string, DbmlTableModel>,
  rects: ReadonlyMap<string, Rect>,
): RoutedRelation[] {
  const drafts = refs.flatMap((ref): Draft[] => {
    const fromTable = tables.get(ref.from.tableKey);
    const toTable = tables.get(ref.to.tableKey);
    const fromRect = rects.get(ref.from.tableKey);
    const toRect = rects.get(ref.to.tableKey);
    if (!fromTable || !toTable || !fromRect || !toRect) return [];

    const y0 = columnAnchorY(fromRect, fromTable, ref.from.columns[0]);
    const y1 = columnAnchorY(toRect, toTable, ref.to.columns[0]);
    const fromRight = fromRect.x + fromRect.width;
    const toRight = toRect.x + toRect.width;
    const selfReference = ref.from.tableKey === ref.to.tableKey;

    if (!selfReference && toRect.x - fromRight >= 2 * STUB) {
      return [{
        ref, y0, y1,
        start: { x: fromRight, side: "right" },
        end: { x: toRect.x, side: "left" },
        corridor: { kind: "between", lo: fromRight + STUB, hi: toRect.x - STUB },
        key: `between:${fromRight}:${toRect.x}`,
      }];
    }
    if (!selfReference && fromRect.x - toRight >= 2 * STUB) {
      return [{
        ref, y0, y1,
        start: { x: fromRect.x, side: "left" },
        end: { x: toRight, side: "right" },
        corridor: { kind: "between", lo: toRight + STUB, hi: fromRect.x - STUB },
        key: `between:${toRight}:${fromRect.x}`,
      }];
    }
    // Overlapping horizontally, too close to turn between, or a table referencing itself: both ends
    // leave on the right and the line runs down the outside, where it crosses neither card.
    const outer = Math.max(fromRight, toRight);
    return [{
      ref, y0, y1,
      start: { x: fromRight, side: "right" },
      end: { x: toRight, side: "right" },
      corridor: { kind: "around", lo: outer + STUB },
      key: `around:${outer}`,
    }];
  });

  const laneX = assignLanes(drafts);

  return drafts.map((draft) => {
    const x = laneX.get(draft) ?? draft.start.x;
    const points = simplify([
      { x: draft.start.x, y: draft.y0 },
      { x, y: draft.y0 },
      { x, y: draft.y1 },
      { x: draft.end.x, y: draft.y1 },
    ]);
    const from: RoutedEnd = { point: { x: draft.start.x, y: draft.y0 }, side: draft.start.side, relation: draft.ref.from.relation };
    const to: RoutedEnd = { point: { x: draft.end.x, y: draft.y1 }, side: draft.end.side, relation: draft.ref.to.relation };

    return {
      id: draft.ref.id,
      from,
      to,
      points,
      d: roundedPath(points, CORNER_RADIUS),
      label: { x, y: (draft.y0 + draft.y1) / 2 },
      markers: [markerPath(from), markerPath(to)],
    };
  });
}

/**
 * Lanes: lines sharing a gap are spread around its centre, lines running around the outside are
 * stacked outward — ordered by where they start, so neighbouring lines do not swap and cross.
 */
function assignLanes(drafts: readonly Draft[]): Map<Draft, number> {
  const groups = new Map<string, Draft[]>();
  for (const draft of drafts) {
    const group = groups.get(draft.key);
    if (group) group.push(draft);
    else groups.set(draft.key, [draft]);
  }

  const laneX = new Map<Draft, number>();
  for (const group of groups.values()) {
    group.sort(
      (a, b) => Math.min(a.y0, a.y1) - Math.min(b.y0, b.y1) || a.ref.id.localeCompare(b.ref.id),
    );
    group.forEach((draft, i) => {
      const { corridor } = draft;
      if (corridor.kind === "around") {
        laneX.set(draft, corridor.lo + i * LANE_SPACING);
        return;
      }
      const centre = (corridor.lo + corridor.hi) / 2;
      const offset = (i - (group.length - 1) / 2) * LANE_SPACING;
      laneX.set(draft, Math.min(corridor.hi, Math.max(corridor.lo, centre + offset)));
    });
  }
  return laneX;
}

/** Drops repeated points and points in the middle of a straight run. */
function simplify(points: readonly Point[]): Point[] {
  const distinct = points.filter((p, i) => i === 0 || p.x !== points[i - 1]?.x || p.y !== points[i - 1]?.y);
  return distinct.filter((p, i) => {
    const prev = distinct[i - 1];
    const next = distinct[i + 1];
    if (!prev || !next) return true;
    return !((prev.x === p.x && p.x === next.x) || (prev.y === p.y && p.y === next.y));
  });
}

function distance(a: Point, b: Point): number {
  return Math.abs(b.x - a.x) + Math.abs(b.y - a.y);
}

/** The point `r` along the orthogonal segment from `a` toward `b`. */
function toward(a: Point, b: Point, r: number): Point {
  const length = distance(a, b);
  if (length === 0) return a;
  return { x: a.x + ((b.x - a.x) / length) * r, y: a.y + ((b.y - a.y) / length) * r };
}

/** An SVG path along orthogonal `points`, each corner replaced by a quadratic curve of at most `radius`. */
export function roundedPath(points: readonly Point[], radius: number): string {
  const first = points[0];
  if (!first) return "";

  let d = `M ${first.x} ${first.y}`;
  for (let i = 1; i < points.length - 1; i++) {
    const prev = points[i - 1]!;
    const corner = points[i]!;
    const next = points[i + 1]!;
    // Never more than half a segment, or two neighbouring corners would overlap on a short run.
    const r = Math.min(radius, distance(prev, corner) / 2, distance(corner, next) / 2);
    const entry = toward(corner, prev, r);
    const exit = toward(corner, next, r);
    d += ` L ${entry.x} ${entry.y} Q ${corner.x} ${corner.y} ${exit.x} ${exit.y}`;
  }
  const last = points[points.length - 1]!;
  if (points.length > 1) d += ` L ${last.x} ${last.y}`;
  return d;
}

/** The cardinality mark at one end, drawn outside the card it touches. */
export function markerPath(end: RoutedEnd): string {
  const { x, y } = end.point;
  const outward = end.side === "right" ? 1 : -1;

  if (end.relation === "1") {
    const barX = x + outward * BAR_OFFSET;
    return `M ${barX} ${y - MARKER_SPREAD} V ${y + MARKER_SPREAD}`;
  }

  // Crow's foot: three prongs meeting a little way out, opening onto the card.
  const apex = x + outward * MARKER_REACH;
  return [
    `M ${apex} ${y} L ${x} ${y - MARKER_SPREAD}`,
    `M ${apex} ${y} L ${x} ${y}`,
    `M ${apex} ${y} L ${x} ${y + MARKER_SPREAD}`,
  ].join(" ");
}
