/**
 * The schema designer's own model of a DBML document (DBML-006).
 *
 * `@dbml/core`'s `Database` is the parser's shape — class instances, back-pointers, `any`-typed
 * fields. This is the shape everything downstream reads: the canvas, the auto-layout, and later the
 * Prisma emitter. It is plain data so that each of those is a pure function a node-env test can
 * call without the 15 MB parser.
 */

/** One end of a relationship: `1` is the referenced side, `*` the side holding the foreign key. */
export type Relation = "1" | "*";

export interface DbmlColumnModel {
  name: string;
  /** As written, arguments included — `varchar(120)`, not `varchar`. */
  type: string;
  pk: boolean;
  notNull: boolean;
  unique: boolean;
  increment: boolean;
  /** The default as DBML shows it; an expression keeps its backticks so it cannot pass for a literal. */
  defaultValue: string | null;
  note: string;
}

export interface DbmlIndexModel {
  name: string | null;
  /** Column names, or an expression in backticks. */
  columns: string[];
  unique: boolean;
  pk: boolean;
}

export interface DbmlTableModel {
  /**
   * `schema.table` in lower case: the identity that survives a re-parse, and the key positions are
   * stored under. Two tables named `orders` in different schemas are two keys.
   */
  key: string;
  schema: string;
  name: string;
  note: string;
  columns: DbmlColumnModel[];
  indexes: DbmlIndexModel[];
}

export interface DbmlEndpointModel {
  tableKey: string;
  /** The table's name as written, for display. */
  table: string;
  columns: string[];
  relation: Relation;
}

export interface DbmlRefModel {
  /** Stable across re-parses of the same text, so hover and selection survive typing. */
  id: string;
  name: string | null;
  from: DbmlEndpointModel;
  to: DbmlEndpointModel;
  onDelete: string | null;
  onUpdate: string | null;
}

export interface DbmlEnumModel {
  schema: string;
  name: string;
  values: string[];
}

export interface DbmlSchemaModel {
  tables: DbmlTableModel[];
  refs: DbmlRefModel[];
  enums: DbmlEnumModel[];
}

/** A parse either produces a model or explains, with a position, why it could not. */
export type ParsedDbml = { ok: true; model: DbmlSchemaModel } | { ok: false; error: string };

/** A fresh empty model — a function, not a shared constant, so no caller can mutate the arrays of another. */
export function emptyModel(): DbmlSchemaModel {
  return { tables: [], refs: [], enums: [] };
}
