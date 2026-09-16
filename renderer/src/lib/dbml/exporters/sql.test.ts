import { describe, expect, it } from "vitest";
import { SQL_DIALECTS, exportSql } from "./sql";

const FIXTURE = `
Table usuarios {
  id integer [pk, increment]
  email varchar(255) [not null, unique]
}

Table animales {
  id integer [pk]
  usuario_id integer [not null]
}

Ref: animales.usuario_id > usuarios.id [delete: cascade]
`;

describe("exportSql", () => {
  it("writes PostgreSQL with its own quoting and identity columns", () => {
    const sql = exportSql(FIXTURE, "postgres");

    expect(sql).toContain('CREATE TABLE "usuarios"');
    expect(sql).toContain('"email" varchar(255) UNIQUE NOT NULL');
    expect(sql).toMatch(/ALTER TABLE "animales" ADD FOREIGN KEY \("usuario_id"\) REFERENCES "usuarios" \("id"\)/);
  });

  it("writes SQL Server with brackets, GO batches and IDENTITY", () => {
    const sql = exportSql(FIXTURE, "mssql");

    expect(sql).toContain("CREATE TABLE [usuarios]");
    expect(sql).toContain("IDENTITY(1, 1)");
    expect(sql).toContain("GO");
  });

  it("ends every export with a newline", () => {
    for (const dialect of SQL_DIALECTS) {
      expect(exportSql(FIXTURE, dialect).endsWith("\n")).toBe(true);
    }
  });

  it("reports invalid DBML with its position instead of writing a file", () => {
    expect(() => exportSql("Table {{{", "postgres")).toThrow(/\(\d+:\d+\)/);
  });

  it("refuses an empty document", () => {
    expect(() => exportSql("   \n ", "postgres")).toThrow(/empty/);
  });
});
