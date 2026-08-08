/**
 * How tall a row is in the two virtualized trees, as a user preference.
 *
 * The redesign left this open deliberately, because the honest answer to "24 or 26?" is that it
 * depends on the person more than on the display. The instinct is to tie it to screen size, but CSS
 * pixels are logical units and the OS scale factor already handles that — and it lands the opposite
 * way round from the intuition: a 24px row is ~4.8mm on a 14" retina laptop viewed from ~52cm, and
 * ~4.4mm on an unscaled 32" 4K panel viewed from ~75cm, so the *big* screen is the smaller target.
 * Rather than guess, the three steps are offered and the choice persists.
 *
 * The height must be declared rather than fall out of padding and line-height. The rows previously
 * measured 23.5px as a side effect of `py-0.5` around 13px text, which is not a number anyone can
 * change on purpose.
 */

/** The three steps. More than three would be a slider, and a slider needs a reason. */
export type TreeDensity = "compact" | "cozy" | "roomy";

/**
 * Row height in CSS pixels.
 *
 * 24 is the WCAG 2.2 SC 2.5.8 floor for a pointer target and what the app shipped with; at that
 * height a 24px `RowActions` fills the row exactly and its 2px focus ring spills onto the
 * neighbours. 26 leaves a pixel of air on each side. 28 is the first height where the ring fits
 * outright, at the cost of about one row in six.
 */
export const DENSITY_PX = { compact: 24, cozy: 26, roomy: 28 } as const;

/** In display order, densest first. */
export const DENSITIES = ["compact", "cozy", "roomy"] as const satisfies readonly TreeDensity[];

/**
 * The middle step. Chosen over `compact` because the focus ring is new: every row-level control the
 * redesign introduces is 24px, and at a 24px row height there is nowhere for its ring to go.
 */
export const DEFAULT_DENSITY: TreeDensity = "cozy";

/** The CSS custom property the trees read their height from. */
export const DENSITY_VAR = "--cf-row-height";

/**
 * Reads a persisted value back. Settings come from SQLite as strings and may be absent (first run),
 * stale (a step that no longer exists), or hand-edited — all three fall back rather than throw,
 * because an unreadable preference should cost the user a default, not a broken file tree.
 */
export function parseDensity(raw: string | null | undefined): TreeDensity {
  return DENSITIES.find((d) => d === raw) ?? DEFAULT_DENSITY;
}

/** Row height for a density, in CSS pixels. */
export function densityPx(density: TreeDensity): number {
  return DENSITY_PX[density];
}
