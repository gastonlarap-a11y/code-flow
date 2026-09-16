/**
 * `schema.prisma` → DBML text (DBML-021).
 *
 * Hand-written, because `@dbml/core` has no Prisma grammar in either direction — it is the mirror of
 * `exporters/prisma.ts`, and the two are built to round-trip: every SQL type emitted here is one
 * that exporter's `mapType` maps back, so importing a schema and exporting it again returns the
 * types it started with rather than drifting a little on each pass.
 *
 * The parse is a brace scanner over blocks, which is the shape the rest of this repo uses for
 * formats it owns (there is no ANTLR anywhere). It reads what Prisma writes: `model`, `enum`, field
 * lines, and the attributes that carry schema meaning. What it deliberately ignores is everything
 * that is Prisma's own bookkeeping and has no database counterpart — `generator`, `datasource`,
 * `@updatedAt`, `@relation`'s `name:`, and the `view`/`type` blocks that describe things a DBML
 * diagram cannot show.
 */

/** A field as written, before it is known to be a column or a relation. */
interface PrismaField {
  /** The Prisma field name — what `@@id`, `@@unique` and `@relation(fields:)` refer to. */
  name: string;
  /** The base type with `?` and `[]` stripped. */
  type: string;
  optional: boolean;
  list: boolean;
  /** Everything after the type, the attributes verbatim. */
  attributes: string;
}

interface PrismaModel {
  name: string;
  /** `@@map`, when present. This is the table's real name. */
  table: string;
  /** `@@schema`, when present. */
  schema: string | null;
  fields: PrismaField[];
  /** The `@@…` lines, each without its leading `@@`. */
  blocks: string[];
}

interface PrismaEnum {
  name: string;
  values: string[];
}

/** One `Ref:` line waiting to be written, once every table name is known. */
interface PendingRef {
  fromTable: string;
  fromColumns: string[];
  toModel: string;
  toFields: string[];
  /** `>` many-to-one, `-` one-to-one. */
  operator: string;
  onDelete: string | null;
  onUpdate: string | null;
}

/**
 * Converts one `schema.prisma`.
 *
 * @throws when the file is empty or declares no models.
 */
export function importPrisma(source: string): string {
  if (!source.trim()) throw new Error("the schema is empty");

  const body = stripComments(source);
  const models: PrismaModel[] = [];
  const enums: PrismaEnum[] = [];

  for (const block of blocksOf(body)) {
    if (block.kind === "model") models.push(readModel(block.name, block.body));
    else if (block.kind === "enum") enums.push({ name: block.name, values: readEnumValues(block.body) });
  }

  if (models.length === 0) throw new Error("no `model` blocks were found in this schema");

  const modelNames = new Set(models.map((model) => model.name));
  const enumNames = new Set(enums.map((entry) => entry.name));
  const tableOf = new Map(models.map((model) => [model.name, qualified(model)]));

  const parts: string[] = [];
  for (const entry of enums) {
    parts.push(`Enum ${quoteIfNeeded(entry.name)} {\n${entry.values.map((v) => `  ${v}`).join("\n")}\n}`);
  }

  const refs: PendingRef[] = [];
  for (const model of models) {
    parts.push(renderTable(model, modelNames, enumNames));
    collectRefs(model, models, modelNames, tableOf, refs);
  }

  for (const ref of refs) {
    const from = `${ref.fromTable}.${columnList(ref.fromColumns)}`;
    const target = models.find((model) => model.name === ref.toModel);
    const toColumns = ref.toFields.map((field) => columnNameOf(target, field));
    const to = `${tableOf.get(ref.toModel) ?? ref.toModel}.${columnList(toColumns)}`;
    const actions = [
      ref.onDelete ? `delete: ${ref.onDelete}` : null,
      ref.onUpdate ? `update: ${ref.onUpdate}` : null,
    ].filter((value): value is string => value !== null);

    parts.push(`Ref: ${from} ${ref.operator} ${to}${actions.length > 0 ? ` [${actions.join(", ")}]` : ""}`);
  }

  return `${parts.join("\n\n")}\n`;
}

