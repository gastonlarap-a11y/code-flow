import { describe, expect, test } from "vitest";
import {
  buttonStyle,
  CONTROL_SIZES,
  CONTROL_VARIANTS,
  iconButtonStyle,
  MIN_ICON_PX,
  MIN_TARGET_PX,
} from "./controlStyles";

/**
 * These are the guarantees the redesign is actually built on, so they are asserted rather than
 * reviewed. Each one failed somewhere in the app before this module existed.
 */
describe("control styles", () => {
  const every = CONTROL_VARIANTS.flatMap((variant) =>
    CONTROL_SIZES.map((size) => ({ variant, size, label: `${variant}/${size}` })),
  );

  test.each(every)("$label meets the hit-target floor for its zone", ({ variant, size }) => {
    expect(buttonStyle(variant, size).targetPx).toBeGreaterThanOrEqual(MIN_TARGET_PX[size]);
    expect(iconButtonStyle(variant, size).targetPx).toBeGreaterThanOrEqual(MIN_TARGET_PX[size]);
  });

  test.each(every)("$label never renders an icon below the floor", ({ variant, size }) => {
    expect(buttonStyle(variant, size).iconSize).toBeGreaterThanOrEqual(MIN_ICON_PX);
    expect(iconButtonStyle(variant, size).iconSize).toBeGreaterThanOrEqual(MIN_ICON_PX);
  });

  test.each(every)("$label is focusable and transitions", ({ variant, size }) => {
    for (const style of [buttonStyle(variant, size), iconButtonStyle(variant, size)]) {
      expect(style.className).toContain("cf-focusable");
      expect(style.className).toContain("cf-interactive");
    }
  });

  // The whole point of the type scale is that no component picks a pixel value again.
  test.each(every)("$label uses a type-scale step, not an arbitrary size", ({ variant, size }) => {
    expect(buttonStyle(variant, size).className).not.toMatch(/text-\[\d/);
    expect(buttonStyle(variant, size).className).toMatch(/\btext-(badge|ui|body|relaxed|title)\b/);
  });

  // An icon-only button has no text to size, so it must not carry a type step either.
  test.each(every)("$label icon-only box carries no text step", ({ variant, size }) => {
    expect(iconButtonStyle(variant, size).className).not.toMatch(/\btext-(badge|ui|body|relaxed)\b/);
  });

  test("the dense step is smaller than the roomy one, in both shapes", () => {
    expect(buttonStyle("primary", "sm").targetPx).toBeLessThan(buttonStyle("primary", "md").targetPx);
    expect(iconButtonStyle("ghost", "sm").targetPx).toBeLessThan(
      iconButtonStyle("ghost", "md").targetPx,
    );
  });

  test("disabled is one opacity everywhere, not 40 in some files and 50 in others", () => {
    const opacities = new Set(
      every.flatMap(({ variant, size }) =>
        [buttonStyle(variant, size), iconButtonStyle(variant, size)].map(
          (s) => /disabled:opacity-(\d+)/.exec(s.className)?.[1],
        ),
      ),
    );
    expect(opacities).toEqual(new Set(["50"]));
  });

  test("only the primary variant paints the accent as a background", () => {
    for (const size of CONTROL_SIZES) {
      expect(buttonStyle("primary", size).className).toContain("bg-[var(--cf-accent-solid)]");
      for (const variant of CONTROL_VARIANTS.filter((v) => v !== "primary")) {
        expect(buttonStyle(variant, size).className).not.toContain("bg-[var(--cf-accent");
      }
    }
  });

  /**
   * The fill and its foreground are one decision, and `--cf-accent` is not part of it. White on the
   * ink accent measured 1.67–4.47:1 across the eight options; the pair `accentStore.ts` stamps is
   * what makes the primary button legible, and pairing `--cf-accent-solid` with a hardcoded `white`
   * would put half of that failure straight back.
   */
  test("the primary fill and its foreground come from the same pair", () => {
    for (const size of CONTROL_SIZES) {
      const { className } = buttonStyle("primary", size);
      expect(className).toContain("text-[var(--cf-accent-on-solid)]");
      expect(className).not.toContain("text-white");
    }
  });

  test("danger carries the danger token so severity is never colour-by-accident", () => {
    for (const size of CONTROL_SIZES) {
      expect(buttonStyle("danger", size).className).toContain("var(--cf-danger)");
      expect(iconButtonStyle("danger", size).className).toContain("var(--cf-danger)");
    }
  });
});
