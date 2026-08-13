import { create } from "zustand";
import { getSetting, setSetting } from "../lib/ipc/commands";

const KEY = "auto_fetch_interval_seconds";
const SECRET_SCAN_KEY = "secret_scan_enabled";
const CHANGES_VIEW_KEY = "changes_view_mode";
export const MIN_AUTO_FETCH_SECONDS = 10;

export type ChangesViewMode = "list" | "tree";

interface PreferencesState {
  /** 0 means auto-fetch is disabled. */
  autoFetchSeconds: number;
  /** Whether the pre-commit secret scanner runs before each commit. Defaults to on. */
  secretScanEnabled: boolean;
  /**
   * How the Changes panel groups its files. Defaults to the directory tree.
   *
   * Held here rather than in the panel, where it was local state: the panel remounts whenever the
   * view changes, so a choice made on purpose lasted until the next click elsewhere.
   *
   * VS Code's own SCM view defaults to the flat list; this deliberately does not. The paths in the
   * repositories this is used on run four and five segments deep, and a flat list of them is a
   * column of near-identical prefixes — the tree, with single-child folders folded, is the shape
   * that reads. The toggle is still there for anyone who prefers the list, and it is remembered.
   */
  changesViewMode: ChangesViewMode;
  init: () => Promise<void>;
  setAutoFetchSeconds: (seconds: number) => Promise<void>;
  setSecretScanEnabled: (enabled: boolean) => Promise<void>;
  setChangesViewMode: (mode: ChangesViewMode) => Promise<void>;
}

function clamp(seconds: number): number {
  if (!Number.isFinite(seconds) || seconds <= 0) return 0;
  return Math.max(MIN_AUTO_FETCH_SECONDS, Math.round(seconds));
}

export const usePreferencesStore = create<PreferencesState>((set) => ({
  autoFetchSeconds: 0,
  secretScanEnabled: true,
  changesViewMode: "tree",

  init: async () => {
    const [raw, scanRaw, viewRaw] = await Promise.all([
      getSetting(KEY).catch(() => null),
      getSetting(SECRET_SCAN_KEY).catch(() => null),
      getSetting(CHANGES_VIEW_KEY).catch(() => null),
    ]);
    set({
      autoFetchSeconds: raw ? clamp(Number(raw)) : 0,
      // Unset (first run) defaults to enabled — the gate is opt-out, not opt-in.
      secretScanEnabled: scanRaw === null ? true : scanRaw === "true",
      // Only an explicit `list` is the flat one. Unset — a first run — gets the tree.
      changesViewMode: viewRaw === "list" ? "list" : "tree",
    });
  },

  setAutoFetchSeconds: async (seconds) => {
    const value = clamp(seconds);
    set({ autoFetchSeconds: value });
    await setSetting(KEY, String(value));
  },

  setSecretScanEnabled: async (enabled) => {
    set({ secretScanEnabled: enabled });
    await setSetting(SECRET_SCAN_KEY, String(enabled));
  },

  setChangesViewMode: async (mode) => {
    set({ changesViewMode: mode });
    await setSetting(CHANGES_VIEW_KEY, mode);
  },
}));