// ---------- lexing ----------

/**
 * Removes `//` and `///` comments, leaving string literals alone.
 *
 * A `//` inside `@default("http://x")` is not a comment, and cutting there would take the rest of a
 * valid attribute with it.
 */
function stripComments(source: string): string {
  let out = "";
  let inString = false;

  for (let i = 0; i < source.length; i += 1) {
    const char = source[i]!;
    if (inString) {
      out += char;
      if (char === "\\") {
        out += source[i + 1] ?? "";
        i += 1;
      } else if (char === '"') {
        inString = false;
      }
      continue;
    }
    if (char === '"') {
      inString = true;
      out += char;
      continue;
    }
    if (char === "/" && source[i + 1] === "/") {
      while (i < source.length && source[i] !== "\n") i += 1;
      out += "\n";
      continue;
    }
    out += char;
  }

  return out;
}

interface Block {
  kind: string;
  name: string;
  body: string;
}

/** Every top-level `<kind> <name> { … }`, found by counting braces outside strings. */
function blocksOf(source: string): Block[] {
  const blocks: Block[] = [];
  const header = /(\w+)\s+(\w+)\s*\{/g;

  let match = header.exec(source);
  while (match !== null) {
    const start = match.index + match[0].length;
    const end = matchingBrace(source, start);
    if (end < 0) break;

    blocks.push({ kind: match[1]!, name: match[2]!, body: source.slice(start, end) });
    header.lastIndex = end + 1;
    match = header.exec(source);
  }

  return blocks;
}

/** The index of the `}` closing a block that starts at `from`, or -1. */
function matchingBrace(source: string, from: number): number {
  let depth = 1;
  let inString = false;

  for (let i = from; i < source.length; i += 1) {
    const char = source[i]!;
    if (inString) {
      if (char === "\\") i += 1;
      else if (char === '"') inString = false;
      continue;
    }
    if (char === '"') inString = true;
    else if (char === "{") depth += 1;
    else if (char === "}" && --depth === 0) return i;
  }

  return -1;
}

// ---------- reading a block ----------

function readModel(name: string, body: string): PrismaModel {
  const fields: PrismaField[] = [];
  const blocks: string[] = [];

  for (const line of lines(body)) {
    if (line.startsWith("@@")) {
      blocks.push(line.slice(2));
      continue;
    }

    // `name Type? attributes…` — the type carries `?` for optional and `[]` for a list.
    const match = /^(\w+)\s+([\w.]+)(\?|\[\])?\s*(.*)$/.exec(line);
    if (match === null) continue;

    fields.push({
      name: match[1]!,
      type: match[2]!,
      optional: match[3] === "?",
      list: match[3] === "[]",
      attributes: match[4] ?? "",
    });
  }

  return {
    name,
    table: attributeArgument(blocks, "map") ?? name,
    schema: attributeArgument(blocks, "schema"),
    fields,
    blocks,
  };
}

/** An enum's values, one per line, ignoring its `@@…` attributes. */
function readEnumValues(body: string): string[] {
  return lines(body)
    .filter((line) => !line.startsWith("@@"))
    .map((line) => line.split(/\s+/)[0]!)
    .filter((value) => value.length > 0);
}

/** Non-empty, trimmed lines. */
function lines(body: string): string[] {
  return body
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

/** The single quoted argument of a `@@name("…")` block attribute. */
function attributeArgument(blocks: string[], name: string): string | null {
  const found = blocks.find((block) => block.startsWith(`${name}(`));
  return found ? (/"([^"]*)"/.exec(found)?.[1] ?? null) : null;
}

// ---------- rendering ----------

function qualified(model: PrismaModel): string {
  const table = quoteIfNeeded(model.table);
  return model.schema ? `${quoteIfNeeded(model.schema)}.${table}` : table;
}

/** DBML needs quotes around a name that is not a bare identifier. */
function quoteIfNeeded(name: string): string {
  return /^[A-Za-z_]\w*$/.test(name) ? name : `"${name}"`;
}

function columnList(columns: string[]): string {
  return columns.length === 1 ? quoteIfNeeded(columns[0]!) : `(${columns.map(quoteIfNeeded).join(", ")})`;
}

/** A Prisma field name resolved to the column it maps to. */
function columnNameOf(model: PrismaModel | undefined, fieldName: string): string {
  const field = model?.fields.find((candidate) => candidate.name === fieldName);
  return field ? columnOf(field) : fieldName;
}

/** A field's column name: `@map` when it has one, its own name otherwise. */
function columnOf(field: PrismaField): string {
  return /@map\("([^"]*)"\)/.exec(field.attributes)?.[1] ?? field.name;
}

