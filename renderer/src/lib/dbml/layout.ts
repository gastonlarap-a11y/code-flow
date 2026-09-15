import type { DbmlSchemaModel, DbmlTableModel } from "./model";

/**
 * Auto-layout for the schema canvas (DBML-007).
 *
 * Pure and DOM-free on purpose: every size is derived from the model, so node-env tests can assert
 * the invariants that matter — nothing overlaps, related tables read left to right, the same input
 * gives the same picture — without rendering a thing. Same precedent as `lib/graphLayout.ts`.
 *
 * The method is a reduced Sugiyama:
 * 1. Tables split into connected components; each is laid out on its own, and tables with no
 *    relationship at all go to a grid underneath instead of being scattered between the others.
 * 2. Within a component, a table's column is the length of its longest chain of foreign keys, so
 *    a referenced table always sits left of the tables that point at it.
 * 3. Each column is reordered by the barycenter of its neighbours, a few sweeps each way, which is
 *    what keeps relationship lines from crossing needlessly.
 * 4. Heights come from the column count, and every gap is generous — crowded cards with lines that
 *    cannot be told apart are the complaint this exists to answer.
 */

/** Card geometry, shared with the canvas so what is laid out is exactly what is drawn. */
export const CARD_WIDTH = 260;
export const HEADER_HEIGHT = 36;
export const ROW_HEIGHT = 26;
export const CARD_PADDING = 8;

export interface Point {
  x: number;
  y: number;
}

export interface Rect extends Point {
  width: number;
  height: number;
}

export interface LayoutOptions {
  /** Horizontal space between columns of related tables — where relationship lines run. */
  columnGap: number;
  /** Vertical space between stacked cards. */
  rowGap: number;
  /** Space between one group of related tables and the next. */
  componentGap: number;
}

export const DEFAULT_LAYOUT: LayoutOptions = { columnGap: 160, rowGap: 56, componentGap: 120 };

/** Sweeps of barycenter reordering. Past four the ordering rarely changes on schemas people draw. */
const ORDERING_SWEEPS = 4;

export function cardHeight(table: DbmlTableModel): number {
  return HEADER_HEIGHT + Math.max(1, table.columns.length) * ROW_HEIGHT + CARD_PADDING;
}

/** Whether two cards are closer than `gap` — touching counts as too close. */
export function tooClose(a: Rect, b: Rect, gap: number): boolean {
  return (
    a.x < b.x + b.width + gap &&
    b.x < a.x + a.width + gap &&
    a.y < b.y + b.height + gap &&
    b.y < a.y + a.height + gap
  );
}

/** The rectangle every card fits in, or `null` for an empty canvas. */
export function boundsOf(rects: Iterable<Rect>): Rect | null {
  let bounds: Rect | null = null;
  for (const r of rects) {
    if (bounds === null) {
      bounds = { ...r };
      continue;
    }
    const right = Math.max(bounds.x + bounds.width, r.x + r.width);
    const bottom = Math.max(bounds.y + bounds.height, r.y + r.height);
    bounds.x = Math.min(bounds.x, r.x);
    bounds.y = Math.min(bounds.y, r.y);
    bounds.width = right - bounds.x;
    bounds.height = bottom - bounds.y;
  }
  return bounds;
}

/** An index that the surrounding code has already guaranteed is in range. */
function nth<T>(items: readonly T[], index: number): T {
  const item = items[index];
  if (item === undefined) throw new Error(`layout: index ${index} out of range`);
  return item;
}

/**
 * Places every table of `model`.
 *
 * `pinned` holds the positions a person dragged tables to. A pinned table stays exactly where it
 * was put; the others are laid out as usual and then pushed down, in reading order, past any card
 * they would sit on top of — so a table added to the document lands in free space instead of under
 * something the user arranged.
 */
export function computeLayout(
  model: DbmlSchemaModel,
  pinned: ReadonlyMap<string, Point> = new Map(),
  options: LayoutOptions = DEFAULT_LAYOUT,
): Map<string, Rect> {
  const auto = autoLayout(model, options);
  if (pinned.size === 0) return auto;

  const result = new Map<string, Rect>();
  const placed: Rect[] = [];

  for (const table of model.tables) {
    const position = pinned.get(table.key);
    const rect = auto.get(table.key);
    if (!position || !rect) continue;
    const kept = { ...rect, x: position.x, y: position.y };
    result.set(table.key, kept);
    placed.push(kept);
  }

  const free = model.tables
    .filter((table) => !pinned.has(table.key))
    .flatMap((table) => {
      const rect = auto.get(table.key);
      return rect ? [{ key: table.key, rect }] : [];
    })
    .sort((a, b) => a.rect.y - b.rect.y || a.rect.x - b.rect.x);

  for (const { key, rect } of free) {
    const candidate = { ...rect };
    // Terminates: each hit moves the card strictly below one of finitely many placed cards.
    for (let hit = placed.find((p) => tooClose(candidate, p, options.rowGap)); hit; ) {
      candidate.y = hit.y + hit.height + options.rowGap;
      hit = placed.find((p) => tooClose(candidate, p, options.rowGap));
    }
    result.set(key, candidate);
    placed.push(candidate);
  }

  return result;
}

