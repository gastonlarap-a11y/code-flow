import { ACCENT_OPTIONS } from "../../state/accentStore";

/**
 * The palette a repository's colour is drawn from — the same eight `accentStore` curates.
 *
 * Reused rather than re-listed: `accentStore.test.ts` already measures every one of these against
 * `lib/ui/contrast.ts` at 4.5:1 as ink, which is exactly how a repository's colour is used (a
 * tinted glyph). A ninth hue invented here would be the one nobody had measured.
 */
export const PROJECT_COLORS: readonly string[] = ACCENT_OPTIONS.map((option) => option.light);

/**
 * The indigo every repository used to be created with.
 *
 * It is recognisable as "never chosen" precisely because it is **not** in the palette — the picker's
 * indigo is `#6260ff`. Nothing the swatch picker can produce equals this, so a repository still
 * carrying it was defaulted by the three creation sites that wrote the literal, never picked by
 * anybody. That is what makes recolouring these safe and recolouring everything else not.
 */
export const LEGACY_DEFAULT_COLOR = "#6366f1";

/** Whether a stored colour is the old hardcoded default rather than a choice. */
export function isLegacyDefault(color: string): boolean {
  return color.toLowerCase() === LEGACY_DEFAULT_COLOR;
}

/**
 * The colour a newly added repository should take.
 *
 * Every creation site used to write `#6366f1` by hand, so every repository in the sidebar was the
 * same indigo and the per-project picker in Settings had nothing to distinguish. Repositories are
 * told apart at a glance far more often than they are configured, so the default does the work.
 *
 * **Least-used wins, not next-in-sequence.** A counter would keep advancing past colours freed by
 * a removed repository and start repeating while some hues went unused; counting what is actually
 * in play keeps the spread even however repositories come and go. Ties break by palette order, so
 * the same set of existing colours always yields the same answer — a property the test rests on.
 */
export function nextProjectColor(existing: readonly string[]): string {
  const used = new Map<string, number>(PROJECT_COLORS.map((colour) => [colour, 0]));

  for (const colour of existing) {
    // A colour outside the palette is one the user picked before the palette changed, or by hand.
    // It is left out of the count rather than added: it cannot be handed out again from here.
    const seen = used.get(colour.toLowerCase());
    if (seen !== undefined) used.set(colour.toLowerCase(), seen + 1);
  }

  let best = PROJECT_COLORS[0]!;
  let fewest = Infinity;
  for (const colour of PROJECT_COLORS) {
    const count = used.get(colour) ?? 0;
    if (count < fewest) {
      fewest = count;
      best = colour;
    }
  }

  return best;
}
