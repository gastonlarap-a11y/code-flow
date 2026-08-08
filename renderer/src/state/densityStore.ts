import { create } from "zustand";
import { getSetting, setSetting } from "../lib/ipc/commands";
import {
  DEFAULT_DENSITY,
  DENSITY_VAR,
  densityPx,
  parseDensity,
  type TreeDensity,
} from "../lib/ui/density";

const KEY = "tree_density";

interface DensityState {
  density: TreeDensity;
  /** Row height in CSS pixels — what the virtualizers seed `estimateSize` with. */
  rowHeight: number;
  init: () => Promise<void>;
  setDensity: (density: TreeDensity) => Promise<void>;
}

/**
 * Its own store rather than a field on `preferencesStore`, for the same reason `themeStore` is
 * separate: this one writes to the document. The height travels to the rows as a CSS custom
 * property, so a change repaints both trees without either of them re-rendering, and nothing has to
 * thread a number down through `FileTree` → `TreeNode`.
 */
export const useDensityStore = create<DensityState>((set) => ({
  density: DEFAULT_DENSITY,
  rowHeight: densityPx(DEFAULT_DENSITY),

  init: async () => {
    const raw = await getSetting(KEY).catch(() => null);
    apply(parseDensity(raw), set);
  },

  setDensity: async (density) => {
    apply(density, set);
    await setSetting(KEY, density);
  },
}));

function apply(density: TreeDensity, set: (partial: Partial<DensityState>) => void): void {
  const rowHeight = densityPx(density);
  document.documentElement.style.setProperty(DENSITY_VAR, `${rowHeight}px`);
  set({ density, rowHeight });
}
