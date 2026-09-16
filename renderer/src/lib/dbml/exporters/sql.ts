import { ModelExporter, Parser } from "@dbml/core";
import { formatParseError } from "../parse";

/**
 * SQL export, delegated to `@dbml/core` (DBML-014).
 *
 * The parser already knows both dialects, so this is a boundary rather than a generator: parse the
 * document the user is editing, hand the model to the exporter, and refuse anything that comes back
 * empty.
 *
 * **`prisma` is deliberately not a dialect here.** `ModelExporter.export(db, "prisma")` does not
 * throw for an unknown target — it returns an empty string — so routing Prisma through this function
 * would write an empty file and call it a success. Prisma is emitted by `./prisma.ts` instead.
 */
export type SqlDialect = "postgres" | "mssql";

/** The dialects offered in the export dialog, in the order they are listed. */
export const SQL_DIALECTS: readonly SqlDialect[] = ["postgres", "mssql"];

export function exportSql(source: string, dialect: SqlDialect): string {
  if (!source.trim()) throw new Error("the document is empty");

  let sql: string;
  try {
    const database = Parser.parse(source, "dbml");
    sql = ModelExporter.export(database, dialect);
  } catch (e) {
    // The same positioned message the canvas shows, rather than `[object Object]` — with the
    // parser's own error kept as the cause, since that is where the diagnostics live.
    throw new Error(formatParseError(e), { cause: e });
  }

  // A silent empty file is the failure mode this guard exists for: the exporter answers with one
  // instead of throwing when it has nothing to say.
  if (!sql.trim()) throw new Error(`the ${dialect} exporter produced nothing`);

  return sql.endsWith("\n") ? sql : `${sql}\n`;
}