function renderTable(model: PrismaModel, models: ReadonlySet<string>, enums: ReadonlySet<string>): string {
  const columns = model.fields.filter((field) => !models.has(field.type));
  const body = columns.map((field) => `  ${renderColumn(field, enums)}`);
  const indexes = renderIndexes(model, models);

  return `Table ${qualified(model)} {\n${body.join("\n")}${indexes}\n}`;
}

function renderColumn(field: PrismaField, enums: ReadonlySet<string>): string {
  const settings: string[] = [];
  if (/@id\b/.test(field.attributes)) settings.push("pk");
  if (callArgument(field.attributes, "default")?.trim() === "autoincrement()") settings.push("increment");
  if (/@unique\b/.test(field.attributes)) settings.push("unique");

  // Prisma says what may be absent; DBML says what may not. A list field is a scalar array, which is
  // never null in Prisma either.
  if (!field.optional) settings.push("not null");

  const fallback = mapDefault(field.attributes);
  if (fallback !== null) settings.push(`default: ${fallback}`);

  const type = enums.has(field.type) ? quoteIfNeeded(field.type) : sqlType(field);
  const suffix = settings.length > 0 ? ` [${settings.join(", ")}]` : "";

  return `${quoteIfNeeded(columnOf(field))} ${type}${field.list ? "[]" : ""}${suffix}`;
}

/**
 * The SQL type a Prisma scalar becomes.
 *
 * Every arm is chosen so `exporters/prisma.ts` maps it back to the type it came from — the native
 * `@db.…` attribute wins where there is one, because that is the column's real type and the Prisma
 * scalar is the language's view of it.
 */
function sqlType(field: PrismaField): string {
  const native = /@db\.(\w+)(?:\(([^)]*)\))?/.exec(field.attributes);
  if (native !== null) {
    const name = native[1]!;
    const args = native[2] ?? null;
    switch (name) {
      case "VarChar":
      case "NVarChar":
        // `NVarChar(Max)` is SQL Server's unbounded text, not a length.
        return args === null || args.toLowerCase() === "max" ? "text" : `varchar(${args})`;
      case "Char":
      case "NChar":
        return args ? `char(${args})` : "char";
      case "Text":
      case "NText":
        return "text";
      case "Uuid":
      case "UniqueIdentifier":
        return "uuid";
      case "Decimal":
      case "Money":
        return args ? `decimal(${args})` : "decimal";
      case "Date":
        return "date";
      case "Time":
        return "time";
      default:
        return args ? `${name.toLowerCase()}(${args})` : name.toLowerCase();
    }
  }

  switch (field.type) {
    case "Int":
      return "integer";
    case "BigInt":
      return "bigint";
    case "Boolean":
      return "boolean";
    case "Float":
      return "float";
    case "Decimal":
      return "decimal";
    case "DateTime":
      return "timestamp";
    case "Json":
      return "json";
    case "Bytes":
      return "blob";
    case "String":
      return "varchar";
    default:
      // An unrecognised scalar is carried across verbatim rather than guessed at: a wrong type in a
      // diagram is worse than an unfamiliar one, and DBML holds any word here.
      return field.type.toLowerCase();
  }
}

