import { Parser } from "@dbml/core";
import {
  emptyModel,
  type DbmlEndpointModel,
  type DbmlEnumModel,
  type DbmlRefModel,
  type DbmlTableModel,
  type ParsedDbml,
  type Relation,
} from "./model";

/**
 * The one module that imports `@dbml/core` (DBML-006).
 *
 * It is 15 MB minified, so everything that reaches this file sits behind a `lazy()` — `DbmlView` in
 * `App.tsx`, `DbmlPreview` in `EditorPane.tsx`. Importing it from anything eager undoes that.
 */

/** The schema `@dbml/core` files a table under when the document names none. */
export const DEFAULT_SCHEMA = "public";

/** The identity of a table across re-parses and in stored layouts. */
export function tableKey(schema: string | null | undefined, name: string): string {
  return `${schema || DEFAULT_SCHEMA}.${name}`.toLowerCase();
}

interface DbmlDiagnostic {
  message?: string;
  location?: { start?: { line: number; column: number } };
}

/**
 * `@dbml/core` doesn't throw plain `Error`s on invalid DBML — it throws a `CompilerError` shaped as
 * `{ diags: DbmlDiagnostic[] }`, so `String(e)` and `e.message` both fall through to
 * `[object Object]` unless that shape is unpacked explicitly.
 */
export function formatParseError(e: unknown): string {
  if (e && typeof e === "object" && "diags" in e && Array.isArray((e as { diags: unknown }).diags)) {
    // Narrowed by the `Array.isArray` check just above; the element shape is the parser's contract.
    const diags = (e as { diags: DbmlDiagnostic[] }).diags;
    return diags
      .map((d) => {
        const loc = d.location?.start ? ` (${d.location.start.line}:${d.location.start.column})` : "";
        return `${d.message ?? "Parse error"}${loc}`;
      })
      .join("\n");
  }
  if (e instanceof Error) return e.message;
  return String(e);
}

function text(value: unknown): string {
  return typeof value === "string" ? value : "";
}

function typeName(type: unknown): string {
  if (typeof type === "string") return type;
  if (type && typeof type === "object" && "type_name" in type) {
    return String((type as { type_name: unknown }).type_name);
  }
  return "?";
}

/** `{ value, type }` from the parser, rendered the way DBML writes it. */
function defaultValue(raw: unknown): string | null {
  if (raw === null || raw === undefined) return null;
  if (typeof raw === "object" && "value" in raw) {
    // `"value" in raw` is the narrowing; `type` is optional on the parser's side.
    const { value, type } = raw as { value: unknown; type?: unknown };
    if (value === null || value === undefined) return null;
    return type === "expression" ? `\`${String(value)}\`` : String(value);
  }
  return String(raw);
}

function relationOf(value: unknown): Relation {
  return value === "1" ? "1" : "*";
}

function orNull(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

/** Text DBML → the designer's model. Never throws: a failure comes back as a positioned message. */
export function parseDbmlModel(source: string): ParsedDbml {
  if (!source.trim()) return { ok: true, model: emptyModel() };

  try {
    const database = Parser.parse(source, "dbml");
    const model = emptyModel();

    for (const schema of database.schemas) {
      for (const table of schema.tables) {
        model.tables.push(toTable(schema.name, table));
      }
      for (const ref of schema.refs) {
        const converted = toRef(ref);
        if (converted) model.refs.push(converted);
      }
      for (const entry of schema.enums) {
        model.enums.push(toEnum(schema.name, entry));
      }
    }

    return { ok: true, model };
  } catch (e) {
    return { ok: false, error: formatParseError(e) };
  }
}

type ParsedDatabase = ReturnType<typeof Parser.parse>;
type ParsedSchema = ParsedDatabase["schemas"][number];
type ParsedTable = ParsedSchema["tables"][number];
type ParsedRef = ParsedSchema["refs"][number];
type ParsedEnum = ParsedSchema["enums"][number];

function toTable(schemaName: string, table: ParsedTable): DbmlTableModel {
  return {
    key: tableKey(schemaName, table.name),
    schema: schemaName || DEFAULT_SCHEMA,
    name: table.name,
    note: text(table.note),
    columns: table.fields.map((field) => ({
      name: field.name,
      type: typeName(field.type as unknown),
      pk: Boolean(field.pk),
      notNull: Boolean(field.not_null),
      unique: Boolean(field.unique),
      increment: Boolean(field.increment),
      defaultValue: defaultValue(field.dbdefault as unknown),
      note: text(field.note),
    })),
    indexes: table.indexes.map((index) => ({
      name: orNull(index.name),
      columns: index.columns.map((column) => {
        const kind: unknown = column.type;
        const value = String(column.value as unknown);
        return kind === "expression" ? `\`${value}\`` : value;
      }),
      unique: Boolean(index.unique),
      pk: Boolean(index.pk),
    })),
  };
}

function toEndpoint(endpoint: ParsedRef["endpoints"][number]): DbmlEndpointModel {
  return {
    tableKey: tableKey(endpoint.schemaName, endpoint.tableName),
    table: endpoint.tableName,
    columns: [...endpoint.fieldNames],
    relation: relationOf(endpoint.relation as unknown),
  };
}

function toRef(ref: ParsedRef): DbmlRefModel | null {
  const [rawFrom, rawTo] = ref.endpoints;
  if (!rawFrom || !rawTo) return null;

  const from = toEndpoint(rawFrom);
  const to = toEndpoint(rawTo);
  const onDelete: unknown = ref.onDelete;
  const onUpdate: unknown = ref.onUpdate;

  return {
    id: `${from.tableKey}.${from.columns.join(",")}->${to.tableKey}.${to.columns.join(",")}`,
    name: orNull(ref.name),
    from,
    to,
    onDelete: orNull(onDelete),
    onUpdate: orNull(onUpdate),
  };
}

function toEnum(schemaName: string, entry: ParsedEnum): DbmlEnumModel {
  return {
    schema: schemaName || DEFAULT_SCHEMA,
    name: entry.name,
    values: entry.values.map((value) => value.name),
  };
}
