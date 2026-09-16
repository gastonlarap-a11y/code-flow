import { describe, expect, it } from "vitest";
import { SQL_IMPORT_DIALECTS, importSql } from "./sql";
import { parseDbmlModel } from "../parse";

const POSTGRES = `
CREATE TABLE usuarios (
  id serial PRIMARY KEY,
  email varchar(255) NOT NULL UNIQUE
);

CREATE TABLE animales (
  id serial PRIMARY KEY,
  usuario_id integer NOT NULL REFERENCES usuarios(id) ON DELETE CASCADE
);

CREATE INDEX idx_animales_usuario ON animales (usuario_id);
`;

describe("importSql", () => {
  it("turns PostgreSQL DDL into DBML the app can parse back", () => {
    // The assertion that matters: not that the text looks right, but that it survives the app's own
    // parser. Importing a document the canvas then rejects is the failure worth a test.
    const model = parseDbmlModel(importSql(POSTGRES, "postgres"));

    expect(model.ok).toBe(true);
    if (!model.ok) return;
    expect(model.model.tables.map((table) => table.name)).toEqual(["usuarios", "animales"]);
  });

  it("keeps the keys, the constraints and the indexes", () => {
    const model = parseDbmlModel(importSql(POSTGRES, "postgres"));
    if (!model.ok) throw new Error(model.error);

    const usuarios = model.model.tables[0];
    expect(usuarios?.columns.map((column) => [column.name, column.pk, column.notNull, column.unique])).toEqual([
      ["id", true, false, false],
      ["email", false, true, true],
    ]);
    expect(model.model.tables[1]?.indexes[0]?.name).toBe("idx_animales_usuario");
  });

  it("carries the foreign key over with its referential action", () => {
    const model = parseDbmlModel(importSql(POSTGRES, "postgres"));
    if (!model.ok) throw new Error(model.error);

    expect(model.model.refs).toHaveLength(1);
    expect(model.model.refs[0]?.onDelete?.toLowerCase()).toBe("cascade");
  });

  it("writes the optional cardinality the old grammar could not read (DBML-019)", () => {
    // A nullable foreign key comes back as `<?`. This is the pairing that made the grammar swap
    // necessary, so it is pinned from the importer's side too.
    const dbml = importSql(
      `CREATE TABLE a (id serial PRIMARY KEY);
       CREATE TABLE b (a_id integer REFERENCES a(id));`,
      "postgres",
    );

    expect(dbml).toContain("?");
    expect(parseDbmlModel(dbml).ok).toBe(true);
  });

  it("ends every import with a newline", () => {
    for (const dialect of SQL_IMPORT_DIALECTS) {
      expect(importSql("CREATE TABLE a (id int);", dialect).endsWith("\n")).toBe(true);
    }
  });

  it("refuses an empty script", () => {
    expect(() => importSql("  \n ", "postgres")).toThrow(/empty/);
  });

  it("reports a syntax error with its position rather than producing nothing", () => {
    expect(() => importSql("CREATE TABLE (((", "postgres")).toThrow(/\(\d+:\d+\)/);
  });

  it("refuses a script it parsed but found no tables in", () => {
    // A dialect close enough to parse and different enough to say nothing lands here, and replacing
    // the user's document with an empty one would be the silent version of this.
    expect(() => importSql("-- just a comment\n", "postgres")).toThrow(/no tables/);
  });
});
