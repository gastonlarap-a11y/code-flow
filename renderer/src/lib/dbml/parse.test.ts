import { describe, expect, it } from "vitest";
import { parseDbmlModel, tableKey } from "./parse";
import type { DbmlSchemaModel } from "./model";

const FIXTURE = `
Enum job_status {
  pending
  running
  done
}

Table public.users {
  id int [pk, increment]
  email varchar(120) [not null, unique, note: 'login']
  status job_status [default: 'pending']
  quota int [default: 10]
  note: 'People'
  indexes {
    (email) [unique, name: 'ix_users_email']
  }
}

Table posts {
  id int [pk]
  author_id int [not null]
}

Ref: posts.author_id > public.users.id [delete: cascade, update: no action]
Ref: posts.id <> users.id
`;

function modelOf(source: string): DbmlSchemaModel {
  const parsed = parseDbmlModel(source);
  if (!parsed.ok) throw new Error(`expected a model, got: ${parsed.error}`);
  return parsed.model;
}

describe("parseDbmlModel", () => {
  it("keys tables by lower-case schema and name, filing unqualified ones under public", () => {
    const model = modelOf(FIXTURE);

    expect(model.tables.map((t) => t.key)).toEqual(["public.users", "public.posts"]);
    expect(model.tables[1]?.schema).toBe("public");
  });

  it("keeps what the old preview shape dropped: increment, defaults, notes and indexes", () => {
    const users = modelOf(FIXTURE).tables[0];

    expect(users?.note).toBe("People");
    expect(users?.columns).toEqual([
      { name: "id", type: "int", pk: true, notNull: false, unique: false, increment: true, defaultValue: null, note: "" },
      {
        name: "email",
        type: "varchar(120)",
        pk: false,
        notNull: true,
        unique: true,
        increment: false,
        defaultValue: null,
        note: "login",
      },
      {
        name: "status",
        type: "job_status",
        pk: false,
        notNull: false,
        unique: false,
        increment: false,
        defaultValue: "pending",
        note: "",
      },
      { name: "quota", type: "int", pk: false, notNull: false, unique: false, increment: false, defaultValue: "10", note: "" },
    ]);
    expect(users?.indexes).toEqual([{ name: "ix_users_email", columns: ["email"], unique: true, pk: false }]);
  });

  it("reads each end of a reference with its cardinality and the referential actions", () => {
    const [manyToOne, manyToMany] = modelOf(FIXTURE).refs;

    expect(manyToOne).toEqual({
      id: "public.posts.author_id->public.users.id",
      name: null,
      from: { tableKey: "public.posts", table: "posts", columns: ["author_id"], relation: "*" },
      to: { tableKey: "public.users", table: "users", columns: ["id"], relation: "1" },
      onDelete: "cascade",
      onUpdate: "no action",
    });
    // `<>` puts the many side on both ends.
    expect(manyToMany?.from.relation).toBe("*");
    expect(manyToMany?.to.relation).toBe("*");
  });

  it("gives every reference to the same table the same key, qualified or not", () => {
    // `users` and `public.users` are one table; a stored position must not care how a ref spelled it.
    const [first, second] = modelOf(FIXTURE).refs;

    expect(first?.to.tableKey).toBe(second?.to.tableKey);
  });

  it("reads enums with their values", () => {
    expect(modelOf(FIXTURE).enums).toEqual([
      { schema: "public", name: "job_status", values: ["pending", "running", "done"] },
    ]);
  });

  it("answers an empty model for blank input without invoking the parser", () => {
    expect(parseDbmlModel("  \n ")).toEqual({ ok: true, model: { tables: [], refs: [], enums: [] } });
  });

  it("reports invalid DBML as a positioned message instead of throwing", () => {
    const parsed = parseDbmlModel("Table {{{");

    expect(parsed.ok).toBe(false);
    if (parsed.ok) return;
    expect(parsed.error).not.toContain("[object Object]");
    expect(parsed.error).toMatch(/\(\d+:\d+\)/);
  });
});

describe("tableKey", () => {
  it("lower-cases and defaults the schema", () => {
    expect(tableKey(null, "Users")).toBe("public.users");
    expect(tableKey("Sales", "Orders")).toBe("sales.orders");
  });
});