/**
 * The argument of `@name(…)`, with nested parentheses and quotes respected.
 *
 * Counted rather than matched by regex: `@default(dbgenerated("gen_random_uuid()"))` has two levels
 * and a string, and a `[^)]*` pattern stops at the first `)` — which silently returned
 * `dbgenerated("gen_random_uuid(` and dropped every function-shaped default on the floor.
 */
function callArgument(attributes: string, name: string): string | null {
  const start = attributes.indexOf(`@${name}(`);
  if (start < 0) return null;

  const from = start + name.length + 2;
  let depth = 1;
  let inString = false;

  for (let i = from; i < attributes.length; i += 1) {
    const char = attributes[i]!;
    if (inString) {
      if (char === "\\") i += 1;
      else if (char === '"') inString = false;
      continue;
    }
    if (char === '"') inString = true;
    else if (char === "(") depth += 1;
    else if (char === ")" && --depth === 0) return attributes.slice(from, i);
  }

  return null;
}

/** `@default(…)` as DBML writes it, or null when there is none worth carrying. */
function mapDefault(attributes: string): string | null {
  const argument = callArgument(attributes, "default");
  if (argument === null) return null;

  const value = argument.trim();
  // `autoincrement()` is already the `increment` setting, not a default value.
  if (value === "autoincrement()") return null;
  if (value === "now()") return "`now()`";
  if (value === "uuid()" || value === "cuid()") return "`uuid()`";

  const generated = /^dbgenerated\("(.*)"\)$/.exec(value);
  if (generated !== null) return `\`${generated[1]!}\``;

  if (value === "true" || value === "false") return value;
  if (/^-?\d+(\.\d+)?$/.test(value)) return value;

  const quoted = /^"(.*)"$/.exec(value);
  if (quoted !== null) return `'${quoted[1]!}'`;

  // A bare word is an enum member, and DBML writes those as strings.
  return /^\w+$/.test(value) ? `'${value}'` : null;
}

/** `@@id`, `@@unique` and `@@index` become a DBML `Indexes` block. */
function renderIndexes(model: PrismaModel, models: ReadonlySet<string>): string {
  const entries: string[] = [];

  for (const block of model.blocks) {
    const match = /^(id|unique|index)\(\s*\[([^\]]*)\]/.exec(block);
    if (match === null) continue;

    const columns = match[2]!
      .split(",")
      .map((name) => name.trim())
      .filter((name) => name.length > 0)
      // `@@index([author])` on a relation field indexes its foreign key, not the relation.
      .filter((name) => !models.has(model.fields.find((f) => f.name === name)?.type ?? ""))
      .map((name) => columnNameOf(model, name));
    if (columns.length === 0) continue;

    const settings: string[] = [];
    if (match[1] === "id") settings.push("pk");
    if (match[1] === "unique") settings.push("unique");
    const name = /name:\s*"([^"]*)"/.exec(block)?.[1] ?? /map:\s*"([^"]*)"/.exec(block)?.[1];
    if (name) settings.push(`name: '${name}'`);

    const target = columns.length === 1 ? columns[0]! : `(${columns.join(", ")})`;
    entries.push(`    ${target}${settings.length > 0 ? ` [${settings.join(", ")}]` : ""}`);
  }

  return entries.length > 0 ? `\n\n  Indexes {\n${entries.join("\n")}\n  }` : "";
}

// ---------- relations ----------

/**
 * The `Ref:` lines one model owns.
 *
 * Only the side carrying `@relation(fields:)` produces one — the other side is the same relation
 * seen from the far end, and writing both would draw every line twice. An implicit many-to-many has
 * no such side on either model, so it is recognised by its shape: a list on both ends.
 */
