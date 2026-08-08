import { create } from "zustand";
import { getSetting, setSetting } from "../lib/ipc/commands";

export interface AccentOption {
  id: string;
  label: string;
  /** The accent as *ink*: text, borders, the active pill. Never a fill behind text. */
  light: string;
  dark: string;
  /**
   * The accent as a *fill* in the light theme — one step darker (700 rather than 500), because white
   * on the ink shade measures 2.43–4.70:1 and only two of the eight options clear AA.
   *
   * The dark theme needs no second shade: its ink shade is already the lighter 400, so the fill stays
   * `dark` and the foreground flips to `--cf-accent-on-solid` instead. `accentStore.test.ts` asserts
   * both directions on every option.
   */
  solid: string;
}

// Curated, not freeform: each pairs a light-theme shade with a lighter dark-theme shade
// of the same hue, following the same 500/400 pattern already used for the default indigo,
// so every option keeps solid contrast against both --cf-bg values.
//
// The shades were Tailwind's until the 2.0 palette pass raised their chroma by 15% in OKLCH while
// holding lightness. Holding L is the whole trick: WCAG luminance tracks it closely, so the ratios
// below barely move (the ink shade's worst case, `cyan`, is 2.43:1 before and after) while the
// colour itself gets more saturated. They are stored as hex rather than `oklch()` because
// `lib/ui/contrast.ts` parses hex, and it is the arbiter these values have to answer to — emitting
// OKLCH would mean rewriting the measurement to make the palette prettier.
//
// The gain is deliberately uneven, and it is a fact about sRGB rather than a choice: `cyan`, `teal`
// and `amber` were already within 3% of the gamut boundary at their lightness, so there was almost
// no chroma left to give them. `indigo`, `blue` and `purple` had room and took the full 15%.
export const ACCENT_OPTIONS: AccentOption[] = [
  { id: "indigo", label: "Indigo", light: "#6260ff", dark: "#808aff", solid: "#4429db" },
  { id: "blue", label: "Blue", light: "#3280ff", dark: "#5ba5ff", solid: "#1245ea" },
  { id: "cyan", label: "Cyan", light: "#06b6d4", dark: "#07d4ef", solid: "#017491" },
  { id: "teal", label: "Teal", light: "#039488", dark: "#02d5bf", solid: "#02766e" },
  { id: "green", label: "Green", light: "#03a447", dark: "#11e275", solid: "#01813a" },
  { id: "amber", label: "Amber", light: "#d97703", dark: "#fdbe04", solid: "#b55202" },
  { id: "rose", label: "Rose", light: "#e60145", dark: "#ff6c83", solid: "#c1013a" },
  { id: "purple", label: "Purple", light: "#980afb", dark: "#c182ff", solid: "#8101d9" },
];

/**
 * What sits *on* a solid accent fill, per theme. Uniform rather than per-option: in the light theme
 * every `solid` is dark enough for white, and in the dark theme every accent is light enough for the
 * near-black. This is the app's background ink, not pure black, so the button reads as part of the
 * theme rather than as a hole in it.
 */
export const ACCENT_ON_SOLID = { light: "#ffffff", dark: "#16161d" } as const;

const KEY = "accent_color";
const DEFAULT_ID = "indigo";

function findOption(id: string): AccentOption {
  // ACCENT_OPTIONS is a non-empty const array, so it always has a first entry.
  return ACCENT_OPTIONS.find((o) => o.id === id) ?? ACCENT_OPTIONS[0]!;
}

interface AccentState {
  accentId: string;
  init: () => Promise<void>;
  setAccent: (id: string, resolvedTheme: "light" | "dark") => Promise<void>;
  apply: (resolvedTheme: "light" | "dark") => void;
}

export const useAccentStore = create<AccentState>((set, get) => ({
  accentId: DEFAULT_ID,

  init: async () => {
    const stored = await getSetting(KEY).catch(() => null);
    if (stored && ACCENT_OPTIONS.some((o) => o.id === stored)) {
      set({ accentId: stored });
    }
  },

  setAccent: async (id, resolvedTheme) => {
    set({ accentId: id });
    get().apply(resolvedTheme);
    await setSetting(KEY, id);
  },

  apply: (resolvedTheme) => {
    const option = findOption(get().accentId);
    const dark = resolvedTheme === "dark";
    const style = document.documentElement.style;
    style.setProperty("--cf-accent", dark ? option.dark : option.light);
    // The fill and its foreground travel together: setting one without the other is how a button
    // ends up with white text on a pale accent, which is the state this pair exists to end.
    style.setProperty("--cf-accent-solid", dark ? option.dark : option.solid);
    style.setProperty("--cf-accent-on-solid", dark ? ACCENT_ON_SOLID.dark : ACCENT_ON_SOLID.light);
  },
}));
