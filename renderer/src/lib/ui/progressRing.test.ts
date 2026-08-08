import { describe, expect, it } from "vitest";
import { ringGeometry } from "./progressRing";

const R = 10;
const CIRCUMFERENCE = 2 * Math.PI * R;

describe("ringGeometry", () => {
  it("hides the whole circle at the start of a countdown", () => {
    expect(ringGeometry(R, 60, 60).dashOffset).toBeCloseTo(CIRCUMFERENCE);
  });

  it("draws the whole circle when the countdown reaches zero", () => {
    expect(ringGeometry(R, 0, 60).dashOffset).toBeCloseTo(0);
  });

  it("draws half of it halfway through", () => {
    expect(ringGeometry(R, 30, 60).dashOffset).toBeCloseTo(CIRCUMFERENCE / 2);
  });

  // Auto-fetch off. Also the divisor, so this guard is load-bearing beyond the visuals.
  it("draws nothing when there is no interval", () => {
    const { dashArray, dashOffset } = ringGeometry(R, 0, 0);
    expect(dashOffset).toBeCloseTo(dashArray);
  });

  // The running countdown keeps its old value until the next tick, so right after the user lowers
  // the interval `remaining` is briefly larger than `total`. Unclamped that is a negative offset,
  // which draws an arc going the wrong way round.
  it("never draws backwards when the interval was just shortened", () => {
    const { dashArray, dashOffset } = ringGeometry(R, 300, 60);
    expect(dashOffset).toBeCloseTo(dashArray);
    expect(dashOffset).toBeGreaterThanOrEqual(0);
  });

  it("never overshoots the circumference", () => {
    for (const remaining of [-5, 0, 1, 59, 60, 61]) {
      const { dashArray, dashOffset } = ringGeometry(R, remaining, 60);
      expect(dashOffset).toBeGreaterThanOrEqual(0);
      expect(dashOffset).toBeLessThanOrEqual(dashArray);
    }
  });
});
