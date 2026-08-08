import { describe, expect, test } from "vitest";
import {
  buildReviewComments,
  computeQualityGatePassed,
  formatFindingAsComment,
  locationLabel,
  parseAnalysis,
} from "./parseAnalysis";

/**
 * The AI reviewer's output, turned into things the user acts on.
 *
 * This is the highest-consequence pure logic in the renderer: what comes out of here becomes
 * comment threads posted to a real pull request, anchored to a real file and line. A regex that
 * quietly stops matching does not throw — it produces a finding with no location, which posts as an
 * unanchored comment, or a review that parses to zero findings and reads as "nothing wrong".
 *
 * The header format is `XLANG-001`, a three-way contract between the prompt that produces it, this
 * parser and `ReviewMemory.ParseFindings` on the C# side. Both parsers are checked here against the
 * same shape.
 */

/** One finding in exactly the shape `DEFAULT_PR_REVIEW_STANDARD` asks the model for. */
const REVIEW = `📈 CALIDAD: Fiabilidad=B Seguridad=C Mantenibilidad=A

Resumen breve del cambio.

### 🚨 [Alta·Bug] Seguridad · F-001
El token se registra en texto plano.

📍 Ubicación: src/auth/login.ts:42-47

💭 Por qué: cualquiera con acceso a los logs obtiene la sesión.

💡 Sugerencia: redactar el campo antes de registrarlo.

🛠️ Ejemplo de solución:
\`\`\`ts
logger.info({ token: "[redacted]" });
\`\`\`

🎯 Confianza: 95

### ℹ️ [Baja·Smell] Mantenibilidad · F-002
Nombre poco descriptivo.

📍 Ubicación: src/util/x.ts:3

💭 Por qué: cuesta leerlo.

💡 Sugerencia: renombrar.

🎯 Confianza: 40

---
🤖 Generado por CodeFlow`;

describe("parseAnalysis", () => {
  test("reads a whole review into findings, summary, grades and footer", () => {
    const parsed = parseAnalysis(REVIEW);

    expect(parsed.findings).toHaveLength(2);
    expect(parsed.summary).toBe("Resumen breve del cambio.");
    expect(parsed.grades).toEqual({ reliability: "B", security: "C", maintainability: "A" });
    expect(parsed.footer).toBe("🤖 Generado por CodeFlow");
  });

  test("the first finding keeps every field the prompt asked for", () => {
    const [first] = parseAnalysis(REVIEW).findings;
    if (!first) throw new Error("expected a finding");

    expect(first).toMatchObject({
      id: "F-001",
      severity: "critical",
      type: "Bug",
      category: "Seguridad",
      subtitle: "El token se registra en texto plano.",
      location: { file: "src/auth/login.ts", startLine: 42, endLine: 47 },
      exampleLang: "ts",
      confidence: 95,
    });
    expect(first.why).toBe("cualquiera con acceso a los logs obtiene la sesión.");
    expect(first.suggestion).toBe("redactar el campo antes de registrarlo.");
  });

  test("an unrecognised severity word falls back to the emoji", () => {
    const [critical, info] = parseAnalysis(REVIEW).findings;
    if (!critical || !info) throw new Error("expected two findings");

    expect(critical.severity).toBe("critical");
    expect(info.severity).toBe("info");
    const [warning] = parseAnalysis("### ⚠️ [Media·Smell] Perf · F-001\nx").findings;
    if (!warning) throw new Error("expected a finding");
    expect(warning.severity).toBe("warning");
  });

  test("the word in the brackets beats the emoji when the two disagree", () => {
    // Observed on this repository's own pull request: the model wrote the right word and the wrong
    // emoji, against the mapping its own prompt gives it. Reading only the emoji stored two `Mayor`
    // findings as critical and turned the Quality Gate red for them.
    const [mayor] = parseAnalysis("### 🚨 [Mayor · Security Hotspot] secretos-en-log · F-011\nx").findings;
    if (!mayor) throw new Error("expected a finding");
    expect(mayor.severity).toBe("warning");

    const [blocker] = parseAnalysis("### ℹ️ [Blocker · Bug] data-loss · F-002\nx").findings;
    if (!blocker) throw new Error("expected a finding");
    expect(blocker.severity).toBe("critical");

    // …and the gate follows the corrected severity, which is the whole point.
    expect(computeQualityGatePassed([mayor])).toBe(true);
    expect(computeQualityGatePassed([blocker])).toBe(false);
  });

  test("the five severity words each land somewhere", () => {
    const severityOf = (word: string) =>
      parseAnalysis(`### ⚠️ [${word} · Bug] x · F-001\ny`).findings[0]?.severity;

    expect(severityOf("Blocker")).toBe("critical");
    expect(severityOf("Crítico")).toBe("critical");
    expect(severityOf("Mayor")).toBe("warning");
    expect(severityOf("Menor")).toBe("info");
    expect(severityOf("Info")).toBe("info");
  });

  test("a block belonging to the next finding never bleeds into this one", () => {
    // The fields are positional, so a parser that scanned forward past the next header would
    // attach finding 2's location to finding 1 — and the comment would land on the wrong file.
    const [first, second] = parseAnalysis(REVIEW).findings;
    if (!first || !second) throw new Error("expected two findings");

    expect(first.location?.file).toBe("src/auth/login.ts");
    expect(second.location?.file).toBe("src/util/x.ts");
    expect(second.confidence).toBe(40);
  });
});

