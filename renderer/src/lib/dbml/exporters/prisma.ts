import { guessLanguage, nounFor, type Lang } from "../inflect";
import type { DbmlColumnModel, DbmlSchemaModel, DbmlTableModel } from "../model";

/**
 * Prisma schema emitted by hand (DBML-015).
 *
 * `@dbml/core` has no Prisma target — and, worse, asking it for one returns an empty string rather
 * than throwing — so this is the one exporter the app writes itself. It works from the canonical
 * model, not from the parser's output, so every decision here is a pure function of data a test can
 * build.
 *
 * What it will not do is guess silently: a column type with no Prisma equivalent keeps its SQL type
 * in a `///` comment above a `String` field, so the schema still parses and the gap is visible.
 */
export type PrismaProvider = "postgresql" | "sqlserver";

export const PRISMA_PROVIDERS: readonly PrismaProvider[] = ["postgresql", "sqlserver"];

/** Prisma's referential actions, keyed by what DBML writes. */
const ACTIONS: Record<string, string> = {
  cascade: "Cascade",
  restrict: "Restrict",
  "set null": "SetNull",
  "set default": "SetDefault",
  "no action": "NoAction",
};

interface MappedType {
  /** The Prisma scalar or enum name. */
  type: string;
  /** A native-type attribute such as `@db.VarChar(255)`, when the provider needs one. */
  native: string | null;
  /** The original SQL type, when nothing matched it. */
  unmapped: string | null;
}

/** `varchar(255)` → `{ base: "varchar", args: ["255"] }`. */
function splitType(type: string): { base: string; args: string[] } {
  const match = /^([^(]+)(?:\(([^)]*)\))?$/.exec(type.trim());
  if (!match) return { base: type.trim().toLowerCase(), args: [] };
  return {
    base: (match[1] ?? "").trim().toLowerCase(),
    args: (match[2] ?? "")
      .split(",")
      .map((a) => a.trim())
      .filter((a) => a.length > 0),
  };
}

function mapType(column: DbmlColumnModel, enums: ReadonlySet<string>, provider: PrismaProvider): MappedType {
  const { base, args } = splitType(column.type);
  if (enums.has(base)) return { type: base, native: null, unmapped: null };

  const sqlServer = provider === "sqlserver";
  const length = args[0];
  const precision = args.length >= 2 ? `${args[0]}, ${args[1]}` : null;

  switch (base) {
    case "int":
    case "integer":
    case "int4":
    case "serial":
    case "smallint":
    case "int2":
    case "smallserial":
      return { type: "Int", native: null, unmapped: null };
    case "bigint":
    case "int8":
    case "bigserial":
      return { type: "BigInt", native: null, unmapped: null };
    case "boolean":
    case "bool":
    case "bit":
      return { type: "Boolean", native: null, unmapped: null };
    case "decimal":
    case "numeric":
    case "money":
      return { type: "Decimal", native: precision ? `@db.Decimal(${precision})` : null, unmapped: null };
    case "real":
    case "float":
    case "float4":
    case "float8":
    case "double":
    case "double precision":
      return { type: "Float", native: null, unmapped: null };
    case "varchar":
    case "character varying":
    case "nvarchar":
      return {
        type: "String",
        native: length ? `@db.${sqlServer ? "NVarChar" : "VarChar"}(${length})` : null,
        unmapped: null,
      };
    case "char":
    case "character":
    case "nchar":
      return { type: "String", native: length ? `@db.${sqlServer ? "NChar" : "Char"}(${length})` : null, unmapped: null };
    case "text":
    case "ntext":
      return { type: "String", native: sqlServer ? "@db.NVarChar(Max)" : "@db.Text", unmapped: null };
    case "uuid":
    case "uniqueidentifier":
      return { type: "String", native: sqlServer ? "@db.UniqueIdentifier" : "@db.Uuid", unmapped: null };
    case "json":
    case "jsonb":
      return { type: "Json", native: null, unmapped: null };
    case "date":
      return { type: "DateTime", native: "@db.Date", unmapped: null };
    case "time":
      return { type: "DateTime", native: "@db.Time", unmapped: null };
    case "timestamp":
    case "timestamptz":
    case "datetime":
    case "datetime2":
    case "smalldatetime":
      return { type: "DateTime", native: null, unmapped: null };
    case "bytea":
    case "binary":
    case "varbinary":
    case "blob":
      return { type: "Bytes", native: null, unmapped: null };
    default:
      return { type: "String", native: null, unmapped: column.type };
  }
}