function autoLayout(model: DbmlSchemaModel, options: LayoutOptions): Map<string, Rect> {
  const { tables } = model;
  const indexOf = new Map(tables.map((table, i) => [table.key, i]));
  const parents: Set<number>[] = tables.map(() => new Set());
  const children: Set<number>[] = tables.map(() => new Set());

  for (const ref of model.refs) {
    const from = indexOf.get(ref.from.tableKey);
    const to = indexOf.get(ref.to.tableKey);
    if (from === undefined || to === undefined || from === to) continue;
    // The side holding the foreign key depends on the side it references. Only `<` reverses the
    // written direction; one-to-one and many-to-many keep it, so the picture follows the text.
    const [child, parent] = ref.from.relation === "1" && ref.to.relation === "*" ? [to, from] : [from, to];
    nth(parents, child).add(parent);
    nth(children, parent).add(child);
  }

  const rects = new Map<string, Rect>();
  let originY = 0;

  const { connected, isolated } = components(tables.length, parents, children);

  for (const members of connected) {
    const columns = assignColumns(members, parents);
    orderColumns(columns, parents, children);

    const heights = columns.map(
      (column) =>
        column.reduce((sum, t) => sum + cardHeight(nth(tables, t)), 0) +
        Math.max(0, column.length - 1) * options.rowGap,
    );
    const tallest = Math.max(0, ...heights);

    columns.forEach((column, c) => {
      const x = c * (CARD_WIDTH + options.columnGap);
      // Each column is centred against the tallest, which shortens the lines between them.
      let y = Math.round(originY + (tallest - nth(heights, c)) / 2);
      for (const t of column) {
        const table = nth(tables, t);
        const height = cardHeight(table);
        rects.set(table.key, { x, y, width: CARD_WIDTH, height });
        y += height + options.rowGap;
      }
    });

    originY += tallest + options.componentGap;
  }

  if (isolated.length > 0) {
    // Unrelated tables have no lines to route, so the grid can be square rather than wide.
    const perRow = Math.max(1, Math.ceil(Math.sqrt(isolated.length)));
    for (let start = 0; start < isolated.length; start += perRow) {
      const row = isolated.slice(start, start + perRow);
      const rowHeight = Math.max(...row.map((t) => cardHeight(nth(tables, t))));
      row.forEach((t, i) => {
        const table = nth(tables, t);
        rects.set(table.key, {
          x: i * (CARD_WIDTH + options.rowGap),
          y: originY,
          width: CARD_WIDTH,
          height: cardHeight(table),
        });
      });
      originY += rowHeight + options.rowGap;
    }
  }

  return rects;
}

/** Groups tables by relationship, in the order each group first appears in the document. */
function components(
  count: number,
  parents: readonly Set<number>[],
  children: readonly Set<number>[],
): { connected: number[][]; isolated: number[] } {
  const seen = new Set<number>();
  const connected: number[][] = [];
  const isolated: number[] = [];

  for (let start = 0; start < count; start++) {
    if (seen.has(start)) continue;
    const members: number[] = [];
    const stack = [start];
    seen.add(start);
    while (stack.length > 0) {
      const current = stack.pop()!;
      members.push(current);
      for (const next of [...nth(parents, current), ...nth(children, current)]) {
        if (seen.has(next)) continue;
        seen.add(next);
        stack.push(next);
      }
    }
    members.sort((a, b) => a - b);
    if (members.length === 1) isolated.push(nth(members, 0));
    else connected.push(members);
  }

  // Larger groups first: they carry the most structure and read best at the top of the canvas.
  connected.sort((a, b) => b.length - a.length || nth(a, 0) - nth(b, 0));
  return { connected, isolated };
}

/** Column = length of the longest foreign-key chain, with cycles broken where they are met. */
function assignColumns(members: readonly number[], parents: readonly Set<number>[]): number[][] {
  const depth = new Map<number, number>();
  const visiting = new Set<number>();

  const depthOf = (t: number): number => {
    const known = depth.get(t);
    if (known !== undefined) return known;
    // A table already on the current path closes a cycle; treating that edge as absent is what
    // lets `a → b → a` terminate, deterministically, because members are visited in index order.
    if (visiting.has(t)) return -1;
    visiting.add(t);
    let value = 0;
    for (const parent of [...nth(parents, t)].sort((a, b) => a - b)) {
      value = Math.max(value, depthOf(parent) + 1);
    }
    visiting.delete(t);
    depth.set(t, value);
    return value;
  };

  const columns: number[][] = [];
  for (const t of members) {
    const column = depthOf(t);
    while (columns.length <= column) columns.push([]);
    nth(columns, column).push(t);
  }
  return columns;
}

/** Barycenter sweeps: each table moves toward the average row of the tables it is connected to. */
function orderColumns(columns: number[][], parents: readonly Set<number>[], children: readonly Set<number>[]): void {
  const row = new Map<number, number>();
  const remember = () => columns.forEach((column) => column.forEach((t, i) => row.set(t, i)));
  remember();

  for (let sweep = 0; sweep < ORDERING_SWEEPS; sweep++) {
    const rightward = sweep % 2 === 0;
    const order = columns.map((_, i) => i);
    if (!rightward) order.reverse();

    for (const c of order) {
      const neighbours = rightward ? parents : children;
      const scored = nth(columns, c).map((t) => {
        const linked = [...nth(neighbours, t)].map((n) => row.get(n)).filter((r): r is number => r !== undefined);
        const score = linked.length === 0 ? (row.get(t) ?? 0) : linked.reduce((a, b) => a + b, 0) / linked.length;
        return { t, score };
      });
      scored.sort((a, b) => a.score - b.score || a.t - b.t);
      columns[c] = scored.map((s) => s.t);
      remember();
    }
  }
}
