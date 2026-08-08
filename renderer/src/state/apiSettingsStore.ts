/**
 * Transport configuration for the API client: timeouts, proxy, certificates, history limit.
 *
 * Deliberately **not** workspace-scoped — the settings blob is global, so unlike its sibling
 * stores this one has no `workspaceId` and survives a workspace switch untouched.
 */

import { create } from "zustand";
import { getSetting, setSetting } from "../lib/ipc/commands";
import { pushErrorToast } from "./toastStore";
import { parseJson } from "./apiShared";
import { defaultApiSettings } from "../types/api";
import type { ApiSettings } from "../types/api";

/** Global on purpose: timeouts, proxy and certificates are transport configuration, not content. */
const SETTINGS_KEY = "api_settings";

interface ApiSettingsState {
  settings: ApiSettings;
  /**
   * Reads the persisted blob and publishes it. Returns the merged value because the orchestrator
   * needs `historyLimit` before it can even request the history list.
   */
  load: () => Promise<ApiSettings>;
  updateSettings: (patch: Partial<ApiSettings>) => Promise<void>;
}

export const useApiSettingsStore = create<ApiSettingsState>((set, get) => ({
  settings: defaultApiSettings(),

  load: async () => {
    const raw = await getSetting(SETTINGS_KEY).catch(() => null);
    // Merged over the defaults rather than used as-is, so a field added in a later version
    // arrives populated on an install whose stored blob predates it.
    const settings = { ...defaultApiSettings(), ...parseJson<Partial<ApiSettings>>(raw, {}) };
    set({ settings });
    return settings;
  },

  updateSettings: async (patch) => {
    const settings = { ...get().settings, ...patch };
    set({ settings });
    await setSetting(SETTINGS_KEY, JSON.stringify(settings)).catch((e) => pushErrorToast(String(e)));
  },
}));