function collectRefs(
  model: PrismaModel,
  models: readonly PrismaModel[],
  modelNames: ReadonlySet<string>,
  tableOf: ReadonlyMap<string, string>,
  out: PendingRef[],
): void {
  const table = tableOf.get(model.name) ?? model.name;

  for (const field of model.fields) {
    if (!modelNames.has(field.type)) continue;

    const relation = /@relation\(([^)]*)\)/.exec(field.attributes)?.[1] ?? "";
    const fields = listArgument(relation, "fields");
    const references = listArgument(relation, "references");

    if (fields.length === 0 || references.length === 0) {
      // No foreign key on this side. It is either the far end of somebody else's relation, or half
      // of an implicit many-to-many — which is a list here and a list there, and belongs to whichever
      // model sorts first so it is written once.
      const other = models.find((candidate) => candidate.name === field.type);
      const back = other?.fields.find((candidate) => candidate.type === model.name);
      if (field.list && back?.list === true && model.name < field.type) {
        out.push({
          fromTable: table,
          fromColumns: [primaryKeyColumn(model)],
          toModel: field.type,
          toFields: [primaryKeyField(other)],
          operator: "<>",
          onDelete: null,
          onUpdate: null,
        });
      }
      continue;
    }

    const columns = fields.map((name) => columnNameOf(model, name));
    // A foreign key that is itself unique is one-to-one; anything else is many-to-one.
    const unique = fields.every((name) => isUnique(model, name)) || hasCompositeUnique(model, fields);

    out.push({
      fromTable: table,
      fromColumns: columns,
      toModel: field.type,
      toFields: references,
      operator: unique ? "-" : ">",
      onDelete: referentialAction(relation, "onDelete"),
      onUpdate: referentialAction(relation, "onUpdate"),
    });
  }
}

/** `fields: [a, b]` → `["a", "b"]`. */
function listArgument(relation: string, name: string): string[] {
  const match = new RegExp(`${name}:\\s*\\[([^\\]]*)\\]`).exec(relation);
  if (match === null) return [];

  return match[1]!
    .split(",")
    .map((value) => value.trim())
    .filter((value) => value.length > 0);
}

/** Prisma's action names, lower-cased the way DBML writes them. */
function referentialAction(relation: string, name: string): string | null {
  const raw = new RegExp(`${name}:\\s*(\\w+)`).exec(relation)?.[1];
  if (raw === undefined) return null;

  switch (raw) {
    case "Cascade":
      return "cascade";
    case "Restrict":
      return "restrict";
    case "SetNull":
      return "set null";
    case "SetDefault":
      return "set default";
    case "NoAction":
      return "no action";
    default:
      return null;
  }
}

function isUnique(model: PrismaModel, fieldName: string): boolean {
  const field = model.fields.find((candidate) => candidate.name === fieldName);
  return field !== undefined && (/@unique\b/.test(field.attributes) || /@id\b/.test(field.attributes));
}

/** Whether `@@unique([…])` or `@@id([…])` covers exactly these fields. */
function hasCompositeUnique(model: PrismaModel, fields: readonly string[]): boolean {
  return model.blocks.some((block) => {
    const match = /^(id|unique)\(\s*\[([^\]]*)\]/.exec(block);
    if (match === null) return false;

    const declared = match[2]!.split(",").map((name) => name.trim());
    return declared.length === fields.length && fields.every((name) => declared.includes(name));
  });
}

/** The column an implicit many-to-many joins on: the model's primary key, or `id`. */
function primaryKeyColumn(model: PrismaModel): string {
  const field = model.fields.find((candidate) => /@id\b/.test(candidate.attributes));
  return field ? columnOf(field) : "id";
}

function primaryKeyField(model: PrismaModel | undefined): string {
  return model?.fields.find((candidate) => /@id\b/.test(candidate.attributes))?.name ?? "id";
}
