import { describe, expect, it } from "vitest";
import { toPrisma } from "./prisma";
import { parseDbmlModel } from "../parse";
import type { DbmlSchemaModel } from "../model";

function modelOf(source: string): DbmlSchemaModel {
  const parsed = parseDbmlModel(source);
  if (!parsed.ok) throw new Error(`fixture does not parse: ${parsed.error}`);
  return parsed.model;
}

const SHOP = modelOf(`
Enum order_status {
  pending
  paid
}

Table users {
  id integer [pk, increment]
  email varchar(255) [not null, unique]
  bio text
  created_at timestamp [default: \`now()\`]
}

Table orders {
  id integer [pk, increment]
  user_id integer [not null]
  coupon_id integer
  status order_status
  total decimal(10,2) [not null]
}

Table coupons {
  id integer [pk]
  code varchar(20) [not null]
  indexes {
    (code) [unique, name: 'ix_coupons_code']
  }
}

Ref: orders.user_id > users.id [delete: cascade]
Ref: orders.coupon_id > coupons.id
`);

describe("toPrisma", () => {
  it("writes the datasource and generator for the chosen provider", () => {
    const schema = toPrisma(SHOP, "postgresql");

    expect(schema).toContain('provider = "postgresql"');
    expect(schema).toContain('url      = env("DATABASE_URL")');
    expect(schema).toContain('generator client {\n  provider = "prisma-client-js"\n}');
  });

  it("maps types, keys and defaults", () => {
    const schema = toPrisma(SHOP, "postgresql");

    expect(schema).toMatch(/id\s+Int\s+@id @default\(autoincrement\(\)\)/);
    expect(schema).toMatch(/email\s+String\s+@unique @db\.VarChar\(255\)/);
    expect(schema).toMatch(/bio\s+String\?\s+@db\.Text/);
    expect(schema).toMatch(/created_at\s+DateTime\?\s+@default\(now\(\)\)/);
    expect(schema).toMatch(/total\s+Decimal\s+@db\.Decimal\(10, 2\)/);
  });

  it("uses the provider's own native types", () => {
    const sqlServer = toPrisma(SHOP, "sqlserver");

    expect(sqlServer).toContain("@db.NVarChar(255)");
    expect(sqlServer).toContain("@db.NVarChar(Max)");
  });

  it("writes both ends of every relation, which DBML does not have", () => {
    const schema = toPrisma(SHOP, "postgresql");

    // The foreign-key side carries the attribute and the referential action.
    expect(schema).toMatch(/user\s+users\s+@relation\(fields: \[user_id\], references: \[id\], onDelete: Cascade\)/);
    // The other side gains the list Prisma requires.
    expect(schema).toMatch(/orders\s+orders\[\]/);
  });

  it("makes the relation optional when its foreign key is nullable", () => {
    const schema = toPrisma(SHOP, "postgresql");

    expect(schema).toMatch(/coupon\s+coupons\?\s+@relation\(fields: \[coupon_id\], references: \[id\]\)/);
  });

  it("declares an enum and uses it as a field type", () => {
    const schema = toPrisma(SHOP, "postgresql");

    expect(schema).toContain("enum order_status {\n  pending\n  paid\n}");
    expect(schema).toMatch(/status\s+order_status\?/);
  });

  it("carries a named unique index over as @@unique", () => {
    expect(toPrisma(SHOP, "postgresql")).toContain('@@unique([code], name: "ix_coupons_code")');
  });

  it("flags a type it cannot map instead of guessing", () => {
    const exotic = modelOf(`
Table readings {
  id integer [pk]
  position geography [not null]
}
`);

    const schema = toPrisma(exotic, "postgresql");

    expect(schema).toContain('/// TODO: no Prisma equivalent for the SQL type "geography".');
    expect(schema).toMatch(/position\s+String\s*$/m);
  });

  it("writes a composite primary key as @@id", () => {
    const composite = modelOf(`
Table post_tags {
  post_id integer [pk]
  tag_id integer [pk]
}
`);

    expect(toPrisma(composite, "postgresql")).toContain("@@id([post_id, tag_id])");
  });

  it("keeps a join table's key columns required, and their relations with them", () => {
    // The shape that exposed the composite-key bug: `@dbml/core` reports two `[pk]` columns as an
    // index rather than two flagged fields, so reading the column flag alone made both optional.
    const join = modelOf(`
Table posts {
  id integer [pk]
}

Table tags {
  id integer [pk]
}

Table post_tags {
  post_id integer [pk]
  tag_id integer [pk]
}

Ref: post_tags.post_id > posts.id
Ref: post_tags.tag_id > tags.id
`);

    const schema = toPrisma(join, "postgresql");

    expect(schema).toContain("@@id([post_id, tag_id])");
    expect(schema).toMatch(/post_id\s+Int\s/);
    expect(schema).not.toMatch(/post_id\s+Int\?/);
    expect(schema).toMatch(/post\s+posts\s+@relation\(fields: \[post_id\], references: \[id\]\)/);
    expect(schema).not.toContain("@@index([post_id, tag_id])");
  });

  it("marks a table with no unique identifier @@ignore, and the relations pointing at it", () => {
    // Prisma refuses to generate a client for a model it cannot address a row of. Emitting one
    // anyway produced a schema that looked right and failed `prisma validate`.
    const keyless = modelOf(`
Table animales {
  id integer [pk]
}

Table vacunas {
  id integer [pk]
}

Table animal_vacunas {
  animal_id integer [not null]
  vacuna_id integer [not null]
}

Ref: animal_vacunas.animal_id > animales.id
Ref: animal_vacunas.vacuna_id > vacunas.id
`);

    const schema = toPrisma(keyless, "postgresql");

    expect(schema).toContain("@@ignore");
    expect(schema).toContain("/// TODO: this table has no primary key and no unique index");
    // The back-relation on a model that keeps its key has to be ignored too, or the schema is invalid.
    expect(schema).toMatch(/animal_vacunas\s+animal_vacunas\[\]\s+@ignore/);
  });

  it("leaves a keyed table alone", () => {
    expect(toPrisma(SHOP, "postgresql")).not.toContain("@@ignore");
  });

  it("names both sides when two references join the same pair of tables", () => {
    // Without a name Prisma cannot tell the two relations apart, and the schema does not compile.
    const twice = modelOf(`
Table users {
  id integer [pk]
}

Table messages {
  id integer [pk]
  sender_id integer [not null]
  recipient_id integer [not null]
}

Ref: messages.sender_id > users.id
Ref: messages.recipient_id > users.id
`);

    const schema = toPrisma(twice, "postgresql");

    expect(schema).toContain('@relation("messages_sender_id", fields: [sender_id], references: [id])');
    expect(schema).toContain('@relation("messages_recipient_id", fields: [recipient_id], references: [id])');
  });

  it("writes a many-to-many as a list on each side, with no foreign key", () => {
    const tagged = modelOf(`
Table posts {
  id integer [pk]
}

Table tags {
  id integer [pk]
}

Ref: posts.id <> tags.id
`);

    const schema = toPrisma(tagged, "postgresql");

    expect(schema).toMatch(/tags\s+tags\[\]/);
    expect(schema).toMatch(/posts\s+posts\[\]/);
    expect(schema).not.toContain("fields: [");
  });

  it("declares named schemas rather than moving the tables to public", () => {
    const scoped = modelOf(`
Table sales.invoices {
  id integer [pk]
}
`);

    const schema = toPrisma(scoped, "postgresql");

    expect(schema).toContain('schemas  = ["public", "sales"]');
    expect(schema).toContain('previewFeatures = ["multiSchema"]');
    expect(schema).toContain('@@schema("sales")');
  });

  it("ends with exactly one newline", () => {
    const schema = toPrisma(SHOP, "postgresql");

    expect(schema.endsWith("}\n")).toBe(true);
    expect(schema.endsWith("}\n\n")).toBe(false);
  });
});
