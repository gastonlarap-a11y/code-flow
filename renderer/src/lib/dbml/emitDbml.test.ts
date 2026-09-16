import { describe, expect, it } from "vitest";
import { emitDbml } from "./emitDbml";
import { parseDbmlModel } from "./parse";
import type {
  DbmlSchemaSnapshot,
  DbmlSnapshotColumn,
  DbmlSnapshotTable,
} from "../../types/domain";

function column(name: string, type: string, extra: Partial<DbmlSnapshotColumn> = {}): DbmlSnapshotColumn {
  return {
    name,
    type,
    pk: false,
    not_null: false,
    unique: false,
    increment: false,
    default_value: null,
    ...extra,
  };
}

function table(name: string, columns: DbmlSnapshotColumn[], extra: Partial<DbmlSnapshotTable> = {}): DbmlSnapshotTable {
  return { schema: "public", name, columns, indexes: [], ...extra };
}

function snapshot(partial: Partial<DbmlSchemaSnapshot>): DbmlSchemaSnapshot {
  return { tables: [], refs: [], enums: [], ...partial };
}

/** Every case goes through the app's own parser: a document it cannot read is not an import. */
function modelOf(input: DbmlSchemaSnapshot) {
  const parsed = parseDbmlModel(emitDbml(input));
  if (!parsed.ok) throw new Error(`the emitted DBML did not parse: ${parsed.error}\n\n${emitDbml(input)}`);
  return parsed.model;
}

describe("emitDbml", () => {
  it("writes a table the app can parse back", () => {
    const model = modelOf(
      snapshot({
        tables: [
          table("usuarios", [
            column("id", "integer", { pk: true, increment: true, not_null: true }),
            column("email", "varchar(255)", { not_null: true, unique: true }),
            column("apodo", "varchar(120)"),
          ]),
        ],
      }),
    );

    expect(model.tables[0]?.columns).toMatchObject([
      { name: "id", pk: true, increment: true },
      { name: "email", unique: true, notNull: true },
      { name: "apodo", notNull: false },
    ]);
  });

  it("does not write `not null` on a primary key, which says it already", () => {
    const text = emitDbml(
      snapshot({ tables: [table("a", [column("id", "integer", { pk: true, not_null: true })])] }),
    );

    expect(text).toContain("[pk]");
    expect(text).not.toContain("not null");
  });

  it("omits the default schema and qualifies every other one", () => {
    const text = emitDbml(
      snapshot({
        tables: [
          table("usuarios", [column("id", "integer", { pk: true })]),
          table("pedidos", [column("id", "integer", { pk: true })], { schema: "ventas" }),
        ],
      }),
    );

    expect(text).toContain("Table usuarios {");
    expect(text).toContain("Table ventas.pedidos {");
  });

  it("tells an expression default apart from a literal one", () => {
    // `now()` in quotes is a column that defaults to the string "now()".
    const model = modelOf(
      snapshot({
        tables: [
          table("a", [
            column("creado", "timestamp", { default_value: "now()" }),
            column("estado", "varchar(20)", { default_value: "activo" }),
            column("cuota", "integer", { default_value: "10" }),
            column("activo", "boolean", { default_value: "TRUE" }),
          ]),
        ],
      }),
    );

    expect(model.tables[0]?.columns.map((c) => c.defaultValue)).toEqual(["`now()`", "activo", "10", "true"]);
  });

  it("drops a NULL default, which is what having none already means", () => {
    const text = emitDbml(
      snapshot({ tables: [table("a", [column("x", "integer", { default_value: "NULL" })])] }),
    );

    expect(text).not.toContain("default");
  });

  it("writes a composite primary key as a pk index", () => {
    const model = modelOf(
      snapshot({
        tables: [
          table(
            "animal_vacunas",
            [column("animal_id", "integer", { not_null: true }), column("vacuna_id", "integer", { not_null: true })],
            { indexes: [{ name: null, columns: ["animal_id", "vacuna_id"], unique: false, pk: true }] },
          ),
        ],
      }),
    );

    expect(model.tables[0]?.indexes[0]).toMatchObject({ pk: true, columns: ["animal_id", "vacuna_id"] });
  });

  it("keeps an index's name", () => {
    const model = modelOf(
      snapshot({
        tables: [
          table("a", [column("x", "integer")], {
            indexes: [{ name: "ix_a_x", columns: ["x"], unique: false, pk: false }],
          }),
        ],
      }),
    );

    expect(model.tables[0]?.indexes[0]?.name).toBe("ix_a_x");
  });

  it("writes a relationship as many-to-one with its referential actions", () => {
    // The snapshot reports a constraint, not a cardinality: claiming one-to-one from a foreign key
    // alone would be a guess the database did not make.
    const model = modelOf(
      snapshot({
        tables: [
          table("usuarios", [column("id", "integer", { pk: true })]),
          table("animales", [column("id", "integer", { pk: true }), column("usuario_id", "integer", { not_null: true })]),
        ],
        refs: [
          {
            from_schema: "public",
            from_table: "animales",
            from_columns: ["usuario_id"],
            to_schema: "public",
            to_table: "usuarios",
            to_columns: ["id"],
            on_delete: "cascade",
            on_update: null,
          },
        ],
      }),
    );

    expect(model.refs).toHaveLength(1);
    expect(model.refs[0]?.onDelete?.toLowerCase()).toBe("cascade");
    const ends = [model.refs[0]?.from, model.refs[0]?.to];
    expect(ends.find((e) => e?.table === "animales")?.relation).toBe("*");
    expect(ends.find((e) => e?.table === "usuarios")?.relation).toBe("1");
  });

  it("writes a composite foreign key with both sides paired", () => {
    const text = emitDbml(
      snapshot({
        tables: [
          table("padre", [column("x", "integer"), column("y", "integer")]),
          table("hijo", [column("a", "integer"), column("b", "integer")]),
        ],
        refs: [
          {
            from_schema: "public",
            from_table: "hijo",
            from_columns: ["a", "b"],
            to_schema: "public",
            to_table: "padre",
            to_columns: ["x", "y"],
            on_delete: null,
            on_update: null,
          },
        ],
      }),
    );

    expect(text).toContain("Ref: hijo.(a, b) > padre.(x, y)");
  });

  it("writes enums and uses them as a column type", () => {
    const model = modelOf(
      snapshot({
        enums: [{ schema: "public", name: "estado", values: ["activo", "inactivo"] }],
        tables: [table("a", [column("s", "estado")])],
      }),
    );

    expect(model.enums[0]?.values).toEqual(["activo", "inactivo"]);
    expect(model.tables[0]?.columns[0]?.type).toBe("estado");
  });

  it("quotes a name that is not a bare identifier", () => {
    const model = modelOf(
      snapshot({ tables: [table("order details", [column("unit price", "numeric(10,2)")])] }),
    );

    expect(model.tables[0]?.name).toBe("order details");
    expect(model.tables[0]?.columns[0]?.name).toBe("unit price");
  });

  it("answers with an empty document for an empty database rather than throwing", () => {
    expect(emitDbml(snapshot({}))).toBe("");
  });
});
