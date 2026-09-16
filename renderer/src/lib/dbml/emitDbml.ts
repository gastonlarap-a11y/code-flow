import type {
  DbmlSchemaSnapshot,
  DbmlSnapshotColumn,
  DbmlSnapshotRef,
  DbmlSnapshotTable,
} from "../../types/domain";

/**
 * A database's schema, written as a DBML document (DBML-025).
 *
 * The sidecar reports what it found as data and this turns it into text, so **DBML emission lives in
 * one place** instead of once per engine in C# — where nothing could test it without a server, and
 * where four copies would drift. It is pure, so a node test calls it directly.
 */
export function emitDbml(snapshot: DbmlSchemaSnapshot): string {
  const parts: string[] = [];

  for (const entry of snapshot.enums) {
    parts.push(`Enum ${qualified(entry.schema, entry.name)} {\n${entry.values.map((v) => `  ${name(v)}`).join("\n")}\n}`);
  }

  for (const table of snapshot.tables) {
    parts.push(renderTable(table));
  }

  for (const relation of snapshot.refs) {
    parts.push(renderRef(relation));
  }

  return parts.length > 0 ? `${parts.join("\n\n")}\n` : "";
}

/** The schema `@dbml/core` files an unqualified table under, and therefore the one not to write. */
const DEFAULT_SCHEMA = "public";

/**
 * A DBML identifier: bare when it can be, double-quoted when it cannot.
 *
 * Quoting everything would be simpler and is what `@dbml/core`'s own exporter does, but a document a
 * person is about to edit reads far better without it — and this text is handed straight to the
 * editor, not to a compiler.
 */
function name(raw: string): string {
  return /^[A-Za-z_]\w*$/.test(raw) ? raw : `"${raw.replace(/"/g, '\\"')}"`;
}

function qualified(schema: string, table: string): string {
  return schema === DEFAULT_SCHEMA || schema.length === 0 ? name(table) : `${name(schema)}.${name(table)}`;
}

function renderTable(table: DbmlSnapshotTable): string {
  const columns = table.columns.map((column) => `  ${renderColumn(column)}`);
  const indexes = renderIndexes(table);

  return `Table ${qualified(table.schema, table.name)} {\n${columns.join("\n")}${indexes}\n}`;
}

function renderColumn(column: DbmlSnapshotColumn): string {
  const settings: string[] = [];
  if (column.pk) settings.push("pk");
  if (column.increment) settings.push("increment");
  if (column.unique) settings.push("unique");
  // A primary key is not null by definition, and saying both is noise on every key column.
  if (column.not_null && !column.pk) settings.push("not null");

  const fallback = renderDefault(column.default_value);
  if (fallback !== null) settings.push(`default: ${fallback}`);

  return `${name(column.name)} ${column.type}${settings.length > 0 ? ` [${settings.join(", ")}]` : ""}`;
}

/**
 * A default, told apart from an expression.
 *
 * The engines report both as text and the difference matters: DBML writes a literal in quotes and an
 * expression in backticks, and `now()` in quotes is a column that defaults to the *string* "now()".
 * Numbers and booleans are bare, which is how DBML spells them.
 */
function renderDefault(raw: string | null): string | null {
  if (raw === null || raw.trim().length === 0) return null;

  const value = raw.trim();
  if (/^-?\d+(\.\d+)?$/.test(value)) return value;
  if (/^(true|false)$/i.test(value)) return value.toLowerCase();
  if (/^null$/i.test(value)) return null;
  // A call, a cast or an operator is an expression; a bare word is a value, which is what an enum
  // member and a string default both look like by the time the engine has stripped its quoting.
  if (/[()[\]:+*/-]/.test(value)) return `\`${value}\``;

  return `'${value.replace(/'/g, "\\'")}'`;
}

function renderIndexes(table: DbmlSnapshotTable): string {
  if (table.indexes.length === 0) return "";

  const entries = table.indexes.map((index) => {
    const settings: string[] = [];
    if (index.pk) settings.push("pk");
    else if (index.unique) settings.push("unique");
    if (index.name !== null && index.name.length > 0) settings.push(`name: '${index.name}'`);

    const columns = index.columns.map(name);
    const target = columns.length === 1 ? columns[0]! : `(${columns.join(", ")})`;

    return `    ${target}${settings.length > 0 ? ` [${settings.join(", ")}]` : ""}`;
  });

  return `\n\n  Indexes {\n${entries.join("\n")}\n  }`;
}

/**
 * One relationship, written from the side that holds the foreign key.
 *
 * Always `>` — many-to-one. The snapshot reports a constraint, not a cardinality, and a foreign key
 * is many-to-one unless something also makes it unique; claiming one-to-one from the constraint
 * alone would be a guess the database did not make. A user who knows better edits the line, which is
 * the whole point of importing into a document rather than a picture.
 */
function renderRef(relation: DbmlSnapshotRef): string {
  const from = `${qualified(relation.from_schema, relation.from_table)}.${columns(relation.from_columns)}`;
  const to = `${qualified(relation.to_schema, relation.to_table)}.${columns(relation.to_columns)}`;

  const actions = [
    relation.on_delete ? `delete: ${relation.on_delete}` : null,
    relation.on_update ? `update: ${relation.on_update}` : null,
  ].filter((value): value is string => value !== null);

  return `Ref: ${from} > ${to}${actions.length > 0 ? ` [${actions.join(", ")}]` : ""}`;
}

function columns(names: string[]): string {
  return names.length === 1 ? name(names[0]!) : `(${names.map(name).join(", ")})`;
}
