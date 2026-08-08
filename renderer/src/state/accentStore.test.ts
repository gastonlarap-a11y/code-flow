import { describe, expect, test } from "vitest";
import { ACCENT_OPTIONS, ACCENT_ON_SOLID } from "./accentStore";
import { AA_CONTRAST, contrastRatio } from "../lib/ui/contrast";

/**
 * The accent palette, held to the contrast it is used at *as a fill*.
 *
 * `Button variant="primary"` paints `--cf-accent-solid` and writes `--cf-accent-on-solid` on it. Both
 * come from this list, so a ninth option added without a `solid` shade — or a "nicer" shade swapped
 * into an existing one — would silently reintroduce the failure §II.7 found: white on the ink accent
 * measured 2.43:1 at worst, against a 4.5:1 floor.
 *
 * The accent *as ink* is deliberately not asserted here. Six of the eight light shades measure
 * 2.43–4.47:1 on white, and raising them means changing the colour the user picked, not a token
 * behind it — a visual decision recorded as an open item in `docs/UX-REDESIGN.md` §II.7 rather than
 * made silently by a test. The dark shades all clear AA (5.54–9.90:1); it is only the light theme.
 */
describe("accent palette", () => {
  test.each(ACCENT_OPTIONS)("$label reads on a solid fill in both themes", (option) => {
    expect(contrastRatio(option.solid, ACCENT_ON_SOLID.light)).toBeGreaterThanOrEqual(AA_CONTRAST);
    // The dark theme has no second shade: the ink accent *is* the fill, flipped to dark ink.
    expect(contrastRatio(option.dark, ACCENT_ON_SOLID.dark)).toBeGreaterThanOrEqual(AA_CONTRAST);
  });

  test("every option is distinct, so the picker never shows the same colour twice", () => {
    expect(new Set(ACCENT_OPTIONS.map((o) => o.light)).size).toBe(ACCENT_OPTIONS.length);
    expect(new Set(ACCENT_OPTIONS.map((o) => o.id)).size).toBe(ACCENT_OPTIONS.length);
  });
});
