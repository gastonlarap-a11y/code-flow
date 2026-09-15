import { describe, expect, it } from "vitest";
import { columnAnchorY, relationSegments } from "./edges";
import { CARD_WIDTH, HEADER_HEIGHT, ROW_HEIGHT, type Rect } from "./layout";
import type { DbmlRefModel, DbmlTableModel } from "./model";

function table(name: string, columns: string[]): DbmlTableModel {
  return {
    key: `public.${name}`,
    schema: "public",
    name,
    note: "",
    columns: columns.map((c) => ({
      name: c,
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
}

const USERS = table("users", ["id", "name"]);
const PETS = table("pets", ["id", "name", "owner_id"]);

const OWNS: DbmlRefModel = {
  id: "public.pets.owner_id->public.users.id",
  name: null,
  from: { tableKey: "public.pets", table: "pets", columns: ["owner_id"], relation: "*" },
  to: { tableKey: "public.users", table: "users", columns: ["id"], relation: "1" },
  onDelete: null,
  onUpdate: null,
};

const TABLES = new Map([
  [USERS.key, USERS],
  [PETS.key, PETS],
]);

function rect(x: number, y: number): Rect {
  return { x, y, width: CARD_WIDTH, height: 200 };
}

describe("columnAnchorY", () => {
  it("lands on the middle of the named column's row", () => {
    expect(columnAnchorY(rect(0, 100), PETS, "owner_id")).toBe(100 + HEADER_HEIGHT + 2 * ROW_HEIGHT + ROW_HEIGHT / 2);
  });

  it("falls back to the header for a column the card does not show", () => {
    expect(columnAnchorY(rect(0, 100), PETS, "missing")).toBe(100 + HEADER_HEIGHT / 2);
  });
});

describe("relationSegments", () => {
  it("leaves each card from the side facing the other", () => {
    const rects = new Map([
      [USERS.key, rect(0, 0)],
      [PETS.key, rect(500, 0)],
    ]);

    const [segment] = relationSegments([OWNS], TABLES, rects);

    // `pets` is on the right, so its line leaves from its left edge toward `users`' right edge.
    expect(segment?.from.x).toBe(500);
    expect(segment?.to.x).toBe(CARD_WIDTH);
    expect(segment?.from.y).toBe(columnAnchorY(rect(500, 0), PETS, "owner_id"));
    expect(segment?.to.y).toBe(columnAnchorY(rect(0, 0), USERS, "id"));
  });

  it("skips a reference to a table that is not on the canvas", () => {
    expect(relationSegments([OWNS], TABLES, new Map([[USERS.key, rect(0, 0)]]))).toEqual([]);
  });
});
