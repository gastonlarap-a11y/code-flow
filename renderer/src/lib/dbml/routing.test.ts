import { describe, expect, it } from "vitest";
import { columnAnchorY } from "./edges";
import { CARD_WIDTH, type Point, type Rect } from "./layout";
import type { DbmlRefModel, DbmlTableModel, Relation } from "./model";
import { LANE_SPACING, STUB, markerPath, roundedPath, routeRelations, type RoutedRelation } from "./routing";

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

function ref(from: string, fromColumn: string, to: string, toColumn: string, relations: [Relation, Relation] = ["*", "1"]): DbmlRefModel {
  return {
    id: `${from}.${fromColumn}->${to}.${toColumn}`,
    name: null,
    from: { tableKey: `public.${from}`, table: from, columns: [fromColumn], relation: relations[0] },
    to: { tableKey: `public.${to}`, table: to, columns: [toColumn], relation: relations[1] },
    onDelete: null,
    onUpdate: null,
  };
}

const TABLES = new Map(
  [
    table("users", ["id", "name"]),
    table("pets", ["id", "owner_id", "vet_id"]),
    table("vets", ["id"]),
    table("employees", ["id", "manager_id"]),
  ].map((t) => [t.key, t]),
);

const rect = (x: number, y: number): Rect => ({ x, y, width: CARD_WIDTH, height: 160 });

function only(routes: RoutedRelation[]): RoutedRelation {
  expect(routes).toHaveLength(1);
  return routes[0]!;
}

function assertOrthogonal(points: readonly Point[]) {
  for (let i = 1; i < points.length; i++) {
    const a = points[i - 1]!;
    const b = points[i]!;
    expect(a.x === b.x || a.y === b.y, `segment ${i} is diagonal`).toBe(true);
  }
}

describe("routeRelations", () => {
  it("leaves the right edge at the column's row and enters the left edge of a card further right", () => {
    // `users` sits right of `pets` here, so the line leaves `pets` rightward.
    const rects = new Map([
      ["public.pets", rect(0, 0)],
      ["public.users", rect(600, 200)],
    ]);

    const route = only(routeRelations([ref("pets", "owner_id", "users", "id")], TABLES, rects));

    expect(route.from.point).toEqual({ x: CARD_WIDTH, y: columnAnchorY(rect(0, 0), TABLES.get("public.pets")!, "owner_id") });
    expect(route.from.side).toBe("right");
    expect(route.to.point.x).toBe(600);
    expect(route.to.side).toBe("left");
    assertOrthogonal(route.points);
  });

  it("turns in the gap between the cards, never inside either", () => {
    const rects = new Map([
      ["public.pets", rect(0, 0)],
      ["public.users", rect(600, 200)],
    ]);

    const route = only(routeRelations([ref("pets", "owner_id", "users", "id")], TABLES, rects));
    const laneX = route.points[1]!.x;

    expect(laneX).toBeGreaterThanOrEqual(CARD_WIDTH + STUB);
    expect(laneX).toBeLessThanOrEqual(600 - STUB);
  });

  it("leaves the left edge when the other card is to the left", () => {
    const rects = new Map([
      ["public.pets", rect(600, 0)],
      ["public.users", rect(0, 200)],
    ]);

    const route = only(routeRelations([ref("pets", "owner_id", "users", "id")], TABLES, rects));

    expect(route.from.side).toBe("left");
    expect(route.from.point.x).toBe(600);
    expect(route.to.side).toBe("right");
    expect(route.to.point.x).toBe(CARD_WIDTH);
  });

  it("goes around the outside when the cards overlap horizontally, crossing neither", () => {
    // Stacked in one column: a line between them would have to pass through one of the cards.
    const rects = new Map([
      ["public.pets", rect(0, 0)],
      ["public.users", rect(40, 400)],
    ]);

    const route = only(routeRelations([ref("pets", "owner_id", "users", "id")], TABLES, rects));

    expect(route.from.side).toBe("right");
    expect(route.to.side).toBe("right");
    expect(route.points[1]!.x).toBeGreaterThanOrEqual(40 + CARD_WIDTH + STUB);
    assertOrthogonal(route.points);
  });

  it("loops a self-reference outside its own card", () => {
    const rects = new Map([["public.employees", rect(100, 100)]]);

    const route = only(routeRelations([ref("employees", "manager_id", "employees", "id")], TABLES, rects));

    expect(route.points.every((p, i) => i === 0 || i === route.points.length - 1 || p.x > 100 + CARD_WIDTH)).toBe(true);
  });

  it("gives lines that share a gap lanes of their own", () => {
    const rects = new Map([
      ["public.pets", rect(0, 0)],
      ["public.users", rect(600, 0)],
      ["public.vets", rect(600, 300)],
    ]);

    const routes = routeRelations(
      [ref("pets", "owner_id", "users", "id"), ref("pets", "vet_id", "vets", "id")],
      TABLES,
      rects,
    );
    const lanes = routes.map((r) => r.points[1]!.x);

    expect(Math.abs(lanes[0]! - lanes[1]!)).toBe(LANE_SPACING);
  });

  it("draws a straight line when the two rows line up", () => {
    const rects = new Map([
      ["public.pets", rect(0, 0)],
      // Offset so `users.id` lands on the same y as `pets.owner_id`.
      ["public.users", rect(600, 26)],
    ]);

    const route = only(routeRelations([ref("pets", "owner_id", "users", "id")], TABLES, rects));

    expect(route.points).toHaveLength(2);
  });

  it("skips a reference to a table that is not on the canvas", () => {
    expect(routeRelations([ref("pets", "owner_id", "users", "id")], TABLES, new Map([["public.pets", rect(0, 0)]]))).toEqual([]);
  });
});

describe("roundedPath", () => {
  it("starts and ends on the route's end points, with a curve at every corner", () => {
    const d = roundedPath(
      [
        { x: 0, y: 0 },
        { x: 50, y: 0 },
        { x: 50, y: 80 },
        { x: 100, y: 80 },
      ],
      8,
    );

    expect(d.startsWith("M 0 0")).toBe(true);
    expect(d.endsWith("L 100 80")).toBe(true);
    expect(d.match(/Q/g)).toHaveLength(2);
  });

  it("never rounds more than half a short segment", () => {
    const d = roundedPath(
      [
        { x: 0, y: 0 },
        { x: 50, y: 0 },
        { x: 50, y: 6 },
        { x: 100, y: 6 },
      ],
      8,
    );

    // The 6px vertical run allows a 3px radius at most: the first curve ends at y = 3.
    expect(d).toContain("Q 50 0 50 3");
  });
});

describe("markerPath", () => {
  it("draws a single bar at a one end and three prongs at a many end", () => {
    const one = markerPath({ point: { x: 100, y: 50 }, side: "right", relation: "1" });
    const many = markerPath({ point: { x: 100, y: 50 }, side: "right", relation: "*" });

    expect(one.match(/M/g)).toHaveLength(1);
    expect(many.match(/M/g)).toHaveLength(3);
  });

  it("draws outside the card, whichever side the line leaves from", () => {
    const right = markerPath({ point: { x: 100, y: 50 }, side: "right", relation: "1" });
    const left = markerPath({ point: { x: 100, y: 50 }, side: "left", relation: "1" });

    expect(right.startsWith("M 108")).toBe(true);
    expect(left.startsWith("M 92")).toBe(true);
  });
});
