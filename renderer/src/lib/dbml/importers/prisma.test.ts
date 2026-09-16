import { describe, expect, it } from "vitest";
import { importPrisma } from "./prisma";
import { toPrisma } from "../exporters/prisma";
import { parseDbmlModel } from "../parse";
import type { DbmlSchemaModel } from "../model";

const SCHEMA = `
// A comment, and a URL that must survive it: "http://example.com"
generator client {
  provider = "prisma-client-js"
}

datasource db {
  provider = "postgresql"
  url      = env("DATABASE_URL")
}

enum Estado {
  ACTIVO
  INACTIVO
}

model Usuario {
  id       Int       @id @default(autoincrement())
  email    String    @unique @db.VarChar(255)
  nombre   String?   @db.VarChar(120)
  estado   Estado    @default(ACTIVO)
  creado   DateTime  @default(now())
  animales Animal[]

  @@map("usuarios")
}

model Animal {
  id        Int      @id @default(autoincrement())
  usuarioId Int      @map("usuario_id")
  nombre    String   @db.VarChar(120)
  usuario   Usuario  @relation(fields: [usuarioId], references: [id], onDelete: Cascade)

  @@index([usuarioId], name: "ix_animal_usuario")
}
`;

function modelOf(source: string): DbmlSchemaModel {
  const parsed = parseDbmlModel(importPrisma(source));
  if (!parsed.ok) throw new Error(`the generated DBML did not parse: ${parsed.error}`);
  return parsed.model;
}

