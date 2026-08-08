/**
 * WCAG relative luminance and contrast ratio, so a palette choice can be asserted instead of eyeballed.
 *
 * This exists because running the §II.7 checklist found that `Button variant="primary"` — white on
 * `--cf-accent` — failed AA on six of the eight accent options in the light theme and on all eight in
 * the dark one, where the accents are deliberately the lighter 400-level shade. Nobody had measured
 * it, because there was nothing to measure it with. Now `accentStore.test.ts` does, on every option.
 *
 * The formulae are WCAG 2.x §1.4.3 verbatim; sRGB only, which is what every token in `index.css` is.
 */

/** One sRGB channel, 0–255, linearised. */
function channel(value: number): number {
  const c = value / 255;
  return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}

/** `#rgb` or `#rrggbb` to its three channels. Throws on anything else — a typo'd token is a bug. */
function parseHex(hex: string): [number, number, number] {
  const value = hex.trim().replace(/^#/, "");
  const full = value.length === 3 ? [...value].map((c) => c + c).join("") : value;
  if (!/^[0-9a-fA-F]{6}$/.test(full)) throw new Error(`not a hex colour: ${hex}`);
  return [0, 2, 4].map((i) => parseInt(full.slice(i, i + 2), 16)) as [number, number, number];
}

/** WCAG relative luminance, 0 (black) to 1 (white). */
export function luminance(hex: string): number {
  const [r, g, b] = parseHex(hex);
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}

/** Contrast ratio between two opaque colours, 1:1 to 21:1. Order does not matter. */
export function contrastRatio(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (hi + 0.05) / (lo + 0.05);
}

/** AA for body text: 4.5:1. The app's type scale tops out at 18px, so the large-text 3:1 never applies. */
export const AA_CONTRAST = 4.5;