/** DBML's default, as Prisma writes it. */
function mapDefault(column: DbmlColumnModel, type: string): string | null {
  if (column.increment) return "@default(autoincrement())";
  const value = column.defaultValue;
  if (value === null) return null;

  // An expression keeps its backticks in the model; Prisma needs either a known function or
  // `dbgenerated`, which is the honest answer for anything else.
  if (value.startsWith("`") && value.endsWith("`")) {
    const expression = value.slice(1, -1);
    if (/^(now|current_timestamp|getdate|sysdatetime)\(\)$/i.test(expression)) return "@default(now())";
    if (/^(gen_random_uuid|uuid_generate_v4|newid)\(\)$/i.test(expression)) return "@default(uuid())";
    return `@default(dbgenerated("${expression.replace(/"/g, '\\"')}"))`;
  }
  if (type === "Boolean") return `@default(${value.toLowerCase() === "true" || value === "1"})`;
  if (type === "Int" || type === "BigInt" || type === "Float" || type === "Decimal") {
    return Number.isNaN(Number(value)) ? null : `@default(${value})`;
  }
  return `@default("${value.replace(/"/g, '\\"')}")`;
}

/**
 * The columns that actually form the primary key.
 *
 * Two columns marked `[pk]` do not come back as two flagged fields: `@dbml/core` turns a composite
 * key into an **index** with `pk` set, and leaves every column's own `pk` false. Reading only the
 * column flag is why a join table came out with optional columns and no key at all.
 */
function primaryKeyOf(table: DbmlTableModel): string[] {
  const flagged = table.columns.filter((column) => column.pk).map((column) => column.name);
  if (flagged.length > 0) return flagged;

  const keyIndex = table.indexes.find((index) => index.pk);
  return keyIndex ? keyIndex.columns.filter((column) => !column.startsWith("`")) : [];
}

interface RelationField {
  name: string;
  type: string;
  list: boolean;
  optional: boolean;
  attribute: string | null;
}

function actionOf(raw: string | null): string | null {
  if (raw === null) return null;
  return ACTIONS[raw.trim().toLowerCase()] ?? null;
}

/** A field name no column on that model already uses. */
function freeName(preferred: string, taken: ReadonlySet<string>, fallbackSuffix: string): string {
  if (!taken.has(preferred)) return preferred;
  const suffixed = `${preferred}_${fallbackSuffix}`;
  return taken.has(suffixed) ? `${suffixed}_rel` : suffixed;
}

/**
 * The relation fields each model gains, both ways round.
 *
 * Prisma needs both ends written out, which DBML does not have: one side carries `@relation` with
 * the foreign key, the other carries the list (or the optional single field, for one-to-one).
 */
