import { describe, expect, it } from "vitest";
import { describeRelation, renderSentence, type RelationQuantityKey, type RelationSentenceKey } from "./relationPhrase";
import type { DbmlRefModel, DbmlTableModel, Relation } from "./model";

// The templates as `translations.ts` will hold them. Duplicated here on purpose: this test is about
// which sentence is chosen and how its nouns agree, and it must not pass just because a key is missing.
const ES: Record<RelationSentenceKey | RelationQuantityKey, string> = {
  "dbml.relation.canHaveMany": "Cada {subject} puede tener {quantity} {object}",
  "dbml.relation.belongsTo": "Cada {subject} pertenece a {quantity} {object}",
  "dbml.relation.mayBelongTo": "Cada {subject} puede pertenecer a {quantity} {object}",
  "dbml.relation.hasAtMostOne": "Cada {subject} tiene como máximo {quantity} {object}",
  "dbml.relation.relatesToMany": "Cada {subject} puede relacionarse con {quantity} {object}",
  "dbml.relation.one.m": "un",
  "dbml.relation.one.f": "una",
  "dbml.relation.many.m": "muchos",
  "dbml.relation.many.f": "muchas",
};

const EN: Record<RelationSentenceKey | RelationQuantityKey, string> = {
  "dbml.relation.canHaveMany": "Each {subject} can have {quantity} {object}",
  "dbml.relation.belongsTo": "Each {subject} belongs to {quantity} {object}",
  "dbml.relation.mayBelongTo": "Each {subject} may belong to {quantity} {object}",
  "dbml.relation.hasAtMostOne": "Each {subject} has at most {quantity} {object}",
  "dbml.relation.relatesToMany": "Each {subject} can be related to {quantity} {object}",
  "dbml.relation.one.m": "one",
  "dbml.relation.one.f": "one",
  "dbml.relation.many.m": "many",
  "dbml.relation.many.f": "many",
};

function translator(dictionary: Record<string, string>) {
  return (key: string, params?: Record<string, string>) =>
    (dictionary[key] ?? key).replace(/\{(\w+)\}/g, (_, name: string) => params?.[name] ?? `{${name}}`);
}

function table(name: string, columns: { name: string; notNull?: boolean; pk?: boolean }[]): DbmlTableModel {
  return {
    key: `public.${name}`,
    schema: "public",
    name,
    note: "",
    columns: columns.map((c) => ({
      name: c.name,
      type: "integer",
      pk: c.pk ?? false,
      notNull: c.notNull ?? false,
      unique: false,
      increment: false,
      defaultValue: null,
      note: "",
    })),
    indexes: [],
  };
}

function ref(from: [string, string, Relation], to: [string, string, Relation]): DbmlRefModel {
  return {
    id: `${from[0]}->${to[0]}`,
    name: null,
    from: { tableKey: `public.${from[0]}`, table: from[0], columns: [from[1]], relation: from[2] },
    to: { tableKey: `public.${to[0]}`, table: to[0], columns: [to[1]], relation: to[2] },
    onDelete: null,
    onUpdate: null,
  };
}

// The veterinary schema used to validate the canvas.
const VET = new Map(
  [
    table("usuarios", [{ name: "id", pk: true }]),
    table("animales", [{ name: "id", pk: true }, { name: "usuario_id", notNull: true }, { name: "especie_id" }]),
    table("especies", [{ name: "id", pk: true }]),
    table("citas", [{ name: "id", pk: true }, { name: "animal_id", notNull: true }]),
  ].map((t) => [t.key, t]),
);

function sentences(r: DbmlRefModel, lang: "es" | "en", tables = VET): string[] {
  const description = describeRelation(r, tables, lang);
  if (!description) throw new Error("no description");
  const translate = translator(lang === "es" ? ES : EN);
  return description.sentences.map((s) => renderSentence(s, translate));
}

describe("describeRelation", () => {
  it("reads the example the feature was asked for", () => {
    const owns = ref(["animales", "usuario_id", "*"], ["usuarios", "id", "1"]);

    expect(sentences(owns, "es")).toEqual([
      "Cada usuario puede tener muchos animales",
      "Cada animal pertenece a un usuario",
    ]);
  });

  it("says 'may belong' when the foreign key can be left empty", () => {
    // `especie_id` is nullable, so an animal need not have a species — and the article agrees with it.
    const species = ref(["animales", "especie_id", "*"], ["especies", "id", "1"]);

    expect(sentences(species, "es")).toEqual([
      "Cada especie puede tener muchos animales",
      "Cada animal puede pertenecer a una especie",
    ]);
  });

  it("agrees the quantity with a feminine object", () => {
    const appointments = ref(["citas", "animal_id", "*"], ["animales", "id", "1"]);

    expect(sentences(appointments, "es")[0]).toBe("Cada animal puede tener muchas citas");
  });

  it("reads `<` the same as the `>` it mirrors", () => {
    const written = ref(["usuarios", "id", "1"], ["animales", "usuario_id", "*"]);

    expect(sentences(written, "es")).toEqual([
      "Cada usuario puede tener muchos animales",
      "Cada animal pertenece a un usuario",
    ]);
  });

  it("describes one-to-one from the side holding the key", () => {
    const tables = new Map(
      [table("perfiles", [{ name: "usuario_id", notNull: true }]), table("usuarios", [{ name: "id", pk: true }])].map((t) => [t.key, t]),
    );
    const profile = ref(["perfiles", "usuario_id", "1"], ["usuarios", "id", "1"]);

    const description = describeRelation(profile, tables, "es");
    expect(description?.kind).toBe("oneToOne");
    expect(sentences(profile, "es", tables)).toEqual([
      "Cada perfil pertenece a un usuario",
      "Cada usuario tiene como máximo un perfil",
    ]);
  });

  it("describes many-to-many in both directions", () => {
    const tables = new Map([table("posts", []), table("tags", [])].map((t) => [t.key, t]));
    const tagged = ref(["posts", "id", "*"], ["tags", "id", "*"]);

    expect(sentences(tagged, "en", tables)).toEqual([
      "Each post can be related to many tags",
      "Each tag can be related to many posts",
    ]);
  });

  it("reads an English schema in English", () => {
    const tables = new Map(
      [table("users", [{ name: "id", pk: true }]), table("posts", [{ name: "user_id", notNull: true }])].map((t) => [t.key, t]),
    );
    const authored = ref(["posts", "user_id", "*"], ["users", "id", "1"]);

    expect(sentences(authored, "en", tables)).toEqual(["Each user can have many posts", "Each post belongs to one user"]);
  });

  it("answers null when a table is not in the document", () => {
    const dangling = ref(["animales", "usuario_id", "*"], ["dueños", "id", "1"]);

    expect(describeRelation(dangling, VET, "es")).toBeNull();
  });
});
