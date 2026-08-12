import { describe, expect, it } from "vitest";
import {
  countVerdicts,
  parseTicketVerdict,
  splitTicketReview,
  ticketVerdictFromStored,
} from "./parseTicketVerdict";
import { parseAnalysis } from "./parseAnalysis";

const FINDINGS = `📈 CALIDAD: Fiabilidad=C Seguridad=A Mantenibilidad=B

### ⚠️ [Mayor · Bug] off-by-one · F-001

El bucle deja fuera el último elemento.

📍 Ubicación: src/lib/paginate.ts:20-24

💭 Por qué: el índice final es exclusivo.

💡 Sugerencia: usar <=.

🎯 Confianza: 80/100`;

const VERDICT = `## VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN

### AC-1: El listado pagina de 20 en 20
Veredicto: cumple
Evidencia: src/lib/paginate.ts:12-18 — el tamaño de página se lee de la configuración
🎯 Confianza: 85/100

### AC-2: El rendimiento no baja de 200 ms
Veredicto: no verificable
Evidencia: sin evidencia en el diff
🎯 Confianza: 70/100

## VEREDICTO DE COBERTURA

Cobertura: incompleta
Faltante: la medición de rendimiento que pide el AC-2
Fuera de alcance: nada
Resumen: la paginación está implementada y probada; el criterio de latencia
no puede comprobarse leyendo el cambio.`;

const FULL = `${FINDINGS}\n\n${VERDICT}`;

describe("splitTicketReview", () => {
  it("hands the finding parser a slice with no criteria in it", () => {
    const { findings, verdict } = splitTicketReview(FULL);
    expect(findings).toBe(FINDINGS);
    expect(verdict).toContain("### AC-1:");
    expect(findings).not.toContain("AC-1");
  });

  it("leaves an ordinary review untouched", () => {
    expect(splitTicketReview(FINDINGS)).toEqual({ findings: FINDINGS, verdict: null });
  });
});

describe("parseTicketVerdict", () => {
  it("reads every criterion with its verdict, evidence and confidence", () => {
    const parsed = parseTicketVerdict(FULL);
    expect(parsed?.criteria).toHaveLength(2);
    expect(parsed?.criteria[0]).toEqual({
      id: "AC-1",
      criterion: "El listado pagina de 20 en 20",
      verdict: "cumple",
      evidence: "src/lib/paginate.ts:12-18 — el tamaño de página se lee de la configuración",
      confidence: 85,
    });
    expect(parsed?.criteria[1]?.verdict).toBe("no verificable");
    expect(parsed?.criteria[1]?.evidence).toBe("sin evidencia en el diff");
  });

  it("reads the coverage block, joining a wrapped summary", () => {
    expect(parseTicketVerdict(FULL)?.coverage).toEqual({
      coverage: "incompleta",
      missing: "la medición de rendimiento que pide el AC-2",
      outOfScope: "nada",
      summary:
        "la paginación está implementada y probada; el criterio de latencia no puede comprobarse leyendo el cambio.",
      // This fixture predates the relevance question and does not answer it, which reads as
      // relevant — the case the test below pins on its own.
      relevant: true,
      relevance: "",
    });
  });

  it("reads a ticket the review disowned, and does not grade its criteria", () => {
    // The case a user hit: a fixture ticket from another project matched one sentence of the diff
    // and came back `cumple` with full confidence. Neither verdict is honest for a ticket nobody
    // aimed at, so the review says the ticket is the wrong one instead.
    const parsed = parseTicketVerdict(
      "## VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN\n\nNo se puntúan.\n\n" +
        "## VEREDICTO DE COBERTURA\n\n" +
        "Relevancia: no corresponde — el ticket habla de importar archivos\n" +
        "Cobertura: no verificable\nResumen: revisa el work item vinculado.\n",
    );

    expect(parsed?.coverage?.relevant).toBe(false);
    expect(parsed?.coverage?.relevance).toContain("no corresponde");
    expect(parsed?.criteria).toEqual([]);
  });

  it("counts a review that never answered the relevance question as relevant", () => {
    // Silence is not a disavowal: an answer written before the question existed keeps its meaning.
    expect(parseTicketVerdict(FULL)?.coverage?.relevant).toBe(true);
  });

  it("answers null for a review that carries no criteria section", () => {
    expect(parseTicketVerdict(FINDINGS)).toBeNull();
  });

  it("accepts the emphasis the model adds despite being told not to", () => {
    const parsed = parseTicketVerdict(
      "## VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN\n\n### AC-1: **Algo**\nVeredicto: **parcial**\nEvidencia: `src/a.ts:1-2` — media\n",
    );
    expect(parsed?.criteria[0]?.verdict).toBe("parcial");
    expect(parsed?.criteria[0]?.criterion).toBe("Algo");
    expect(parsed?.criteria[0]?.confidence).toBeNull();
  });

  it("falls back to `no verificable` for a word it cannot read", () => {
    const parsed = parseTicketVerdict(
      "## VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN\n\n### AC-1: Algo\nVeredicto: quizás\nEvidencia: ninguna\n",
    );
    // Never `cumple`: an unreadable verdict is not evidence that the work was done.
    expect(parsed?.criteria[0]?.verdict).toBe("no verificable");
  });

  it("survives a criteria section with no criteria at all — the `mode: none` ticket", () => {
    const parsed = parseTicketVerdict(
      "## VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN\n\nEl ticket no declara criterios verificables.\n\n## VEREDICTO DE COBERTURA\n\nCobertura: no verificable\nFaltante: nada\nFuera de alcance: nada\nResumen: sin criterios que juzgar.\n",
    );
    expect(parsed?.criteria).toEqual([]);
    expect(parsed?.coverage?.coverage).toBe("no verificable");
  });

  it("keeps the criteria when the coverage block never arrived", () => {
    const parsed = parseTicketVerdict("## VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN\n\n### AC-1: Algo\nVeredicto: cumple\n");
    expect(parsed?.criteria).toHaveLength(1);
    expect(parsed?.coverage).toBeNull();
  });
});

