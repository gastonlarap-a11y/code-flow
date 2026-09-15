import { describe, expect, it } from "vitest";
import { CARD_WIDTH, DEFAULT_LAYOUT, boundsOf, cardHeight, computeLayout, tooClose, type Rect } from "./layout";
import type { DbmlRefModel, DbmlSchemaModel, DbmlTableModel, Relation } from "./model";

// Models are built by hand rather than parsed: the layout must not depend on the 15 MB parser, and
// a fixture written as data says exactly which relationships the assertion is about.
function table(name: string, columns = 2): DbmlTableModel {
  return {
    key: `public.${name}`,
    schema: "public",
    name,
    note: "",
    columns: Array.from({ length: columns }, (_, i) => ({
      name: `c${i}`,
      type: "int",
      pk: i === 0,
      notNull: false,
      unique: false,
      increment: false,
      defaultValue: null,
      note: "",
    })),
    indexes: [],
  };
}

/** `from.col > to.col` unless told otherwise: `from` holds the foreign key. */
function ref(from: string, to: string, fromRelation: Relation = "*", toRelation: Relation = "1"): DbmlRefModel {
  return {
    id: `${from}->${to}`,
    name: null,
    from: { tableKey: `public.${from}`, table: from, columns: ["c1"], relation: fromRelation },
    to: { tableKey: `public.${to}`, table: to, columns: ["c0"], relation: toRelation },
    onDelete: null,
    onUpdate: null,
  };
}

function model(tables: DbmlTableModel[], refs: DbmlRefModel[] = []): DbmlSchemaModel {
  return { tables, refs, enums: [] };
}

function rectOf(layout: Map<string, Rect>, name: string): Rect {
  const rect = layout.get(`public.${name}`);
  if (!rect) throw new Error(`no rect for ${name}`);
  return rect;
}

function assertNothingTooClose(layout: Map<string, Rect>, gap: number) {
  const rects = [...layout.entries()];
  for (let i = 0; i < rects.length; i++) {
    for (let j = i + 1; j < rects.length; j++) {
      const [ka, a] = rects[i]!;
      const [kb, b] = rects[j]!;
      expect(tooClose(a, b, gap), `${ka} and ${kb} are closer than ${gap}px`).toBe(false);
    }
  }
}

const SHOP = model(
  [table("comments", 4), table("users", 5), table("posts", 6), table("tags", 2), table("post_tags", 2), table("audit", 3)],
  [ref("posts", "users"), ref("comments", "posts"), ref("comments", "users"), ref("post_tags", "posts"), ref("post_tags", "tags")],
);

describe("computeLayout", () => {
  it("places nothing for an empty document", () => {
    expect(computeLayout(model([])).size).toBe(0);
  });

  it("puts a referenced table left of every table that points at it", () => {
    const layout = computeLayout(SHOP);

    expect(rectOf(layout, "users").x).toBeLessThan(rectOf(layout, "posts").x);
    expect(rectOf(layout, "posts").x).toBeLessThan(rectOf(layout, "comments").x);
    expect(rectOf(layout, "tags").x).toBeLessThan(rectOf(layout, "post_tags").x);
  });

  it("keeps every card at least a full gap from every other", () => {
    // The complaint this answers: cards so close that neither they nor their lines can be told apart.
    assertNothingTooClose(computeLayout(SHOP), DEFAULT_LAYOUT.rowGap);
  });

  it("leaves a wide lane between columns of related tables, for the lines", () => {
    const layout = computeLayout(SHOP);
    const users = rectOf(layout, "users");
    const posts = rectOf(layout, "posts");

    expect(posts.x - (users.x + users.width)).toBe(DEFAULT_LAYOUT.columnGap);
  });

  it("sends a table with no relationships below the related ones instead of between them", () => {
    const layout = computeLayout(SHOP);
    const related = ["users", "posts", "comments", "tags", "post_tags"].map((n) => rectOf(layout, n));
    const lowestRelatedBottom = Math.max(...related.map((r) => r.y + r.height));

    expect(rectOf(layout, "audit").y).toBeGreaterThanOrEqual(lowestRelatedBottom + DEFAULT_LAYOUT.rowGap);
  });

  it("draws the same picture for the same document", () => {
    expect([...computeLayout(SHOP)]).toEqual([...computeLayout(SHOP)]);
  });

  it("terminates on a cycle and still keeps the cards apart", () => {
    const cyclic = model([table("a"), table("b"), table("c")], [ref("a", "b"), ref("b", "c"), ref("c", "a")]);

    const layout = computeLayout(cyclic);

    expect(layout.size).toBe(3);
    assertNothingTooClose(layout, DEFAULT_LAYOUT.rowGap);
  });

  it("follows a one-to-many written with `<` the same way as the `>` it mirrors", () => {
    // `users.c0 < posts.c1` says the same thing as `posts.c1 > users.c0`.
    const layout = computeLayout(model([table("posts"), table("users")], [ref("users", "posts", "1", "*")]));

    expect(rectOf(layout, "users").x).toBeLessThan(rectOf(layout, "posts").x);
  });

  it("sizes each card from its column count", () => {
    const layout = computeLayout(model([table("narrow", 1), table("tall", 12)]));

    expect(rectOf(layout, "narrow").height).toBe(cardHeight(table("narrow", 1)));
    expect(rectOf(layout, "tall").height).toBeGreaterThan(rectOf(layout, "narrow").height);
    expect(rectOf(layout, "tall").width).toBe(CARD_WIDTH);
  });
});

describe("computeLayout with pinned tables", () => {
  it("leaves a pinned table exactly where it was put", () => {
    const pinned = new Map([["public.users", { x: 900, y: 40 }]]);

    const users = rectOf(computeLayout(SHOP, pinned), "users");

    expect({ x: users.x, y: users.y }).toEqual({ x: 900, y: 40 });
  });

  it("moves a free table out from under a pinned one instead of overlapping it", () => {
    // A table added to the document must land in free space, not beneath one the user arranged.
    const unpinned = computeLayout(SHOP);
    const posts = rectOf(unpinned, "posts");
    const pinned = new Map([["public.users", { x: posts.x, y: posts.y }]]);

    const layout = computeLayout(SHOP, pinned);

    assertNothingTooClose(layout, DEFAULT_LAYOUT.rowGap);
  });

  it("ignores a pin for a table the document no longer declares", () => {
    // Positions outlive their tables on purpose (DBML-005); the layout simply has nothing to place.
    const pinned = new Map([["public.renamed_away", { x: 5, y: 5 }]]);

    const layout = computeLayout(SHOP, pinned);

    expect(layout.has("public.renamed_away")).toBe(false);
    expect(layout.size).toBe(SHOP.tables.length);
  });
});

describe("boundsOf", () => {
  it("encloses every card", () => {
    const bounds = boundsOf([
      { x: 10, y: 20, width: 100, height: 50 },
      { x: 300, y: -40, width: 50, height: 30 },
    ]);

    expect(bounds).toEqual({ x: 10, y: -40, width: 340, height: 110 });
  });

  it("is null for nothing", () => {
    expect(boundsOf([])).toBeNull();
  });
});
