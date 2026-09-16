export interface FindingLocation {
  file: string;
  startLine: number;
  endLine: number;
}

/** The five SonarQube-style severity words the model writes in the finding header, kept verbatim
 * for display. `severity` below is the three-way bucket every piece of logic (the Quality Gate,
 * the card colour) keys on; this is the fine-grained label the reader sees. `XLANG-001`. */
export type SeverityLabel = "Blocker" | "Crítico" | "Mayor" | "Menor" | "Info";

export interface AnalysisFinding {
  id: string;
  severity: "critical" | "warning" | "info";
  /** The header's severity word, normalised to one of the five canonical labels — `null` when the
   * model wrote something none of them match. Purely for display; `severity` drives every decision. */
  severityLabel: SeverityLabel | null;
  type: string;
  category: string;
  subtitle: string;
  location: FindingLocation | null;
  why: string;
  suggestion: string;
  exampleLang: string;
  exampleCode: string;
  confidence: number | null;
}

export interface QualityGrades {
  reliability: string;
  security: string;
  maintainability: string;
}

export interface ParsedAnalysis {
  findings: AnalysisFinding[];
  /** Any prose before the first finding heading (e.g. a one-line "looks fine ✅" reply) —
   * also the fallback: if nothing matched the expected format at all, the full raw text
   * ends up here so something reasonable always renders. */
  summary: string;
  footer: string | null;
  /** The model's own A–E self-assessment for this change, parsed from the leading
   * "📈 CALIDAD" line — `null` if it didn't follow that format. */
  grades: QualityGrades | null;
  /** The model's own Quality Gate verdict, from the "🚦 Quality Gate:" line — `null` if absent.
   * Advisory only: `computeQualityGatePassed(findings)` stays the gate of record (`XLANG-001`). */
  selfReportedGate: "PASSED" | "FAILED" | null;
  /** Bullets of the trailing "## 👍 Lo que está bien" section — what the change gets right. */
  strengths: string[];
  /** Bullets of the trailing "## 🗒️ Notas" section — non-blocking context that is not a finding. */
  notes: string[];
}

const FOOTER_RE = /\n?---\n🤖[^\n]*$/;
const GRADES_RE = /^📈\s*CALIDAD:\s*Fiabilidad=([A-E])\s+Seguridad=([A-E])\s+Mantenibilidad=([A-E])\s*$/m;
/** The model's self-reported Quality Gate line, right after "📈 CALIDAD". Advisory: the gate of
 * record is `computeQualityGatePassed(findings)`. `XLANG-001`. */
const GATE_RE = /^🚦\s*Quality Gate:\s*(PASSED|FAILED)\s*$/im;
/** The header's emoji alternation. Five since the WF-PR-REVIEWER re-sync (🔴 Blocker · 🚨 Crítico ·
 * 🟠 Mayor · 🟡 Menor · 🔵 Info); ⚠️ and ℹ️ stay so every `review_runs` row written before it still
 * parses. The severity *word* in the brackets is what wins — see `severityOf`. `XLANG-001`. */
const HEADER_RE = /^###\s*(🔴|🚨|🟠|🟡|🔵|⚠️|ℹ️)\s*\[([^·\]]+)·([^\]]+)\]\s*([^·]+)·\s*(F-\d+)\s*$/;
/** The two trailing sections the standard asks for after the findings. Accents optional, like
 * `Ubicacion`. `XLANG-001`. */
const STRENGTHS_HEADING_RE = /^[ \t]*##[ \t]*👍[ \t]*Lo que est[aá] bien[ \t]*$/im;
const NOTES_HEADING_RE = /^[ \t]*##[ \t]*🗒️?[ \t]*Notas[ \t]*$/im;
/** Either trailing-section heading — used to cut the afterword off the finding text. */
const AFTERWORD_HEADING_RE = /^[ \t]*##[ \t]*(?:👍[ \t]*Lo que est[aá] bien|🗒️?[ \t]*Notas)[ \t]*$/im;

/**
 * A finding's severity, from the word the model wrote — falling back to the emoji.
 *
 * The header carries the severity twice: as one of five words inside the brackets, and as one of
 * three emoji the prompt asks be derived from it. This read only the emoji and threw the word away,
 * so when the model wrote `### 🚨 [Mayor · Security Hotspot]` — the right word, the wrong emoji,
 * against its own instructions — two `Mayor` findings rendered as `critical` and the Quality Gate
 * went red for them. Observed on this repository's own pull request.
 *
 * The word wins because it is the one the model reasoned about; the emoji is decoration derived from
 * it, three symbols for five levels. The emoji stays as the fallback for an unrecognised word, so
 * nothing that parsed before stops parsing now.
 *
 * `XLANG-001`: `src/CodeFlow.App/Review/ReviewMemory.cs` holds the same table and the two change
 * together.
 */
