import { HEADER_HEIGHT, ROW_HEIGHT, type Point, type Rect } from "./layout";
import type { DbmlRefModel, DbmlTableModel } from "./model";

/**
 * Where relationship lines attach (DBML-009).
 *
 * A line attaches to the row of the column it names, not to the middle of the card — which is what
 * makes it possible to read which column references which. Pure: the canvas hands in the rectangles
 * it drew, and this answers with coordinates.
 */
export interface RelationSegment {
  id: string;
  from: Point;
  to: Point;
}

/** The vertical centre of a column's row, or of the header when the column is not on the card. */
export function columnAnchorY(rect: Rect, table: DbmlTableModel, column: string | undefined): number {
  const index = column === undefined ? -1 : table.columns.findIndex((c) => c.name === column);
  return index < 0 ? rect.y + HEADER_HEIGHT / 2 : rect.y + HEADER_HEIGHT + index * ROW_HEIGHT + ROW_HEIGHT / 2;
}

/**
 * One segment per reference whose two tables are both on the canvas, leaving each card from the side
 * that faces the other.
 */
export function relationSegments(
  refs: readonly DbmlRefModel[],
  tables: ReadonlyMap<string, DbmlTableModel>,
  rects: ReadonlyMap<string, Rect>,
): RelationSegment[] {
  return refs.flatMap((ref) => {
    const fromTable = tables.get(ref.from.tableKey);
    const toTable = tables.get(ref.to.tableKey);
    const fromRect = rects.get(ref.from.tableKey);
    const toRect = rects.get(ref.to.tableKey);
    if (!fromTable || !toTable || !fromRect || !toRect) return [];

    const fromIsLeft = fromRect.x + fromRect.width / 2 <= toRect.x + toRect.width / 2;

    return [
      {
        id: ref.id,
        from: {
          x: fromIsLeft ? fromRect.x + fromRect.width : fromRect.x,
          y: columnAnchorY(fromRect, fromTable, ref.from.columns[0]),
        },
        to: {
          x: fromIsLeft ? toRect.x : toRect.x + toRect.width,
          y: columnAnchorY(toRect, toTable, ref.to.columns[0]),
        },
      },
    ];
  });
}
