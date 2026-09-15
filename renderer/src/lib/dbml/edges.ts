import { HEADER_HEIGHT, ROW_HEIGHT, type Rect } from "./layout";
import type { DbmlTableModel } from "./model";

/**
 * Where a relationship line attaches to a card (DBML-009).
 *
 * At the row of the column the reference names, not the middle of the card — which is what makes it
 * possible to read which column references which. `routing.ts` builds each line from these anchors.
 */

/** The vertical centre of a column's row, or of the header when the column is not on the card. */
export function columnAnchorY(rect: Rect, table: DbmlTableModel, column: string | undefined): number {
  const index = column === undefined ? -1 : table.columns.findIndex((c) => c.name === column);
  return index < 0 ? rect.y + HEADER_HEIGHT / 2 : rect.y + HEADER_HEIGHT + index * ROW_HEIGHT + ROW_HEIGHT / 2;
}
