/** The four answers a criterion can get. `no verificable` is a first-class one, not a failure. */
export type CriterionVerdict = "cumple" | "no cumple" | "parcial" | "no verificable";

/** The three answers the branch as a whole can get. */
export type CoverageVerdict = "completa" | "incompleta" | "no verificable";

export interface TicketCriterionVerdict {
  /** `AC-1` … `AC-N` — the criterion's own id, so the table and the ticket agree. */
  id: string;
  criterion: string;
  verdict: CriterionVerdict;
  /** `path:lines — why`, or the sentence saying there is none. Kept as written. */
  evidence: string;
  confidence: number | null;
}

export interface TicketCoverage {
  coverage: CoverageVerdict;
  missing: string;
  outOfScope: string;
  summary: string;
  /**
   * Whether the ticket describes this change at all.
   *
   * <b>Read before the criteria, because it can invalidate them.</b> A branch can be linked to the
   * wrong work item, and a criterion generic enough — *"if the key exists, update it"* — matches
   * almost any code that talks to a database. Grading such a ticket criterion by criterion yields a
   * confident `cumple` off a coincidence, which is exactly what happened the first time a fixture
   * ticket from another project was linked.
   */
  relevant: boolean;
  /** Why, in one line. Empty when the model did not answer it. */
  relevance: string;
}

export interface ParsedTicketVerdict {
  criteria: TicketCriterionVerdict[];
  coverage: TicketCoverage | null;
}

/**
 * `XLANG-016`: these two headers are literals the model is told to emit verbatim, and
 * `src/CodeFlow.App/Tickets/TicketVerdict.cs` matches on the same pair. The accent is optional in
 * both parsers for the same reason `parseAnalysis.ts` accepts `Ubicacion` — a dropped accent is the
 * cheapest thing to tolerate and the most expensive to lose an answer to.
 */
const CRITERIA_HEADING_RE = /^[ \t]*##[ \t]*VERIFICACI[OÓ]N DE CRITERIOS DE ACEPTACI[OÓ]N[ \t]*$/im;
const COVERAGE_HEADING_RE = /^[ \t]*##[ \t]*VEREDICTO DE COBERTURA[ \t]*$/im;
const CRITERION_HEADER_RE = /^[ \t]*###[ \t]*(AC-\d+)[ \t]*:?[ \t]*([^\n]*)$/gim;
const CONFIDENCE_RE = /🎯\s*Confianza:\s*(\d{1,3})/;

const FIELD_LABELS = [
  "Veredicto:",
  "Evidencia:",
  "Relevancia:",
  "Cobertura:",
  "Faltante:",
  "Fuera de alcance:",
  "Resumen:",
];

/** Strips the emphasis the model wraps values in despite being told not to. */
function clean(value: string): string {
  return value.trim().replace(/^[*`_:\s]+/, "").replace(/[*`_:\s]+$/, "").trim();
}

/**
 * One labelled field: the rest of its line plus any wrapped continuation, up to the next label,
 * heading, confidence line or blank line.
 */
function field(block: string, label: string): string {
  const collected: string[] = [];
  let inside = false;

  for (const raw of block.split("\n")) {
    const line = raw.replace(/\r$/, "").trimStart();
    if (!inside) {
      if (!line.toLowerCase().startsWith(label.toLowerCase())) continue;
      inside = true;
      collected.push(line.slice(label.length).trim());
      continue;
    }
    if (
      line.length === 0 ||
      line.startsWith("#") ||
      line.startsWith("🎯") ||
      FIELD_LABELS.some((l) => line.toLowerCase().startsWith(l.toLowerCase()))
    ) {
      break;
    }
    collected.push(line.trim());
  }

  return clean(collected.filter((l) => l.length > 0).join(" "));
}

/**
 * Maps what the model wrote onto the four literals, defaulting to `no verificable`.
 *
 * The default is the conservative one deliberately: the prompt's standing order is to prefer a false
 * alarm to approving incomplete work, and a verdict this parser could not read is not evidence that
 * a criterion was met. `src/CodeFlow.App/Tickets/TicketVerdict.cs` holds the same table.
 */
function normaliseVerdict(raw: string): CriterionVerdict {
  const value = clean(raw).toLowerCase();
  if (value.startsWith("cumple")) return "cumple";
  if (value.startsWith("no cumple")) return "no cumple";
  if (value.startsWith("parcial")) return "parcial";
  return "no verificable";
}

function normaliseCoverage(raw: string): CoverageVerdict {
  const value = clean(raw).toLowerCase();
  if (value.startsWith("completa")) return "completa";
  if (value.startsWith("incompleta")) return "incompleta";
  return "no verificable";
}

/**
 * Cuts a ticket review into the slice `parseAnalysis` reads and the slice this module reads.
 *
 * The two are disjoint by construction rather than by convention. `### AC-1:` could never match
 * `parseAnalysis`'s finding header anyway — no emoji, no bracketed severity, no `F-NNN` — but the
 * cut also keeps the criteria table out of the `summary` fallback, which is where an answer with no
 * findings at all would otherwise dump it.
 */
