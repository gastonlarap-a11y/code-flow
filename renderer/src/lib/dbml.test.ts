// Smoke coverage for the @dbml/core boundary, added with the 8→9 major bump. What it pins:
// - `Parser.parse(source, "dbml")` still returns the schemas/tables/refs shape `parseDbml` walks.
// - Invalid DBML is reported through the `CompilerError { diags }` unpacking, never thrown and
//   never the useless `[object Object]` that `String(e)` would produce.
import { describe, expect, it } from "vitest";

import { parseDbml } from "./dbml";

const FIXTURE = `
Table users {
  id int [pk, not null]
  email varchar [unique]
  note: 'People who signed up'
}

Table posts {
  id int [pk]
  author_id int [not null]
}

Ref: posts.author_id > users.id
`;

describe("parseDbml", () => {
  it("parses tables, columns and refs from a representative document", () => {
    const schema = parseDbml(FIXTURE);

    expect(schema.error).toBeNull();
    expect(schema.tables.map((t) => t.name)).toEqual(["users", "posts"]);

    const users = schema.tables[0];
    expect(users?.note).toBe("People who signed up");
    expect(users?.columns).toEqual([
      { name: "id", type: "int", pk: true, notNull: true, unique: false },
      { name: "email", type: "varchar", pk: false, notNull: false, unique: true },
    ]);

    expect(schema.refs).toEqual([
      {
        fromTable: "posts",
        fromField: "author_id",
        fromRelation: "N",
        toTable: "users",
        toField: "id",
        toRelation: "1",
      },
    ]);
  });

  it("returns an empty schema for blank input without invoking the parser", () => {
    expect(parseDbml("   \n ")).toEqual({ tables: [], refs: [], error: null });
  });

  it("surfaces parse failures as a positioned message, not an exception", () => {
    const schema = parseDbml("Table {{{");

    expect(schema.tables).toEqual([]);
    expect(schema.error).toBeTruthy();
    expect(schema.error).not.toContain("[object Object]");
    // The diagnostic carries a line:column location, which is what the editor overlay shows.
    expect(schema.error).toMatch(/\(\d+:\d+\)/);
  });
});