function relationFields(
  model: DbmlSchemaModel,
  tables: ReadonlyMap<string, DbmlTableModel>,
  lang: Lang,
): Map<string, RelationField[]> {
  const fields = new Map<string, RelationField[]>();
  const add = (tableKey: string, field: RelationField) => {
    const list = fields.get(tableKey);
    if (list) list.push(field);
    else fields.set(tableKey, [field]);
  };

  // Two references between the same pair of tables need names, or Prisma cannot tell them apart.
  const pairCount = new Map<string, number>();
  for (const ref of model.refs) {
    const key = [ref.from.tableKey, ref.to.tableKey].sort().join("|");
    pairCount.set(key, (pairCount.get(key) ?? 0) + 1);
  }

  for (const ref of model.refs) {
    const fromTable = tables.get(ref.from.tableKey);
    const toTable = tables.get(ref.to.tableKey);
    if (!fromTable || !toTable) continue;

    const pairKey = [ref.from.tableKey, ref.to.tableKey].sort().join("|");
    const ambiguous = (pairCount.get(pairKey) ?? 0) > 1 || ref.from.tableKey === ref.to.tableKey;
    const relationName = ambiguous ? `${fromTable.name}_${ref.from.columns.join("_")}` : null;
    const named = relationName ? `"${relationName}", ` : "";

    const manyToMany = ref.from.relation === "*" && ref.to.relation === "*";
    const oneToOne = ref.from.relation === "1" && ref.to.relation === "1";
    const fromNoun = nounFor(fromTable.name, lang);
    const toNoun = nounFor(toTable.name, lang);
    const fromTaken = new Set(fromTable.columns.map((c) => c.name));
    const toTaken = new Set(toTable.columns.map((c) => c.name));

    if (manyToMany) {
      // Prisma's implicit many-to-many: a list on each side and no foreign key anywhere.
      add(fromTable.key, { name: freeName(toNoun.plural.replace(/ /g, "_"), fromTaken, "rel"), type: toTable.name, list: true, optional: false, attribute: relationName ? `@relation("${relationName}")` : null });
      add(toTable.key, { name: freeName(fromNoun.plural.replace(/ /g, "_"), toTaken, "rel"), type: fromTable.name, list: true, optional: false, attribute: relationName ? `@relation("${relationName}")` : null });
      continue;
    }

    // Everything else has one side holding the foreign key: the `*` end, or the first end written.
    const holderIsFrom = ref.from.relation === "*" || oneToOne;
    const holder = holderIsFrom ? fromTable : toTable;
    const target = holderIsFrom ? toTable : fromTable;
    const holderColumns = holderIsFrom ? ref.from.columns : ref.to.columns;
    const targetColumns = holderIsFrom ? ref.to.columns : ref.from.columns;
    const holderTaken = holderIsFrom ? fromTaken : toTaken;
    const targetTaken = holderIsFrom ? toTaken : fromTaken;
    const holderNoun = holderIsFrom ? fromNoun : toNoun;
    const targetNoun = holderIsFrom ? toNoun : fromNoun;

    // A foreign key that is part of the primary key is required, whatever its `not null` says —
    // which is exactly the shape of a join table.
    const holderKey = new Set(primaryKeyOf(holder));
    const optional = holderColumns.some((name) => {
      const column = holder.columns.find((c) => c.name === name);
      return column !== undefined && !column.notNull && !holderKey.has(name);
    });

    const onDelete = actionOf(ref.onDelete);
    const onUpdate = actionOf(ref.onUpdate);
    const attribute = [
      `@relation(${named}fields: [${holderColumns.join(", ")}], references: [${targetColumns.join(", ")}]`,
      onDelete ? `, onDelete: ${onDelete}` : "",
      onUpdate ? `, onUpdate: ${onUpdate}` : "",
      ")",
    ].join("");

    add(holder.key, {
      name: freeName(targetNoun.singular.replace(/ /g, "_"), holderTaken, holderColumns.join("_")),
      type: target.name,
      list: false,
      optional,
      attribute,
    });
    add(target.key, {
      name: freeName(
        oneToOne ? holderNoun.singular.replace(/ /g, "_") : holderNoun.plural.replace(/ /g, "_"),
        targetTaken,
        "rel",
      ),
      type: holder.name,
      list: !oneToOne,
      optional: oneToOne,
      attribute: relationName ? `@relation("${relationName}")` : null,
    });
  }

  return fields;
}

/**
 * Pads a column of a Prisma block so the types line up, the way `prisma format` would — and always
 * leaves at least one space, since a value wider than the column would otherwise run straight into
 * the attribute after it (`animal_vacunas[]@ignore` is not valid Prisma).
 */
function pad(value: string, width: number): string {
  return value.length >= width ? `${value} ` : value.padEnd(width, " ");
}

