import { describe, expect, test } from "vitest";
import {
  DEFAULT_DENSITY,
  DENSITIES,
  DENSITY_PX,
  densityPx,
  parseDensity,
  type TreeDensity,
} from "./density";
import { MIN_TARGET_PX } from "./controlStyles";

describe("tree density", () => {
  test("every step clears the WCAG pointer-target floor", () => {
    for (const density of DENSITIES) {
      expect(densityPx(density)).toBeGreaterThanOrEqual(MIN_TARGET_PX.sm);
    }
  });

  test("the steps are ordered densest first, with no duplicates", () => {
    const heights = DENSITIES.map(densityPx);
    expect(heights).toEqual([...heights].sort((a, b) => a - b));
    expect(new Set(heights).size).toBe(heights.length);
  });

  // The reason `cozy` is the default: a row-level control is `MIN_TARGET_PX.sm` tall, so at the
  // densest step it fills the row and its focus ring has nowhere to go.
  test("the default leaves room a 24px row does not", () => {
    expect(densityPx(DEFAULT_DENSITY)).toBeGreaterThan(MIN_TARGET_PX.sm);
    expect(densityPx("compact")).toBe(MIN_TARGET_PX.sm);
  });

  test("parses the values it persists", () => {
    for (const density of DENSITIES) {
      expect(parseDensity(density)).toBe(density);
    }
  });

  // A setting can be absent on first run, or left over from a step that no longer exists.
  test("falls back instead of throwing on anything else", () => {
    expect(parseDensity(null)).toBe(DEFAULT_DENSITY);
    expect(parseDensity(undefined)).toBe(DEFAULT_DENSITY);
    expect(parseDensity("")).toBe(DEFAULT_DENSITY);
    expect(parseDensity("comfortable")).toBe(DEFAULT_DENSITY);
    expect(parseDensity("26")).toBe(DEFAULT_DENSITY);
  });

  test("the px table and the ordered list cover the same steps", () => {
    expect(Object.keys(DENSITY_PX).sort()).toEqual([...DENSITIES].sort());
  });

  test("the default is one of the steps", () => {
    expect(DENSITIES).toContain(DEFAULT_DENSITY satisfies TreeDensity);
  });
});

// The stylesheet's pre-boot copy of this height is checked against the default in
// `scripts/density-css.test.mjs` — it reads a file, and this half of the tree is browser code with
// no Node types, the same split that puts the i18n parity check over there.
