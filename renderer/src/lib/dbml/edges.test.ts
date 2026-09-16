import { describe, expect, it } from "vitest";
import { columnAnchorY } from "./edges";
import { CARD_WIDTH, HEADER_HEIGHT, ROW_HEIGHT, type Rect } from "./layout";
import type { DbmlTableModel } from "./model";

const PETS: DbmlTableModel = {
  key: "public.pets",
  schema: "public",
  name: "pets",
  note: "",
  columns: ["id", "name", "owner_id"].map((name) => ({
    name,
    type: "int",
    pk: false,
    notNull: false,
    unique: false,
    increment: false,
    defaultValue: null,
    note: "",
  })),
  indexes: [],
};

const rect = (x: number, y: number): Rect => ({ x, y, width: CARD_WIDTH, height: 200 });

describe("columnAnchorY", () => {
  it("lands on the middle of the named column's row", () => {
    expect(columnAnchorY(rect(0, 100), PETS, "owner_id")).toBe(100 + HEADER_HEIGHT + 2 * ROW_HEIGHT + ROW_HEIGHT / 2);
  });

  it("falls back to the header for a column the card does not show", () => {
    expect(columnAnchorY(rect(0, 100), PETS, "missing")).toBe(100 + HEADER_HEIGHT / 2);
  });
});
