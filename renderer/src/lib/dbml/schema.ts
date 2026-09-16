import { parseDbmlModel } from "./parse";

/**
 * The `.dbml` preview's shape, kept for `components/editor/DbmlDiagram.tsx`.
 *
 * An adapter over `parseDbmlModel` rather than a second walk of the parser's output: there is one
 * place that reads `@dbml/core` (`parse.ts`), and this narrows its model to what the Editor's quick
 * preview draws.
 */
export interface DbmlColumn {
  name: string;
  type: string;
  pk: boolean;
  notNull: boolean;
  unique: boolean;
}

export interface DbmlTable {
  name: string;
  note: string;
  columns: DbmlColumn[];
}

export interface DbmlRef {
  fromTable: string;
  fromField: string;
  fromRelation: string;
  toTable: string;
  toField: string;
  toRelation: string;
}

export interface DbmlSchema {
  tables: DbmlTable[];
  refs: DbmlRef[];
  error: string | null;
}

export function parseDbml(source: string): DbmlSchema {
  const parsed = parseDbmlModel(source);
  if (!parsed.ok) return { tables: [], refs: [], error: parsed.error };

  return {
    tables: parsed.model.tables.map((table) => ({
      name: table.name,
      note: table.note,
      columns: table.columns.map(({ name, type, pk, notNull, unique }) => ({ name, type, pk, notNull, unique })),
    })),
    refs: parsed.model.refs.map((ref) => ({
      fromTable: ref.from.table,
      fromField: ref.from.columns[0] ?? "",
      fromRelation: ref.from.relation === "1" ? "1" : "N",
      toTable: ref.to.table,
      toField: ref.to.columns[0] ?? "",
      toRelation: ref.to.relation === "1" ? "1" : "N",
    })),
    error: null,
  };
}