describe("XLANG-001 is untouched", () => {
  it("parses the same findings whether or not the criteria section follows them", () => {
    // The load-bearing assertion of the whole design: two contracts over one answer.
    expect(parseAnalysis(FULL).findings).toEqual(parseAnalysis(FINDINGS).findings);
    expect(parseAnalysis(FULL).grades).toEqual(parseAnalysis(FINDINGS).grades);
  });

  it("keeps the criteria section out of the summary of a review with no findings", () => {
    const clean = "📈 CALIDAD: Fiabilidad=A Seguridad=A Mantenibilidad=A\n\n✅ Sin problemas.";
    const { findings } = splitTicketReview(`${clean}\n\n${VERDICT}`);
    expect(parseAnalysis(findings).summary).toBe("✅ Sin problemas.");
  });
});

describe("ticketVerdictFromStored", () => {
  it("reads a stored review back into the shape the live parser produces", () => {
    const rebuilt = ticketVerdictFromStored({
      criteria: [
        { id: "AC-1", criterion: "Algo", verdict: "cumple", evidence: "src/a.ts:1-2 — sí", confidence: 90 },
      ],
      coverage: { coverage: "incompleta", missing: "el AC-2", out_of_scope: "nada", summary: "casi" },
    });

    expect(rebuilt.criteria[0]?.verdict).toBe("cumple");
    expect(rebuilt.coverage?.outOfScope).toBe("nada");
  });

  it("widens a word no parser would write today to the cautious answer", () => {
    // Those columns are TEXT, and a row written by an older parser could hold anything. Reading it
    // back as `cumple` would be inventing a verdict nobody gave.
    const rebuilt = ticketVerdictFromStored({
      criteria: [{ id: "AC-1", criterion: "Algo", verdict: "quizás", evidence: "", confidence: null }],
      coverage: { coverage: "más o menos", missing: "", out_of_scope: "", summary: "" },
    });

    expect(rebuilt.criteria[0]?.verdict).toBe("no verificable");
    expect(rebuilt.coverage?.coverage).toBe("no verificable");
  });

  it("keeps a missing coverage block missing", () => {
    expect(ticketVerdictFromStored({ criteria: [], coverage: null }).coverage).toBeNull();
  });
});

describe("countVerdicts", () => {
  it("counts each verdict, zeros included", () => {
    expect(countVerdicts(parseTicketVerdict(FULL)!.criteria)).toEqual({
      cumple: 1,
      "no cumple": 0,
      parcial: 0,
      "no verificable": 1,
    });
  });
});