export function splitTicketReview(raw: string): { findings: string; verdict: string | null } {
  const match = raw.match(CRITERIA_HEADING_RE);
  if (!match || match.index === undefined) return { findings: raw, verdict: null };
  return { findings: raw.slice(0, match.index).trimEnd(), verdict: raw.slice(match.index) };
}

/**
 * Parses the criteria table and the coverage verdict out of a review, or `null` when the review
 * carries neither — which is every review this feature did not produce, and also a ticket review the
 * model answered without the closing sections. Tolerant on purpose: the caller renders the text as
 * an ordinary analysis rather than losing it.
 */
export function parseTicketVerdict(raw: string): ParsedTicketVerdict | null {
  const { verdict } = splitTicketReview(raw);
  if (verdict === null) return null;

  const coverageMatch = verdict.match(COVERAGE_HEADING_RE);
  const criteriaText = coverageMatch?.index !== undefined ? verdict.slice(0, coverageMatch.index) : verdict;

  const headers = [...criteriaText.matchAll(CRITERION_HEADER_RE)];
  const criteria: TicketCriterionVerdict[] = headers.map((header, i) => {
    const start = header.index + header[0].length;
    const end = i + 1 < headers.length ? headers[i + 1]!.index : criteriaText.length;
    const block = criteriaText.slice(start, end);
    const confidence = block.match(CONFIDENCE_RE);

    return {
      // Both groups are required by CRITERION_HEADER_RE, so they are captured on a match.
      id: header[1]!.toUpperCase(),
      criterion: clean(header[2] ?? ""),
      verdict: normaliseVerdict(field(block, "Veredicto:")),
      evidence: field(block, "Evidencia:"),
      confidence: confidence ? Number(confidence[1]) : null,
    };
  });

  const coverageBlock = coverageMatch?.index !== undefined ? verdict.slice(coverageMatch.index) : null;
  return {
    criteria,
    coverage:
      coverageBlock === null
        ? null
        : {
            coverage: normaliseCoverage(field(coverageBlock, "Cobertura:")),
            missing: field(coverageBlock, "Faltante:"),
            outOfScope: field(coverageBlock, "Fuera de alcance:"),
            summary: field(coverageBlock, "Resumen:"),
            // Absent reads as relevant, which is safe in both directions: an answer written before
            // this field existed keeps its meaning, and a model that forgot the line does not have
            // its verdict discarded. Only an explicit "no corresponde" disowns the ticket.
            relevant: !field(coverageBlock, "Relevancia:").toLowerCase().startsWith("no corresponde"),
            relevance: field(coverageBlock, "Relevancia:"),
          },
  };
}

const CRITERION_VERDICTS: readonly string[] = ["cumple", "no cumple", "parcial", "no verificable"];
const COVERAGE_VERDICTS: readonly string[] = ["completa", "incompleta", "no verificable"];

/**
 * Rebuilds a stored review into the shape the live parser produces, so one renderer serves both.
 *
 * <b>The widening happens here and nowhere else.</b> Those columns are `TEXT`, and a row written by
 * an older parser could hold any word at all; the four literals are the contract, so this is the
 * boundary that enforces them. Anything unreadable becomes `no verificable` for the same reason the
 * parser defaults that way — a verdict nobody can read is not evidence that a criterion was met.
 */
export function ticketVerdictFromStored(review: {
  criteria: { id: string; criterion: string; verdict: string; evidence: string; confidence: number | null }[];
  coverage: {
    coverage: string;
    missing: string;
    out_of_scope: string;
    summary: string;
    relevant?: boolean;
    relevance?: string;
  } | null;
}): ParsedTicketVerdict {
  return {
    criteria: review.criteria.map((c) => ({
      id: c.id,
      criterion: c.criterion,
      verdict: (CRITERION_VERDICTS.includes(c.verdict) ? c.verdict : "no verificable") as CriterionVerdict,
      evidence: c.evidence,
      confidence: c.confidence,
    })),
    coverage: review.coverage
      ? {
          coverage: (COVERAGE_VERDICTS.includes(review.coverage.coverage)
            ? review.coverage.coverage
            : "no verificable") as CoverageVerdict,
          missing: review.coverage.missing,
          outOfScope: review.coverage.out_of_scope,
          summary: review.coverage.summary,
          // A row stored before the relevance question existed says nothing about it, and silence
          // is not a disavowal: it reads as relevant, like a live answer that omitted the line.
          relevant: review.coverage.relevant ?? true,
          relevance: review.coverage.relevance ?? "",
        }
      : null,
  };
}

/** How many criteria got each verdict — what the section header shows at a glance. */
export function countVerdicts(criteria: TicketCriterionVerdict[]): Record<CriterionVerdict, number> {
  const counts: Record<CriterionVerdict, number> = {
    cumple: 0,
    "no cumple": 0,
    parcial: 0,
    "no verificable": 0,
  };
  for (const c of criteria) counts[c.verdict]++;
  return counts;
}
