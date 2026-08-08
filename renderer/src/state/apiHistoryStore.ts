/**
 * Request history for the API client. Workspace-scoped like its siblings; the retention cap
 * (`historyLimit`) belongs to `apiSettingsStore` and is read at insert time, not copied here.
 */

import { create } from "zustand";
import { apiAddHistory, apiClearHistory, apiDeleteHistory, apiListHistory } from "../lib/ipc/apiCommands";
import { guarded } from "./apiShared";
import { useApiSettingsStore } from "./apiSettingsStore";
import type { ApiHistoryEntry } from "../types/api";

interface ApiHistoryState {
  workspaceId: string | null;
  history: ApiHistoryEntry[];
  hydrate: (workspaceId: string, history: ApiHistoryEntry[]) => void;
  reset: (workspaceId: string | null) => void;
  reloadHistory: () => Promise<void>;
  addHistory: (entry: ApiHistoryEntry) => Promise<void>;
  deleteHistory: (id: string) => Promise<void>;
  clearHistory: () => Promise<void>;
}

export const useApiHistoryStore = create<ApiHistoryState>((set, get) => ({
  workspaceId: null,
  history: [],

  hydrate: (workspaceId, history) => set({ workspaceId, history }),
  reset: (workspaceId) => set({ workspaceId, history: [] }),

  reloadHistory: async () => {
    const workspaceId = get().workspaceId;
    if (workspaceId === null) return;
    const limit = useApiSettingsStore.getState().settings.historyLimit;
    set({ history: await apiListHistory(workspaceId, limit) });
  },

  addHistory: async (entry) => {
    await guarded(async () => {
      await apiAddHistory(entry);
      const limit = useApiSettingsStore.getState().settings.historyLimit;
      set((s) => ({ history: [entry, ...s.history].slice(0, limit) }));
    });
  },

  deleteHistory: async (id) => {
    await guarded(async () => {
      await apiDeleteHistory(id);
      set((s) => ({ history: s.history.filter((h) => h.id !== id) }));
    });
  },

  clearHistory: async () => {
    const workspaceId = get().workspaceId;
    if (workspaceId === null) return;
    await guarded(async () => {
      await apiClearHistory(workspaceId);
      set({ history: [] });
    });
  },
}));