describe("parseLocation, through parseAnalysis", () => {
  const locate = (raw: string) => {
    const [finding] = parseAnalysis(`### 🚨 [Alta·Bug] X · F-001\ns\n\n📍 Ubicación: ${raw}\n`).findings;
    if (!finding) throw new Error("expected a finding");
    return finding.location;
  };

  test("a single line is its own range", () => {
    expect(locate("src/a.ts:12")).toEqual({ file: "src/a.ts", startLine: 12, endLine: 12 });
  });

  test("markdown wrapping is stripped before matching", () => {
    // The model formats paths as code everywhere else and is not told to stop here. Without the
    // strip, the location parses to nothing and the finding posts unanchored — silently.
    expect(locate("`src/a.ts:12-15`")).toEqual({ file: "src/a.ts", startLine: 12, endLine: 15 });
    expect(locate("**src/a.ts:12**")).toEqual({ file: "src/a.ts", startLine: 12, endLine: 12 });
  });

  test("a Windows path keeps its drive letter", () => {
    // The match is non-greedy up to the *last* colon-number, so `C:` must not be taken as the file.
    expect(locate("C:/repo/src/a.ts:12")).toEqual({ file: "C:/repo/src/a.ts", startLine: 12, endLine: 12 });
  });

  test("the unaccented spelling is accepted", () => {
    const [finding] = parseAnalysis("### 🚨 [Alta·Bug] X · F-001\ns\n\n📍 Ubicacion: src/a.ts:9\n").findings;
    if (!finding) throw new Error("expected a finding");

    expect(finding.location).toEqual({ file: "src/a.ts", startLine: 9, endLine: 9 });
  });

  test("a location with no line number yields nothing rather than a wrong anchor", () => {
    expect(locate("src/a.ts")).toBeNull();
  });
});

describe("degrading rather than disappearing", () => {
  test("text that never opens a heading becomes the summary", () => {
    // The fallback that keeps a "looks fine ✅" reply visible instead of rendering an empty panel.
    const parsed = parseAnalysis("Todo se ve bien, no hay hallazgos.");

    expect(parsed.findings).toHaveLength(0);
    expect(parsed.summary).toBe("Todo se ve bien, no hay hallazgos.");
  });

  test("a finding missing every optional field still parses", () => {
    const [only] = parseAnalysis("### ⚠️ [Media·Smell] Estilo · F-003\nAlgo pasa.").findings;
    if (!only) throw new Error("expected a finding");

    expect(only).toMatchObject({ id: "F-003", category: "Estilo", location: null, confidence: null });
    expect(only.why).toBe("");
  });
});

describe("what reaches the pull request", () => {
  test("the quality gate is computed from the findings, not self-reported", () => {
    const parsed = parseAnalysis(REVIEW);

    expect(computeQualityGatePassed(parsed.findings)).toBe(false);
    expect(computeQualityGatePassed(parsed.findings.filter((f) => f.severity !== "critical"))).toBe(true);
  });

  test("a posted comment drops the location and keeps everything else", () => {
    // The location is redundant once the thread is anchored to that line, and repeating it in the
    // body is how a comment ends up naming a line it is not on after the file moves.
    const [firstFinding] = parseAnalysis(REVIEW).findings;
    if (!firstFinding) throw new Error("expected a finding");
    const comment = formatFindingAsComment(firstFinding);

    expect(comment).toContain("### 🚨 [Bug] Seguridad · F-001");
    expect(comment).not.toContain("📍");
    expect(comment).toContain("💭 **Por qué:**");
    expect(comment).toContain("🎯 Confianza: 95/100");
  });

  test("an unanchored summary comes first, then one anchored comment per finding", () => {
    const comments = buildReviewComments(parseAnalysis(REVIEW), "2026-07-30");

    // One thread per finding is the whole point: a single giant comment cannot be resolved
    // per-finding on either host. The summary leads and is deliberately unanchored — it is about
    // the change, not about a line.
    expect(comments).toHaveLength(3);
    const [summaryComment] = comments;
    if (!summaryComment) throw new Error("expected a summary comment");
    expect(summaryComment.location).toBeNull();
    expect(comments.slice(1).map((c) => c.location)).toEqual([
      { file: "src/auth/login.ts", startLine: 42, endLine: 47 },
      { file: "src/util/x.ts", startLine: 3, endLine: 3 },
    ]);
  });

  test("a range renders as a range and a single line does not", () => {
    expect(locationLabel({ file: "a.ts", startLine: 1, endLine: 4 })).toBe("a.ts:1-4");
    expect(locationLabel({ file: "a.ts", startLine: 1, endLine: 1 })).toBe("a.ts:1");
  });
});
