import { describe, expect, test } from "vitest";
import { measuredRowHeight } from "./rowMeasurement";

const ROW = 24;

/** Only the two fields the decision reads, which is all a `ResizeObserverEntry` is used for here. */
const observed = (blockSize: number) =>
  ({ borderBoxSize: [{ blockSize, inlineSize: 0 }] }) as unknown as ResizeObserverEntry;

const element = (height: number) => ({ getBoundingClientRect: () => ({ height }) });

describe("measuredRowHeight", () => {
  test("the observer's box wins, sub-pixels included", () => {
    // Rounding here accumulates into a visibly wrong scroll height over a long tree.
    expect(measuredRowHeight(element(24), observed(23.5), ROW)).toBe(23.5);
  });

  test("without an observation it falls back to the element", () => {
    expect(measuredRowHeight(element(23.5), undefined, ROW)).toBe(23.5);
  });

  test("a row that measures nothing is a row nobody can see", () => {
    // The bug this exists for. A view hidden with `display: none` keeps its tree observed and every
    // row reports 0; those zeros used to reach the size cache and collapse the list on the way back,
    // taking the rows at the top — the directories — with it.
    expect(measuredRowHeight(element(0), observed(0), ROW)).toBe(ROW);
  });

  test("hidden with no observation is the same absence", () => {
    expect(measuredRowHeight(element(0), undefined, ROW)).toBe(ROW);
  });

  test("an observation of zero does not defer to a stale rect", () => {
    // The observer is the fresher of the two. If it says zero, the element's own rect saying
    // otherwise is the stale reading, not the truth.
    expect(measuredRowHeight(element(24), observed(0), ROW)).toBe(ROW);
  });
});