function severityOf(severity: string | undefined, emoji: string): AnalysisFinding["severity"] {
  switch (severity?.trim().toLowerCase()) {
    case "blocker":
    case "crítico":
    case "critico":
      return "critical";
    case "mayor":
      return "warning";
    case "menor":
    case "info":
      return "info";
    default:
      if (emoji === "🔴" || emoji === "🚨") return "critical";
      if (emoji === "🟠" || emoji === "⚠️") return "warning";
      return "info";
  }
}

/** The header's severity word normalised to one of the five canonical labels for display, falling
 * back to the emoji when the word is unrecognised. Kept separate from {@link severityOf}: that one
 * buckets into three for every decision, this one preserves the level the reader is shown.
 * `XLANG-001` — `ReviewMemory.SeverityLabelOf` holds the same table. */
function severityLabelOf(severity: string | undefined, emoji: string): SeverityLabel | null {
  switch (severity?.trim().toLowerCase()) {
    case "blocker":
      return "Blocker";
    case "crítico":
    case "critico":
      return "Crítico";
    case "mayor":
      return "Mayor";
    case "menor":
      return "Menor";
    case "info":
      return "Info";
    default:
      switch (emoji) {
        case "🔴":
          return "Blocker";
        case "🚨":
          return "Crítico";
        case "🟠":
        case "⚠️":
          return "Mayor";
        case "🟡":
          return "Menor";
        case "🔵":
        case "ℹ️":
          return "Info";
        default:
          return null;
      }
  }
}

/** The `- ` / `* ` bullets under one trailing "## …" section of a review — `## 👍 Lo que está bien`
 * or `## 🗒️ Notas`. Stops at the next `## ` heading. */
