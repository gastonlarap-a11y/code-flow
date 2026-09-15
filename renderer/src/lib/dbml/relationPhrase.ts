import { nounFor, type Gender, type Lang } from "./inflect";
import type { DbmlRefModel, DbmlTableModel } from "./model";

/**
 * A relationship explained in words, both directions (DBML-011): "Cada usuario puede tener muchos
 * animales" / "Cada animal pertenece a un usuario".
 *
 * Pure: it decides *which* sentence applies and fills its nouns; the wording itself lives in
 * `translations.ts` under the keys below, so both languages are edited where every other string is.
 * The one piece of grammar that cannot live in a template is agreement — "muchos animales" but
 * "muchas citas" — so the quantity word is chosen by the noun's gender, as a key of its own.
 */

export type RelationKind = "oneToMany" | "oneToOne" | "manyToMany";

export type RelationSentenceKey =
  | "dbml.relation.canHaveMany"
  | "dbml.relation.belongsTo"
  | "dbml.relation.mayBelongTo"
  | "dbml.relation.hasAtMostOne"
  | "dbml.relation.relatesToMany";

export type RelationQuantityKey =
  | "dbml.relation.one.m"
  | "dbml.relation.one.f"
  | "dbml.relation.many.m"
  | "dbml.relation.many.f";

export interface RelationSentence {
  key: RelationSentenceKey;
  /** Already inflected: the subject in the singular, the object in whatever number the sentence needs. */
  subject: string;
  object: string;
  /** "un"/"una"/"muchos"/"muchas" (or "one"/"many"), agreeing with the object. */
  quantity: RelationQuantityKey;
}

export interface RelationDescription {
  kind: RelationKind;
  sentences: readonly [RelationSentence, RelationSentence];
}

const one = (gender: Gender): RelationQuantityKey => (gender === "f" ? "dbml.relation.one.f" : "dbml.relation.one.m");
const many = (gender: Gender): RelationQuantityKey => (gender === "f" ? "dbml.relation.many.f" : "dbml.relation.many.m");

/**
 * Whether the foreign key can be left empty — which is the difference between "belongs to" and
 * "may belong to". A column the table does not declare is treated as required: claiming optionality
 * the document does not state would be the worse mistake.
 */
function isOptional(table: DbmlTableModel, columns: readonly string[]): boolean {
  return columns.some((name) => {
    const column = table.columns.find((c) => c.name === name);
    return column !== undefined && !column.notNull && !column.pk;
  });
}

/** Both readings of one reference, or `null` when either table is not in the document. */
export function describeRelation(
  ref: DbmlRefModel,
  tables: ReadonlyMap<string, DbmlTableModel>,
  lang: Lang,
): RelationDescription | null {
  const fromTable = tables.get(ref.from.tableKey);
  const toTable = tables.get(ref.to.tableKey);
  if (!fromTable || !toTable) return null;

  const fromNoun = nounFor(fromTable.name, lang);
  const toNoun = nounFor(toTable.name, lang);

  if (ref.from.relation === "*" && ref.to.relation === "*") {
    return {
      kind: "manyToMany",
      sentences: [
        { key: "dbml.relation.relatesToMany", subject: fromNoun.singular, object: toNoun.plural, quantity: many(toNoun.gender) },
        { key: "dbml.relation.relatesToMany", subject: toNoun.singular, object: fromNoun.plural, quantity: many(fromNoun.gender) },
      ],
    };
  }

  if (ref.from.relation === "1" && ref.to.relation === "1") {
    // The side written first holds the foreign key: it belongs to the other, which has at most one.
    return {
      kind: "oneToOne",
      sentences: [
        {
          key: isOptional(fromTable, ref.from.columns) ? "dbml.relation.mayBelongTo" : "dbml.relation.belongsTo",
          subject: fromNoun.singular,
          object: toNoun.singular,
          quantity: one(toNoun.gender),
        },
        { key: "dbml.relation.hasAtMostOne", subject: toNoun.singular, object: fromNoun.singular, quantity: one(fromNoun.gender) },
      ],
    };
  }

  // One-to-many, whichever way round it was written (`>` or `<`).
  const manyIsFrom = ref.from.relation === "*";
  const manyTable = manyIsFrom ? fromTable : toTable;
  const manyColumns = manyIsFrom ? ref.from.columns : ref.to.columns;
  const manyNoun = manyIsFrom ? fromNoun : toNoun;
  const oneNoun = manyIsFrom ? toNoun : fromNoun;

  return {
    kind: "oneToMany",
    sentences: [
      { key: "dbml.relation.canHaveMany", subject: oneNoun.singular, object: manyNoun.plural, quantity: many(manyNoun.gender) },
      {
        key: isOptional(manyTable, manyColumns) ? "dbml.relation.mayBelongTo" : "dbml.relation.belongsTo",
        subject: manyNoun.singular,
        object: oneNoun.singular,
        quantity: one(oneNoun.gender),
      },
    ],
  };
}

/** Turns a sentence into text through the caller's translator — `useT()` in the app, a table in tests. */
export function renderSentence(
  sentence: RelationSentence,
  translate: (key: RelationSentenceKey | RelationQuantityKey, params?: Record<string, string>) => string,
): string {
  return translate(sentence.key, {
    subject: sentence.subject,
    object: sentence.object,
    quantity: translate(sentence.quantity),
  });
}
