/**
 * Workspace lifecycle for the API client — the one store that knows how to load and tear down
 * all of them.
 *
 * The data itself lives in the domain stores (`apiTreeStore`, `apiEnvironmentStore`,
 * `apiHistoryStore`, `apiCookieStore`, `apiTabsStore`, `apiSettingsStore`); this store owns only
 * `workspaceId`/`loading` and the init/switch choreography, plus the imperative `get…` snapshot
 * helpers built on `lib/api/authChain.ts`.
 */

import { create } from "zustand";
import { apiListCookies, apiListEnvironments, apiListHistory, apiLoadTree } from "../lib/ipc/apiCommands";
import { getSetting } from "../lib/ipc/commands";
import { pushErrorToast } from "./toastStore";
import { useApiRuntimeStore } from "./apiRuntimeStore";
import { useApiModalStore } from "./apiModalStore";
import { useWorkspaceStore } from "./workspaceStore";
import { useApiSettingsStore } from "./apiSettingsStore";
import { useApiTreeStore } from "./apiTreeStore";
import { activeEnvironmentKey, useApiEnvironmentStore } from "./apiEnvironmentStore";
import { useApiHistoryStore } from "./apiHistoryStore";
import { useApiCookieStore } from "./apiCookieStore";
import { openTabsKey, useApiTabsStore } from "./apiTabsStore";
import { authChainForTab, buildVariableContext, effectiveAuthChain } from "../lib/api/authChain";
import type { AuthConfig } from "../types/api";
import type { VariableContext } from "../lib/api/variables";

interface ApiState {
  /** Whose data is in the stores right now; `null` until the first load. */
  workspaceId: string | null;
  loading: boolean;

  init: (workspaceId: string) => Promise<void>;
  /**
   * Points the whole API client at another workspace: tears the current one down (live
   * connections included), then loads the new one. A no-op when the workspace is unchanged.
   */
  setWorkspace: (workspaceId: string) => Promise<void>;
}

/**
 * The load in flight, and the workspace it belongs to.
 *
 * `init()` reloads the entire tree, so it must run once per workspace — but four entry points
 * can be the first to need the data (the API view, the API section of Settings, a
 * command-palette action that opens a request, and the workspace effect in `App`), and none of
 * them can know whether it got there first. Handing every caller the same promise is what keeps
 * that from becoming two concurrent loads; keying it by workspace is what keeps a *switch* from
 * being mistaken for one of those duplicate calls and ignored. A module-level latch is the only
 * place that outlives all four; a ref in any component would be re-created by StrictMode.
 */
let pendingLoad: { workspaceId: string; promise: Promise<void> } | null = null;

/** Resolves once the active workspace's tree, environments, history and cookies are in the stores. */
export function ensureApiStoreLoaded(): Promise<void> {
  const workspaceId = useWorkspaceStore.getState().activeWorkspaceId;
  // Nothing to scope the data to yet — `App` calls `setWorkspace` as soon as there is one.
  if (workspaceId === null) return Promise.resolve();
  useApiRuntimeStore.getState().init();
  return useApiStore.getState().setWorkspace(workspaceId);
}

export const useApiStore = create<ApiState>((set, get) => ({
  workspaceId: null,
  loading: false,

  init: async (workspaceId) => {
    // Set before the first await: everything that persists (the open tabs, the active
    // environment) keys off it, and those writes can land while the load is still running.
    set({ workspaceId, loading: true });
    try {
      const [settings, rawEnvironment, rawTabs] = await Promise.all([
        useApiSettingsStore.getState().load(),
        getSetting(activeEnvironmentKey(workspaceId)).catch(() => null),
        getSetting(openTabsKey(workspaceId)).catch(() => null),
      ]);

      const [tree, environments, history, cookies] = await Promise.all([
        apiLoadTree(workspaceId),
        apiListEnvironments(workspaceId),
        apiListHistory(workspaceId, settings.historyLimit),
        apiListCookies(workspaceId),
      ]);

      // Two switches in quick succession leave two loads in flight; the one whose workspace is
      // no longer selected must not be the one that gets to publish its data.
      if (get().workspaceId !== workspaceId) return;

      useApiTreeStore.getState().hydrate(workspaceId, tree.collections, tree.folders, tree.requests);
      useApiEnvironmentStore.getState().hydrate(workspaceId, environments, rawEnvironment);
      useApiHistoryStore.getState().hydrate(workspaceId, history);
      useApiCookieStore.getState().hydrate(workspaceId, cookies);
      useApiTabsStore.getState().hydrate(workspaceId, rawTabs);
    } catch (e) {
      pushErrorToast(String(e));
    } finally {
      if (get().workspaceId === workspaceId) set({ loading: false });
    }
  },

  setWorkspace: async (workspaceId) => {
    if (pendingLoad?.workspaceId === workspaceId) return pendingLoad.promise;

    const tabs = useApiTabsStore.getState();
    // Flush what the debounce still owes the outgoing workspace — and cancel it, because from
    // here on the tabs store persists under the incoming workspace's key.
    tabs.flushPersist();
    tabs.releaseAll();
    // The runner and the export sheet are opened against one collection id, and that collection
    // belongs to the workspace being left — staying open would leave them pointed at a row the
    // store is about to drop.
    useApiModalStore.getState().closeApiModal();
    // Cleared rather than left to be overwritten by `init`: for the length of the load the view
    // would otherwise still be showing the workspace the user just left. Each reset stamps the
    // incoming workspace so nothing persisted mid-load lands under the old scope.
    useApiTreeStore.getState().reset(workspaceId);
    useApiEnvironmentStore.getState().reset(workspaceId);
    useApiHistoryStore.getState().reset(workspaceId);
    useApiCookieStore.getState().reset(workspaceId);
    useApiTabsStore.getState().reset(workspaceId);

    const promise = get().init(workspaceId);
    pendingLoad = { workspaceId, promise };
    return promise;
  },
}));

// ---------------------------------------------------------------------------
// Scope assembly — imperative snapshots
// ---------------------------------------------------------------------------

// The walks live in `lib/api/authChain.ts` as pure functions; these read the stores for callers
// inside event handlers. They build a fresh object per call, so none of them can be a selector —
// reactive readers select the slices and rebuild in a `useMemo` keyed on them.

export function getVariableContext(collectionId: string | null): VariableContext {
  const { environments, activeEnvironmentId } = useApiEnvironmentStore.getState();
  const { collections } = useApiTreeStore.getState();
  return buildVariableContext(environments, collections, activeEnvironmentId, collectionId);
}

export function getEffectiveAuthChain(requestId: string): (AuthConfig | null)[] {
  const { requests, folders, collections } = useApiTreeStore.getState();
  return effectiveAuthChain(requests, folders, collections, requestId);
}

export function getAuthChainForTab(tabId: string): (AuthConfig | null)[] {
  const { openTabs } = useApiTabsStore.getState();
  const { folders, collections } = useApiTreeStore.getState();
  return authChainForTab(openTabs, folders, collections, tabId);
}