function bulletsUnder(afterword: string, headingRe: RegExp): string[] {
  const m = afterword.match(headingRe);
  if (!m || m.index === undefined) return [];
  const rest = afterword.slice(m.index + m[0].length);
  const nextHeading = rest.search(/^[ \t]*##[ \t]/m);
  const body = nextHeading === -1 ? rest : rest.slice(0, nextHeading);
  return body
    .split("\n")
    .map((l) => l.replace(/^[ \t]*[-*][ \t]+/, "").trim())
    .filter((l) => l.length > 0);
}

/** Parses "{file}:{startLine}-{endLine}" (or a single "{file}:{line}") from the finding's
 * "📍 Ubicación" field — the file/line this comment gets anchored to when posted to the PR.
 * The model markdown-formats file paths/identifiers everywhere else in its output (it isn't
 * told not to here either), so e.g. "`src/foo.ts:24-27`" is just as likely as the plain form
 * the prompt actually asks for — strip that wrapping before matching, or a location that
 * parses to nothing silently falls back to an unanchored comment. */
function parseLocation(raw: string | undefined): FindingLocation | null {
  if (!raw) return null;
  const cleaned = raw.trim().replace(/[`*_]+/g, "").trim();
  const m = cleaned.match(/^(.+?):(\d+)(?:-(\d+))?\s*$/);
  if (!m) return null;
  const startLine = Number(m[2]);
  const endLine = m[3] ? Number(m[3]) : startLine;
  return { file: m[1]!.trim(), startLine, endLine };
}

/** Parses the structured "### {emoji} [Severity · Type] Category · F-00N" findings format
 * the review/analysis prompts are instructed to produce. Deliberately lenient — a finding
 * that doesn't fully match every sub-field still renders with whatever it has, and text
 * that never even opens a heading falls back to `summary` rather than disappearing. */
export function parseAnalysis(raw: string): ParsedAnalysis {
  let text = raw.trim();
  let footer: string | null = null;
  const footerMatch = text.match(FOOTER_RE);
  if (footerMatch && footerMatch.index !== undefined) {
    footer = footerMatch[0].replace(/^\n?---\n/, "").trim();
    text = text.slice(0, footerMatch.index).trim();
  }

  let grades: QualityGrades | null = null;
  const gradesMatch = text.match(GRADES_RE);
  if (gradesMatch && gradesMatch.index !== undefined) {
    // All three groups are required by GRADES_RE, so they're always captured on a match.
    grades = { reliability: gradesMatch[1]!, security: gradesMatch[2]!, maintainability: gradesMatch[3]! };
    text = (text.slice(0, gradesMatch.index) + text.slice(gradesMatch.index + gradesMatch[0].length)).trim();
  }

  let selfReportedGate: ParsedAnalysis["selfReportedGate"] = null;
  const gateMatch = text.match(GATE_RE);
  if (gateMatch && gateMatch.index !== undefined) {
    selfReportedGate = gateMatch[1]!.toUpperCase() as "PASSED" | "FAILED";
    text = (text.slice(0, gateMatch.index) + text.slice(gateMatch.index + gateMatch[0].length)).trim();
  }

  // The two trailing "## …" sections come after the findings, so they have to be lifted out
  // *before* the finding loop — otherwise the last finding's block swallows them, and a number in
  // a "Lo que está bien" bullet gets read as its confidence.
  let strengths: string[] = [];
  let notes: string[] = [];
  const afterwordMatch = text.match(AFTERWORD_HEADING_RE);
  if (afterwordMatch && afterwordMatch.index !== undefined) {
    const afterword = text.slice(afterwordMatch.index);
    strengths = bulletsUnder(afterword, STRENGTHS_HEADING_RE);
    notes = bulletsUnder(afterword, NOTES_HEADING_RE);
    text = text.slice(0, afterwordMatch.index).trimEnd();
  }

  const lines = text.split("\n");
  const findings: AnalysisFinding[] = [];
  const summaryLines: string[] = [];
  let sawHeader = false;
  let i = 0;

  while (i < lines.length) {
    const line = lines[i]!;
    const headerMatch = line.match(HEADER_RE);
    if (!headerMatch) {
      if (!sawHeader) summaryLines.push(line);
      i++;
      continue;
    }
    sawHeader = true;
    // All five groups are required by HEADER_RE, so they're always captured on a match.
    const [, emoji, severityRaw, typeRaw, categoryRaw, id] = headerMatch;
    i++;
    const blockLines: string[] = [];
    while (i < lines.length) {
      const nextLine = lines[i]!;
      if (nextLine.match(HEADER_RE)) break;
      blockLines.push(nextLine);
      i++;
    }
    const block = blockLines.join("\n").trim();

    const subtitleMatch = block.match(/^([\s\S]*?)\n\n?(?:📍|💭)/);
    const locationMatch = block.match(/📍\s*Ubicaci[oó]n:\s*([^\n]+)/);
    const whyMatch = block.match(/💭\s*Por qué:\s*([\s\S]*?)\n\n?💡/);
    const suggestionMatch = block.match(/💡\s*Sugerencia:\s*([\s\S]*?)\n\n?(?:🛠️|🎯)/);
    const codeMatch = block.match(/```(\w*)\n([\s\S]*?)```/);
    const confidenceMatch = block.match(/🎯\s*Confianza:\s*(\d+)/);

    findings.push({
      id: id!.trim(),
      severity: severityOf(severityRaw, emoji!),
      severityLabel: severityLabelOf(severityRaw, emoji!),
      type: typeRaw!.trim(),
      category: categoryRaw!.trim(),
      subtitle: (subtitleMatch?.[1] ?? block.split("\n")[0] ?? "").trim(),
      location: parseLocation(locationMatch?.[1]),
      why: (whyMatch?.[1] ?? "").trim(),
      suggestion: (suggestionMatch?.[1] ?? "").trim(),
      exampleLang: codeMatch?.[1] ?? "",
      exampleCode: codeMatch?.[2] ?? "",
      confidence: confidenceMatch ? Number(confidenceMatch[1]) : null,
    });
  }

  return { findings, summary: summaryLines.join("\n").trim(), footer, grades, selfReportedGate, strengths, notes };
}

/** Fallback emoji when a finding has no `severityLabel` (an unrecognised severity word on an
 * `⚠️`/`ℹ️` header from an old `review_runs` row). Three buckets, like `severity`. */
const SEVERITY_EMOJI: Record<AnalysisFinding["severity"], string> = {
  critical: "🚨",
  warning: "🟠",
  info: "🔵",
};

/** The five-level scale, keyed by the display label — matches the header emoji the standard now
 * asks for and `engine-contract.md`'s visual vocabulary. Used for the comment header and the
 * summary table's dot. */
const SEVERITY_LABEL_EMOJI: Record<SeverityLabel, string> = {
  Blocker: "🔴",
  Crítico: "🚨",
  Mayor: "🟠",
  Menor: "🟡",
  Info: "🔵",
};

const SEVERITY_LABEL_ES: Record<AnalysisFinding["severity"], string> = {
  critical: "Crítico",
  warning: "Mayor",
  info: "Info",
};

/** The finding's five-level emoji, falling back to the three-bucket one. */
export function findingEmoji(finding: AnalysisFinding): string {
  return finding.severityLabel ? SEVERITY_LABEL_EMOJI[finding.severityLabel] : SEVERITY_EMOJI[finding.severity];
}

/** The finding's severity word for display, falling back to the three-bucket label. */
export function findingSeverityLabel(finding: AnalysisFinding): string {
  return finding.severityLabel ?? SEVERITY_LABEL_ES[finding.severity];
}

export function locationLabel(location: FindingLocation): string {
  return `${location.file}:${location.startLine}${location.endLine !== location.startLine ? `-${location.endLine}` : ""}`;
}

/** No critical findings = the change is postable/mergeable as far as this review is
 * concerned — computed deterministically rather than asking the model to self-report a
 * pass/fail that might contradict its own findings list. */
export function computeQualityGatePassed(findings: AnalysisFinding[]): boolean {
  return !findings.some((f) => f.severity === "critical");
}

/** Reconstructs one finding as a standalone markdown block — the same shape the model
 * produced for it in the first place, just without the other findings around it, and
 * without the "📍 Ubicación" line (redundant once the comment is anchored to that exact
 * line on the PR). Used to post a PR review as one comment thread per finding instead of one
 * giant comment. */
export function formatFindingAsComment(finding: AnalysisFinding): string {
  const lines = [
    `### ${findingEmoji(finding)} [${findingSeverityLabel(finding)} · ${finding.type}] ${finding.category} · ${finding.id}`,
    "",
    finding.subtitle,
  ];
  if (finding.why) lines.push("", `💭 **Por qué:** ${finding.why}`);
  if (finding.suggestion) lines.push("", `💡 **Sugerencia:** ${finding.suggestion}`);
  if (finding.exampleCode) lines.push("", "🛠️ Ejemplo de solución:", `\`\`\`${finding.exampleLang}`, finding.exampleCode, "```");
  if (finding.confidence !== null) lines.push("", `🎯 Confianza: ${finding.confidence}/100`);
  return lines.join("\n");
}

export interface ReviewCommentInput {
  content: string;
  location: FindingLocation | null;
}

/** The full set of Azure DevOps comment threads for one review: the summary first (Quality
 * Gate + grades + findings table, unanchored), then one thread per finding — anchored to its
 * file/line when the model reported one, a general comment otherwise. */
export function buildReviewComments(parsed: ParsedAnalysis, date: string): ReviewCommentInput[] {
  return [
    // This helper posts every finding, so the summary describes every finding.
    { content: formatSummaryComment(parsed, date, parsed.findings), location: null },
    ...parsed.findings.map((f) => ({ content: formatFindingAsComment(f), location: f.location })),
  ];
}

/** The instruction text sent to Claude for the "Resolve with AI" action — unlike
 * `formatFindingAsComment` (which omits location because the PR comment is already anchored
 * to that line), this needs the location spelled out since it's the only way Claude knows
 * where to make the edit. */
export function formatFindingAsFixPrompt(finding: AnalysisFinding): string {
  const lines = [`Hallazgo ${finding.id} (${finding.severity}): ${finding.subtitle}`];
  if (finding.location) lines.push(`Ubicación: ${locationLabel(finding.location)}`);
  if (finding.why) lines.push(`Por qué: ${finding.why}`);
  if (finding.suggestion) lines.push(`Sugerencia: ${finding.suggestion}`);
  if (finding.exampleCode) lines.push("Ejemplo de solución:", `\`\`\`${finding.exampleLang}`, finding.exampleCode, "```");
  return lines.join("\n");
}

/** The **fix-pack**: the review's findings as an actionable JSON artifact (schema
 * `pr-review-fixpack/v1`) another agent can consume to apply the fixes — fields renamed to action
 * terms (`problema`/`causa`/`correccion`) rather than the review's own. Provider-neutral: it's a
 * string you can copy, export, or post. */
export function buildFixpack(parsed: ParsedAnalysis, prId: number): string {
  const hallazgos = parsed.findings.map((f) => ({
    id: f.id,
    severidad: f.severity,
    severidad_label: findingSeverityLabel(f),
    tipo: f.type,
    categoria: f.category,
    archivo: f.location?.file ?? null,
    lineas: f.location ? locationLabel(f.location).split(":")[1] ?? null : null,
    problema: f.subtitle,
    causa: f.why,
    correccion: f.suggestion,
    codigo_sugerido: f.exampleCode || null,
    confianza: f.confidence,
  }));
  return JSON.stringify(
    {
      schema: "pr-review-fixpack/v1",
      pr: prId,
      generado: new Date().toISOString(),
      hallazgos,
      fortalezas: parsed.strengths,
      notas: parsed.notes,
    },
    null,
    2,
  );
}

/**
 * The overview comment posted once per review — Quality Gate + A–E grades + a table linking every
 * posted finding to its file/line, mirroring a standard "PR review summary" format.
 *
 * `posted` is what the reviewer actually chose to publish, and it's what the table and the count
 * describe: the summary must not announce findings that were never posted (it says "Hallazgos
 * posteados", and a table of comments nobody can find on the PR is worse than no table).
 *
 * The **Quality Gate and the grades stay computed from the whole review**, deliberately. They
 * judge the change, not the reviewer's selection — a Blocker doesn't stop being a Blocker because
 * it wasn't posted, and letting the gate flip to PASSED by unticking a box would make it a
 * meaningless badge. When the two sets differ, the count says so, so nobody reads a FAILED gate
 * over a one-row table as a contradiction.
 */
const SEVERITY_LABEL_ORDER: readonly SeverityLabel[] = ["Blocker", "Crítico", "Mayor", "Menor", "Info"];

/** Appends "## 👍 Lo que está bien" / "## 🗒️ Notas" bullet blocks when the review carried them. */
function appendAfterword(lines: string[], parsed: ParsedAnalysis): void {
  if (parsed.strengths.length > 0) {
    lines.push("", "## 👍 Lo que está bien", ...parsed.strengths.map((s) => `- ${s}`));
  }
  if (parsed.notes.length > 0) {
    lines.push("", "## 🗒️ Notas", ...parsed.notes.map((n) => `- ${n}`));
  }
}

export function formatSummaryComment(parsed: ParsedAnalysis, date: string, posted: AnalysisFinding[]): string {
  const passed = computeQualityGatePassed(parsed.findings);
  const lines = [`### 📋 Revisión automatizada (pr-review) — ${date}`, "", `🚦 **Quality Gate:** ${passed ? "✅ PASSED" : "❌ FAILED"}`];
  if (parsed.grades) {
    lines.push(
      `🛡️ Fiabilidad **${parsed.grades.reliability}** · 🔒 Seguridad **${parsed.grades.security}** · 🧹 Mantenibilidad **${parsed.grades.maintainability}**`,
    );
  }
  lines.push("");

  if (parsed.findings.length === 0) {
    lines.push(parsed.summary || "✅ No se encontraron problemas en este cambio.");
    appendAfterword(lines, parsed);
    return lines.join("\n");
  }
  if (posted.length === 0) {
    lines.push(
      `La revisión encontró ${parsed.findings.length} hallazgo(s), ninguno de los cuales se publicó como comentario.`,
    );
    appendAfterword(lines, parsed);
    return lines.join("\n");
  }

  const counts = SEVERITY_LABEL_ORDER.map((label) => ({
    label,
    n: posted.filter((f) => findingSeverityLabel(f) === label).length,
  }))
    .filter(({ n }) => n > 0)
    .map(({ label, n }) => `${n} ${label}`);
  const outOf = posted.length < parsed.findings.length ? ` de ${parsed.findings.length}` : "";
  lines.push(`**Hallazgos posteados:** ${posted.length}${outOf} (${counts.join(" · ")})`, "");
  lines.push("| | ID | Hallazgo | Archivo | Confianza |", "|---|---|---|---|---|");
  for (const f of posted) {
    const loc = f.location ? `\`${locationLabel(f.location)}\`` : "—";
    lines.push(`| ${findingEmoji(f)} | ${f.id} | ${f.type} / ${f.category} | ${loc} | ${f.confidence ?? "—"} |`);
  }
  appendAfterword(lines, parsed);
  return lines.join("\n");
}
