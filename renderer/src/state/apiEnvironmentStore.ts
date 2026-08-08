/**
 * Environments and their variables, plus which one is active.
 *
 * `setVariable` lives here even though one of its scopes is a collection: two of the three
 * scopes it resolves ("environment", "global") are environment rows, and the collection branch
 * is one cross-store call into `apiTreeStore` — the store that owns collection rows.
 */

import { create } from "zustand";
import {
  apiCreateEnvironment,
  apiDeleteEnvironment,
  apiDuplicateEnvironment,
  apiListEnvironments,
  apiUpdateEnvironment,
} from "../lib/ipc/apiCommands";
import { setSetting } from "../lib/ipc/commands";
import { guarded, newId } from "./apiShared";
import { useApiTreeStore } from "./apiTreeStore";
import { parseVariables } from "../lib/api/authChain";
import type { ApiEnvironment, ApiVariable, VariableScope } from "../types/api";

/**
 * Which environment is selected *is* content, so it is stored per workspace — a shared key would
 * put another workspace's selection back on screen right after the switch that was supposed to
 * leave it behind.
 */
export const activeEnvironmentKey = (workspaceId: string) => `api_active_environment:${workspaceId}`;

interface ApiEnvironmentState {
  workspaceId: string | null;
  environments: ApiEnvironment[];
  /** `null` = "No environment"; the Globals row is always in scope and is never this id. */
  activeEnvironmentId: string | null;

  /** `rawActiveId` is the persisted blob as read; a stale id (deleted environment) drops to null. */
  hydrate: (workspaceId: string, environments: ApiEnvironment[], rawActiveId: string | null) => void;
  reset: (workspaceId: string | null) => void;
  reloadEnvironments: () => Promise<void>;

  createEnvironment: (name: string) => Promise<ApiEnvironment | null>;
  updateEnvironment: (environment: ApiEnvironment) => Promise<void>;
  deleteEnvironment: (id: string) => Promise<void>;
  duplicateEnvironment: (id: string) => Promise<void>;
  setActiveEnvironment: (id: string | null) => void;
  /** Writes one variable's `currentValue`, creating the row if the scope doesn't define it yet. */
  setVariable: (scope: VariableScope, key: string, value: string, collectionId?: string | null) => Promise<void>;
}

export const useApiEnvironmentStore = create<ApiEnvironmentState>((set, get) => ({
  workspaceId: null,
  environments: [],
  activeEnvironmentId: null,

  hydrate: (workspaceId, environments, rawActiveId) =>
    set({
      workspaceId,
      environments,
      activeEnvironmentId:
        rawActiveId && environments.some((e) => e.id === rawActiveId) ? rawActiveId : null,
    }),

  reset: (workspaceId) => set({ workspaceId, environments: [], activeEnvironmentId: null }),

  reloadEnvironments: async () => {
    const workspaceId = get().workspaceId;
    if (workspaceId === null) return;
    set({ environments: await apiListEnvironments(workspaceId) });
  },

  createEnvironment: async (name) => {
    const workspaceId = get().workspaceId;
    if (workspaceId === null) return null;
    return guarded(async () => {
      const environment = await apiCreateEnvironment(workspaceId, name);
      set((s) => ({ environments: [...s.environments, environment] }));
      return environment;
    });
  },

  updateEnvironment: async (environment) => {
    await guarded(async () => {
      await apiUpdateEnvironment(environment);
      set((s) => ({
        environments: s.environments.map((e) => (e.id === environment.id ? environment : e)),
      }));
    });
  },

  deleteEnvironment: async (id) => {
    await guarded(async () => {
      await apiDeleteEnvironment(id);
      set((s) => ({ environments: s.environments.filter((e) => e.id !== id) }));
      if (get().activeEnvironmentId === id) get().setActiveEnvironment(null);
    });
  },

  duplicateEnvironment: async (id) => {
    await guarded(async () => {
      const copy = await apiDuplicateEnvironment(id);
      set((s) => ({ environments: [...s.environments, copy] }));
    });
  },

  setActiveEnvironment: (id) => {
    const workspaceId = get().workspaceId;
    set({ activeEnvironmentId: id });
    if (workspaceId === null) return;
    void setSetting(activeEnvironmentKey(workspaceId), id ?? "").catch(() => {});
  },

  setVariable: async (scope, key, value, collectionId) => {
    if (scope === "collection") {
      const tree = useApiTreeStore.getState();
      const collection = tree.collections.find((c) => c.id === collectionId);
      if (!collection) return;
      const variables = upsertVariable(parseVariables(collection.variables), key, value);
      await tree.updateCollection({ ...collection, variables: JSON.stringify(variables) });
      return;
    }
    const { environments, activeEnvironmentId } = get();
    const target =
      scope === "global"
        ? environments.find((e) => e.is_global)
        : environments.find((e) => e.id === activeEnvironmentId);
    if (!target) return;
    const variables = upsertVariable(parseVariables(target.variables), key, value);
    await get().updateEnvironment({ ...target, variables: JSON.stringify(variables) });
  },
}));

function upsertVariable(variables: ApiVariable[], key: string, value: string): ApiVariable[] {
  const index = variables.findIndex((variable) => variable.key === key);
  if (index < 0) {
    return [
      ...variables,
      {
        id: newId(),
        key,
        initialValue: "",
        currentValue: value,
        secret: false,
        enabled: true,
        description: "",
      },
    ];
  }
  const next = [...variables];
  // `index` was found via `findIndex` above and `next` is a same-length copy of `variables`, so
  // `next[index]` is always defined here.
  next[index] = { ...next[index]!, currentValue: value, enabled: true };
  return next;
}
