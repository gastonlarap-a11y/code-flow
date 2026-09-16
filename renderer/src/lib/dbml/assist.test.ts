import { describe, expect, it } from "vitest";
import { checkProposal, rewritesDocument } from "./assist";

const SCHEMA = `Table usuarios {
  id integer [pk]
  email varchar(255) [not null]
}

Table animales {
  id integer [pk]
  usuario_id integer [not null]
}

Ref: animales.usuario_id > usuarios.id`;

describe("rewritesDocument", () => {
  it("is true only for the mode whose answer replaces the file", () => {
    expect(rewritesDocument("edit")).toBe(true);
    expect(rewritesDocument("review")).toBe(false);
    expect(rewritesDocument("explain")).toBe(false);
  });
});

describe("checkProposal", () => {
  it("accepts a valid rewrite and counts what it changed", () => {
    const proposal = `${SCHEMA}

Table vacunas {
  id integer [pk]
}`;

    const result = checkProposal(SCHEMA, proposal);

    expect(result.kind).toBe("ok");
    if (result.kind !== "ok") return;
    expect(result.summary.added).toEqual(["public.vacunas"]);
    expect(result.summary.removed).toEqual([]);
    expect(result.summary.kept).toBe(2);
  });

  it("reports the tables a proposal dropped, which the diff alone would bury", () => {
    // A model that obeys the format and returns half the document is the failure this catches.
    const result = checkProposal(SCHEMA, "Table usuarios {\n  id integer [pk]\n}");

    expect(result.kind).toBe("ok");
    if (result.kind !== "ok") return;
    expect(result.summary.removed).toEqual(["public.animales"]);
  });

  it("refuses prose, so an apology never replaces a schema", () => {
    const result = checkProposal(SCHEMA, "Lo siento, no puedo ayudarte con eso.");

    expect(result.kind).toBe("invalid");
    if (result.kind !== "invalid") return;
    expect(result.error).toMatch(/\(\d+:\d+\)/);
  });

  it("refuses an empty answer", () => {
    expect(checkProposal(SCHEMA, "   \n  ").kind).toBe("empty");
  });

  it("calls an identical document unchanged rather than offering it", () => {
    expect(checkProposal(SCHEMA, SCHEMA).kind).toBe("unchanged");
  });

  it("ignores line endings and trailing whitespace when deciding that", () => {
    const noisy = `${SCHEMA.replace(/\n/g, "\r\n")}   \r\n\r\n`;

    expect(checkProposal(SCHEMA, noisy).kind).toBe("unchanged");
  });

  it("still offers a proposal when the open document does not parse", () => {
    // The state the editor is in for most of an edit; every proposed table reads as added.
    const result = checkProposal("Table {{{", SCHEMA);

    expect(result.kind).toBe("ok");
    if (result.kind !== "ok") return;
    expect(result.summary.added).toEqual(["public.usuarios", "public.animales"]);
    expect(result.summary.removed).toEqual([]);
    expect(result.summary.kept).toBe(0);
  });

  it("keys tables by schema, so two tables of one name are two tables", () => {
    const before = "Table ventas.pedidos {\n  id integer [pk]\n}";
    const after = `${before}

Table compras.pedidos {
  id integer [pk]
}`;

    const result = checkProposal(before, after);

    expect(result.kind).toBe("ok");
    if (result.kind !== "ok") return;
    expect(result.summary.added).toEqual(["compras.pedidos"]);
    expect(result.summary.kept).toBe(1);
  });
});