export function toPrisma(model: DbmlSchemaModel, provider: PrismaProvider): string {
  const tables = new Map(model.tables.map((table) => [table.key, table]));
  const enums = new Set(model.enums.map((e) => e.name.toLowerCase()));
  const lang = guessLanguage(
    model.tables.flatMap((table) => [table.name, ...table.columns.map((c) => c.name)]),
    "en",
  );
  const relations = relationFields(model, tables, lang);
  const schemas = [...new Set(model.tables.map((t) => t.schema))].filter((s) => s !== "public");

  // Prisma cannot address a row it cannot identify, and refuses to generate a client for a model
  // with no unique criteria — a join table written without a key is the usual case. Its own
  // introspection keeps such a table and marks it `@@ignore`, which is better than dropping it, and
  // every relation field pointing at it has to be ignored too or the schema does not validate.
  const ignored = new Set(
    model.tables
      .filter((table) => primaryKeyOf(table).length === 0 && !table.indexes.some((index) => index.unique))
      .map((table) => table.name),
  );

  const lines: string[] = [
    "// Generated from a DBML document by CodeFlow.",
    "",
    "datasource db {",
    `  provider = "${provider}"`,
    '  url      = env("DATABASE_URL")',
  ];
  if (schemas.length > 0) {
    // Named schemas are a preview feature, and leaving them out would silently move every table.
    lines.push(`  schemas  = [${["public", ...schemas].map((s) => `"${s}"`).join(", ")}]`);
  }
  lines.push("}", "", "generator client {", '  provider = "prisma-client-js"');
  if (schemas.length > 0) lines.push('  previewFeatures = ["multiSchema"]');
  lines.push("}", "");

  for (const entry of model.enums) {
    lines.push(`enum ${entry.name} {`);
    for (const value of entry.values) lines.push(`  ${value}`);
    lines.push("}", "");
  }

  for (const table of model.tables) {
    const primaryKey = primaryKeyOf(table);
    const extra = relations.get(table.key) ?? [];
    const names = [...table.columns.map((c) => c.name), ...extra.map((r) => r.name)];
    // One past the longest name, so even that one is followed by a space and every type starts in
    // the same column.
    const width = Math.max(...names.map((n) => n.length), 1) + 1;

    if (table.note) lines.push(`/// ${table.note}`);
    lines.push(`model ${table.name} {`);

    for (const column of table.columns) {
      const mapped = mapType(column, enums, provider);
      if (mapped.unmapped) lines.push(`  /// TODO: no Prisma equivalent for the SQL type "${mapped.unmapped}".`);
      if (column.note) lines.push(`  /// ${column.note}`);

      const isPrimaryKey = primaryKey.includes(column.name);
      const optional = !column.notNull && !isPrimaryKey;
      const attributes = [
        primaryKey.length === 1 && isPrimaryKey ? "@id" : null,
        column.unique && !(primaryKey.length === 1 && isPrimaryKey) ? "@unique" : null,
        mapDefault(column, mapped.type),
        mapped.native,
      ].filter((a): a is string => a !== null);

      lines.push(
        `  ${pad(column.name, width)} ${pad(`${mapped.type}${optional ? "?" : ""}`, 12)}${attributes.join(" ")}`.trimEnd(),
      );
    }

    for (const relation of extra) {
      const type = `${relation.type}${relation.list ? "[]" : relation.optional ? "?" : ""}`;
      const attributes = [relation.attribute, ignored.has(relation.type) ? "@ignore" : null]
        .filter((a): a is string => a !== null)
        .join(" ");
      lines.push(`  ${pad(relation.name, width)} ${pad(type, 12)}${attributes}`.trimEnd());
    }

    if (primaryKey.length > 1) lines.push("", `  @@id([${primaryKey.join(", ")}])`);

    for (const index of table.indexes) {
      const columns = index.columns.filter((c) => !c.startsWith("`"));
      if (columns.length === 0) {
        lines.push(`  /// TODO: expression index ${index.name ?? ""} has no Prisma equivalent.`.trimEnd());
        continue;
      }
      // The primary key is already declared — as `@id` or `@@id` — so the index that carries it in
      // the parser's model must not come back as a second constraint.
      if (index.pk || columns.join(",") === primaryKey.join(",")) continue;
      const name = index.name ? `, name: "${index.name}"` : "";
      lines.push(`  ${index.unique ? "@@unique" : "@@index"}([${columns.join(", ")}]${name})`);
    }

    if (ignored.has(table.name)) {
      lines.push(
        "",
        "  /// TODO: this table has no primary key and no unique index, so Prisma cannot address its",
        "  /// rows. Give it one in the DBML and remove the @@ignore below.",
        "  @@ignore",
      );
    }

    if (table.schema !== "public") lines.push(`  @@schema("${table.schema}")`);

    lines.push("}", "");
  }

  return `${lines.join("\n").trimEnd()}\n`;
}
