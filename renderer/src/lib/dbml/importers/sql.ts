import { Parser, ModelExporter } from "@dbml/core";
import { formatParseError } from "../parse";

/**
 * SQL DDL → DBML text (DBML-020).
 *
 * Delegated end to end: `@dbml/core` parses the dialect and writes the DBML, the same round trip
 * `exporters/sql.ts` makes in the other direction. Nothing is hand-written here, which is the point
 * — a DDL parser per dialect is exactly the code not worth owning.
 *
 * **SQLite is absent on purpose.** The parser offers `mysql`, `postgres`, `mssql`, `snowflake`,
 * `oracle` and `schemarb`; there is no SQLite grammar, and feeding SQLite DDL to another dialect
 * would half-work, which is worse than saying no. A SQLite database is read by introspection
 * instead.
 */
export const SQL_IMPORT_DIALECTS = ["postgres", "mysql", "mssql"] as const;

export type SqlImportDialect = (typeof SQL_IMPORT_DIALECTS)[number];

/**
 * Converts one SQL script.
 *
 * @throws when the script is empty, the dialect rejects it, or the conversion produces nothing.
 */
export function importSql(sql: string, dialect: SqlImportDialect): string {
  if (!sql.trim()) throw new Error("the script is empty");

  let dbml: string;
  try {
    const database = Parser.parse(sql, dialect);
    dbml = ModelExporter.export(database, "dbml");
  } catch (e) {
    // The dialect parsers raise the same `CompilerError { diags }` the DBML one does, so the reader
    // gets `message (line:column)` pointing into *their* SQL rather than `[object Object]`.
    throw new Error(formatParseError(e), { cause: e });
  }

  // A script the parser accepted but found no tables in — comments, a lone `SET`, or DDL for a
  // dialect close enough to parse and different enough to say nothing. Refusing beats replacing the
  // user's document with an empty one.
  if (!dbml.trim()) throw new Error(`no tables were found in this ${dialect} script`);

  return dbml.endsWith("\n") ? dbml : `${dbml}\n`;
}