describe("importPrisma", () => {
  it("produces DBML the app can parse back", () => {
    // The assertion that matters. Everything below reads the parsed model rather than the text, so a
    // formatting change cannot pass a test the parser would fail.
    const model = modelOf(SCHEMA);

    expect(model.tables.map((table) => table.name)).toEqual(["usuarios", "Animal"]);
  });

  it("takes the table name from @@map, and the column name from @map", () => {
    const model = modelOf(SCHEMA);

    expect(model.tables[0]?.name).toBe("usuarios");
    expect(model.tables[1]?.columns.map((column) => column.name)).toEqual(["id", "usuario_id", "nombre"]);
  });

  it("does not turn a relation field into a column", () => {
    // `animales Animal[]` and `usuario Usuario` describe the relation, not storage.
    const model = modelOf(SCHEMA);

    expect(model.tables[0]?.columns.map((c) => c.name)).not.toContain("animales");
    expect(model.tables[1]?.columns.map((c) => c.name)).not.toContain("usuario");
  });

  it("reads @id, @unique and optionality", () => {
    const usuarios = modelOf(SCHEMA).tables[0];

    expect(usuarios?.columns[0]).toMatchObject({ name: "id", pk: true, increment: true });
    expect(usuarios?.columns[1]).toMatchObject({ name: "email", unique: true, notNull: true });
    // `nombre String?` is the one column that may be absent.
    expect(usuarios?.columns[2]).toMatchObject({ name: "nombre", notNull: false });
  });

  it("prefers the native @db type over the Prisma scalar", () => {
    const usuarios = modelOf(SCHEMA).tables[0];

    expect(usuarios?.columns[1]?.type).toBe("varchar(255)");
    expect(usuarios?.columns[4]?.type).toBe("timestamp");
  });

  it("carries enums across, and an enum default as a value", () => {
    const model = modelOf(SCHEMA);

    expect(model.enums.map((entry) => entry.name)).toEqual(["Estado"]);
    expect(model.enums[0]?.values).toEqual(["ACTIVO", "INACTIVO"]);
    expect(model.tables[0]?.columns[3]?.type).toBe("Estado");
  });

  it("writes the foreign key once, from the side that declares it, with its action", () => {
    const model = modelOf(SCHEMA);

    expect(model.refs).toHaveLength(1);
    expect(model.refs[0]?.onDelete?.toLowerCase()).toBe("cascade");
    const tables = [model.refs[0]?.from.table, model.refs[0]?.to.table];
    expect(tables).toContain("Animal");
    expect(tables).toContain("usuarios");
  });

  it("keeps @@index with the column its Prisma field maps to", () => {
    const animal = modelOf(SCHEMA).tables[1];

    expect(animal?.indexes).toHaveLength(1);
    expect(animal?.indexes[0]?.columns).toEqual(["usuario_id"]);
    expect(animal?.indexes[0]?.name).toBe("ix_animal_usuario");
  });

  it("reads a composite primary key as a pk index", () => {
    const model = modelOf(`
model AnimalVacuna {
  animalId Int @map("animal_id")
  vacunaId Int @map("vacuna_id")

  @@id([animalId, vacunaId])
  @@map("animal_vacunas")
}
`);

    const index = model.tables[0]?.indexes[0];
    expect(index?.pk).toBe(true);
    expect(index?.columns).toEqual(["animal_id", "vacuna_id"]);
  });

  it("calls a unique foreign key a one-to-one", () => {
    const model = modelOf(`
model Usuario {
  id     Int     @id
  perfil Perfil?
}

model Perfil {
  id        Int     @id
  usuarioId Int     @unique
  usuario   Usuario @relation(fields: [usuarioId], references: [id])
}
`);

    expect(model.refs).toHaveLength(1);
    expect(model.refs[0]?.from.relation).toBe("1");
    expect(model.refs[0]?.to.relation).toBe("1");
  });

  it("recognises an implicit many-to-many and writes it once", () => {
    // A list on both sides with no foreign key is Prisma's implicit join table.
    const model = modelOf(`
model Post {
  id   Int    @id
  tags Tag[]
}

model Tag {
  id    Int    @id
  posts Post[]
}
`);

    expect(model.refs).toHaveLength(1);
    expect(model.refs[0]?.from.relation).toBe("*");
    expect(model.refs[0]?.to.relation).toBe("*");
  });

  it("puts a @@schema model under that schema", () => {
    const model = modelOf(`
model Pedido {
  id Int @id

  @@schema("ventas")
}
`);

    expect(model.tables[0]?.schema).toBe("ventas");
    expect(model.tables[0]?.key).toBe("ventas.pedido");
  });

  it("maps the defaults that have a database meaning and drops the rest", () => {
    const model = modelOf(`
model Fila {
  id     String   @id @default(uuid())
  activo Boolean  @default(true)
  cuota  Int      @default(10)
  nota   String   @default("hola")
  creado DateTime @default(now())
  raro   String   @default(dbgenerated("gen_random_uuid()"))
}
`);

    // An expression keeps its backticks through the parser, so it can never pass for a literal.
    expect(model.tables[0]?.columns.map((column) => column.defaultValue)).toEqual([
      "`uuid()`",
      "true",
      "10",
      "hola",
      "`now()`",
      "`gen_random_uuid()`",
    ]);
  });

  it("does not mistake a URL inside a string for a comment", () => {
    const model = modelOf(`
model Sitio {
  id  Int    @id
  url String @default("https://example.com/a")
}
`);

    expect(model.tables[0]?.columns).toHaveLength(2);
    expect(model.tables[0]?.columns[1]?.defaultValue).toBe("https://example.com/a");
  });

  it("round-trips with the exporter without drifting", () => {
    // The claim the two files are built on: every SQL type written here is one `exporters/prisma.ts`
    // maps back, so a schema that goes out and comes in again is the schema it started as. Without
    // this, each pass would nudge the types and nobody would notice until the fourth one.
    const once = modelOf(SCHEMA);
    const twice = modelOf(toPrisma(once, "postgresql"));

    const shapeOf = (model: DbmlSchemaModel) =>
      model.tables.map((table) => ({
        name: table.name,
        columns: table.columns.map((column) => [column.name, column.type, column.pk, column.notNull]),
      }));

    expect(shapeOf(twice)).toEqual(shapeOf(once));
    expect(twice.refs).toHaveLength(once.refs.length);
  });

  it("refuses an empty schema", () => {
    expect(() => importPrisma("   \n ")).toThrow(/empty/);
  });

  it("refuses a schema with no models, rather than producing an empty document", () => {
    expect(() => importPrisma('datasource db {\n  provider = "postgresql"\n}\n')).toThrow(/no `model`/);
  });
});
