/**
 * "There was nothing uncommitted to analyse" — the one job outcome that is not a failure.
 *
 * Lives here rather than in `AnalyzeSection.tsx` because `.test.tsx` files are never collected —
 * `vite.config.ts`'s include glob only picks up `.test.ts` — and this is the predicate that decides
 * between a calm empty state and a red banner. It was wrong once already and nothing failed.
 */

/**
 * The sidecar's marker for "there was nothing uncommitted to analyse".
 *
 * A byte-level contract with `AiOperations.NothingToAnalyzePrefix`
 * (`docs/business-rules/13-cross-language-contracts.md`) — matched by text, like `STALE_REVIEW: `
 * in `prStore`. Paraphrasing either side compiles and turns an empty state back into a red banner.
 */
export const NOTHING_TO_ANALYZE_PREFIX = "NOTHING_TO_ANALYZE: ";

/**
 * The same refusal as it was filed *before* the marker existed.
 *
 * Rows written by earlier versions carry the bare Spanish sentence, and an install that has one
 * would otherwise keep seeing it as its latest analysis. Matched by text, once, here — not a new
 * contract: nothing writes this form any more.
 */
export const LEGACY_NOTHING_TO_ANALYZE = "No hay cambios sin commitear para analizar";

/** The shape this predicate needs from a job — structural, so `Job` stays out of `lib/`. */
export interface RefusableJob {
  status: string;
  error: { message: string } | null;
}

/**
 * Whether a job is a "there was nothing to analyse" refusal rather than an analysis.
 *
 * Both forms are anchored at the start of the message, which is only sound because `jobsStore`
 * files the error's own `message` rather than the stringified `Error` — `String(error)` prepends
 * `"Error: "`, and that alone is what used to leak the raw sentinel onto the screen.
 */
export function isRefusal(job: RefusableJob): boolean {
  if (job.status !== "error" || !job.error) return false;
  return (
    job.error.message.startsWith(NOTHING_TO_ANALYZE_PREFIX) ||
    job.error.message === LEGACY_NOTHING_TO_ANALYZE
  );
}
