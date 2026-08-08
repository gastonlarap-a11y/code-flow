import { describe, expect, test } from "vitest";

/**
 * `XLANG-015`'s renderer half: the difference between "nothing to analyse" and "the analysis
 * failed" is a prefix, and the two render as opposite things — a calm empty state or a red banner
 * carrying a sentinel no user can read.
 */

import { isRefusal, LEGACY_NOTHING_TO_ANALYZE, NOTHING_TO_ANALYZE_PREFIX } from "./analyzeRefusal";

const errored = (message: string) => ({ status: "error", error: { message } });

describe("recognising the refusal", () => {
  test("the sidecar's marker, with the sentence it carries", () => {
    expect(isRefusal(errored(`${NOTHING_TO_ANALYZE_PREFIX}${LEGACY_NOTHING_TO_ANALYZE}`))).toBe(true);
  });

  test("the bare sentence rows written before the marker existed still carry", () => {
    expect(isRefusal(errored(LEGACY_NOTHING_TO_ANALYZE))).toBe(true);
  });

  /**
   * The regression this module exists for: `String(new Error(…))` prepends `"Error: "`, which moved
   * the marker off the start of the message and turned the empty state into a red banner reading
   * `Error: NOTHING_TO_ANALYZE: …`. `jobsStore` now files `error.message`, so the marker stays
   * anchored — if this ever passes, the normalisation upstream has been lost.
   */
  test("a message the transport prefixed is not recognised — the fix belongs upstream", () => {
    expect(isRefusal(errored(`Error: ${NOTHING_TO_ANALYZE_PREFIX}whatever`))).toBe(false);
  });

  test("a genuine failure is not a refusal", () => {
    expect(isRefusal(errored("claude exited with an error (127)"))).toBe(false);
  });

  test("a job that did not fail is not a refusal, whatever it says", () => {
    expect(isRefusal({ status: "done", error: null })).toBe(false);
    expect(isRefusal({ status: "running", error: null })).toBe(false);
    // A cancelled run keeps its own state; it must never read as "nothing to analyse".
    expect(isRefusal({ status: "cancelled", error: null })).toBe(false);
  });

  test("an errored job with no error payload is not a refusal", () => {
    expect(isRefusal({ status: "error", error: null })).toBe(false);
  });

  // The wording is not the contract; the prefix is.
  test("a message that merely mentions having nothing to analyse is not one", () => {
    expect(isRefusal(errored("I found nothing to analyse in this diff"))).toBe(false);
  });
});
