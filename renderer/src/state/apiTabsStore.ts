/**
 * The builder's open tabs and their unsaved drafts.
 *
 * Persistence is per workspace under `api_open_tabs:<id>` (versioned `PersistedTabs`). Draft
 * edits arrive per keystroke, so writes are debounced; structural changes (open, close, focus,
 * save) write straight through — see `persistNow`/`schedulePersist`.
 */

import { create } from "zustand";
import { apiCreateRequest, apiStreamDisconnect, apiUpdateRequest } from "../lib/ipc/apiCommands";
import { setSetting } from "../lib/ipc/commands";
import { guarded, newId, parseJson } from "./apiShared";
import { translate } from "./languageStore";
import { useApiRuntimeStore } from "./apiRuntimeStore";
import { useApiTreeStore } from "./apiTreeStore";
import { parseSpec } from "../lib/api/authChain";
import { defaultRequestSpec } from "../types/api";
import type { ApiProtocol, ApiRequestRow, ApiRequestSpec } from "../types/api";

/**
 * Which requests are open *is* content, so tabs are stored per workspace — a shared key would
 * put another workspace's tabs back on screen right after the switch that was supposed to leave
 * them behind.
 */
export const openTabsKey = (workspaceId: string) => `api_open_tabs:${workspaceId}`;

/**
 * One editor tab in the builder. `requestId: null` is a scratch request — it exists only here
 * and in the persisted tab list until the user saves it into a collection, which is what makes
 * "type a URL, hit Send" work without creating anything.
 */
export interface ApiTab {
  id: string;
  requestId: string | null;
  draft: ApiRequestSpec;
  name: string;
  dirty: boolean;
  /** Where `saveTab` files a scratch request; carried so the save needs no extra argument. */
  collectionId: string | null;
  folderId: string | null;
}

/** Persisted shape of `api_open_tabs`. Versioned so a later change can be migrated, not guessed. */
interface PersistedTabs {
  version: 1;
  tabs: ApiTab[];
  activeTabId: string | null;
}

interface ApiTabsState {
  workspaceId: string | null;
  openTabs: ApiTab[];
  activeTabId: string | null;

  /** `rawTabs` is the persisted blob as read; anything unparseable restores as no tabs. */
  hydrate: (workspaceId: string, rawTabs: string | null) => void;
  /**
   * Clears the tabs and stamps the *incoming* workspace before its load starts, so a debounced
   * persist firing mid-switch writes the (empty) list under the new key instead of clobbering
   * the outgoing workspace's just-flushed tabs.
   */
  reset: (workspaceId: string | null) => void;
  /** Writes any pending debounced persist through immediately and cancels the timer. */
  flushPersist: () => void;
  /** Releases every tab's live connection and runtime buffers (workspace teardown). */
  releaseAll: () => void;

  openRequest: (requestId: string) => void;
  openScratchTab: (protocol?: ApiProtocol, target?: { collectionId: string; folderId: string | null }) => string;
  closeTab: (tabId: string) => void;
  setActiveTab: (tabId: string) => void;
  renameTab: (tabId: string, name: string) => void;
  updateDraft: (tabId: string, patch: Partial<ApiRequestSpec>) => void;
  saveTab: (
    tabId: string,
    target?: { collectionId: string; folderId: string | null },
  ) => Promise<ApiRequestRow | null>;

  /**
   * Turns the tabs of deleted requests back into scratch tabs instead of closing them: the
   * user's unsaved edits are in the draft, and silently discarding them on a delete elsewhere
   * in the tree is the kind of data loss nobody forgives.
   */
  detachRequests: (deletedRequestIds: string[]) => void;
}

export const useApiTabsStore = create<ApiTabsState>((set, get) => ({
  workspaceId: null,
  openTabs: [],
  activeTabId: null,

  hydrate: (workspaceId, rawTabs) => {
    const restored = parseJson<PersistedTabs | null>(rawTabs, null);
    const openTabs = restored?.version === 1 ? restored.tabs.map(rehydrateTab) : [];
    const activeTabId =
      openTabs.find((tab) => tab.id === restored?.activeTabId)?.id ?? openTabs[0]?.id ?? null;
    set({ workspaceId, openTabs, activeTabId });
  },

  reset: (workspaceId) => {
    set({ workspaceId, openTabs: [], activeTabId: null });
  },

  flushPersist: () => persistNow(get),

  releaseAll: () => {
    for (const tab of get().openTabs) releaseTab(tab.id);
  },

  openRequest: (requestId) => {
    const existing = get().openTabs.find((tab) => tab.requestId === requestId);
    if (existing) {
      get().setActiveTab(existing.id);
      return;
    }
    const row = useApiTreeStore.getState().requests.find((r) => r.id === requestId);
    if (!row) return;
    const tab: ApiTab = {
      id: newId(),
      requestId,
      draft: parseSpec(row),
      name: row.name,
      dirty: false,
      collectionId: row.collection_id,
      folderId: row.folder_id,
    };
    set((s) => ({ openTabs: [...s.openTabs, tab], activeTabId: tab.id }));
    persistNow(get);
  },

  openScratchTab: (protocol = "http", target) => {
    const tab: ApiTab = {
      id: newId(),
      requestId: null,
      draft: defaultRequestSpec(protocol),
      name: "",
      dirty: false,
      collectionId: target?.collectionId ?? null,
      folderId: target?.folderId ?? null,
    };
    set((s) => ({ openTabs: [...s.openTabs, tab], activeTabId: tab.id }));
    persistNow(get);
    return tab.id;
  },

  closeTab: (tabId) => {
    const index = get().openTabs.findIndex((tab) => tab.id === tabId);
    if (index < 0) return;
    const openTabs = get().openTabs.filter((tab) => tab.id !== tabId);
    // Focus the neighbour that visually takes the closed tab's place, browser-style.
    const successor = openTabs[Math.min(index, openTabs.length - 1)];
    set({
      openTabs,
      activeTabId: get().activeTabId === tabId ? (successor?.id ?? null) : get().activeTabId,
    });
    persistNow(get);
    releaseTab(tabId);
  },

  setActiveTab: (tabId) => {
    if (get().activeTabId === tabId) return;
    set({ activeTabId: tabId });
    persistNow(get);
  },

  renameTab: (tabId, name) => {
    set((s) => ({
      openTabs: s.openTabs.map((tab) => (tab.id === tabId ? { ...tab, name, dirty: true } : tab)),
    }));
    schedulePersist(get);
  },

  updateDraft: (tabId, patch) => {
    set((s) => ({
      openTabs: s.openTabs.map((tab) =>
        tab.id === tabId ? { ...tab, draft: { ...tab.draft, ...patch }, dirty: true } : tab,
      ),
    }));
    schedulePersist(get);
  },

  saveTab: async (tabId, target) => {
    const tab = get().openTabs.find((t) => t.id === tabId);
    if (!tab) return null;
    const spec = JSON.stringify(tab.draft);

    // The IPC write stays here rather than delegating to the tree store's own guarded actions:
    // a failed save must throw into *this* guard so the tab stays dirty; the tree just mirrors
    // the row afterwards via `upsertRequestRow`.
    return guarded(async () => {
      const tree = useApiTreeStore.getState();
      if (tab.requestId) {
        const row = tree.requests.find((r) => r.id === tab.requestId);
        if (!row) return null;
        const updated: ApiRequestRow = {
          ...row,
          name: tab.name || row.name,
          protocol: tab.draft.protocol,
          method: tab.draft.method,
          url: tab.draft.url,
          spec,
        };
        await apiUpdateRequest(updated);
        tree.upsertRequestRow(updated);
        set((s) => ({
          openTabs: s.openTabs.map((t) => (t.id === tabId ? { ...t, dirty: false } : t)),
        }));
        persistNow(get);
        return updated;
      }

      const collectionId = target?.collectionId ?? tab.collectionId;
      // A scratch tab with nowhere to go isn't an error: it's the UI's cue to open the
      // "Save to collection" picker and call back with a target.
      if (!collectionId) return null;
      const folderId = target ? target.folderId : tab.folderId;
      const created = await apiCreateRequest(
        collectionId,
        folderId,
        tab.name || translate("api.untitledRequest"),
        tab.draft.protocol,
        spec,
      );
      tree.upsertRequestRow(created);
      set((s) => ({
        openTabs: s.openTabs.map((t) =>
          t.id === tabId
            ? { ...t, requestId: created.id, name: created.name, dirty: false, collectionId, folderId }
            : t,
        ),
      }));
      persistNow(get);
      return created;
    });
  },

  detachRequests: (deletedRequestIds) => {
    const gone = new Set(deletedRequestIds);
    const affected = get().openTabs.some((tab) => tab.requestId !== null && gone.has(tab.requestId));
    if (!affected) return;
    set({
      openTabs: get().openTabs.map((tab) =>
        tab.requestId !== null && gone.has(tab.requestId)
          ? { ...tab, requestId: null, collectionId: null, folderId: null, dirty: true }
          : tab,
      ),
    });
    persistNow(get);
  },
}));

// ---------------------------------------------------------------------------
// Tab persistence
// ---------------------------------------------------------------------------

/**
 * Draft edits arrive per keystroke, and `api_open_tabs` carries the whole workbench — writing it
 * on every one would mean a SQLite round trip per character. Structural changes (open, close,
 * focus, save) write straight through; edits coalesce into a trailing write.
 */
const PERSIST_DEBOUNCE_MS = 600;
let persistTimer: ReturnType<typeof setTimeout> | null = null;

function persistNow(get: () => ApiTabsState) {
  if (persistTimer !== null) {
    clearTimeout(persistTimer);
    persistTimer = null;
  }
  const { workspaceId, openTabs, activeTabId } = get();
  if (workspaceId === null) return;
  const payload: PersistedTabs = { version: 1, tabs: openTabs, activeTabId };
  void setSetting(openTabsKey(workspaceId), JSON.stringify(payload)).catch(() => {});
}

function schedulePersist(get: () => ApiTabsState) {
  if (persistTimer !== null) clearTimeout(persistTimer);
  persistTimer = setTimeout(() => {
    persistTimer = null;
    persistNow(get);
  }, PERSIST_DEBOUNCE_MS);
}

/**
 * Everything a tab owns outside this store: its live socket and its runtime buffers.
 *
 * A tab is the last owner of both; whether it goes away because the user closed it or because
 * the workspace it belonged to was left behind, skipping this keeps the connection open and the
 * response body in memory for the rest of the session.
 */
function releaseTab(tabId: string) {
  const runtime = useApiRuntimeStore.getState();
  const connection = runtime.connections[tabId];
  if (connection) void apiStreamDisconnect(connection.id).catch(() => {});
  runtime.disposeTab(tabId);
}

/** A tab written by an older version can be missing spec fields the editor now reads. */
function rehydrateTab(tab: ApiTab): ApiTab {
  return { ...tab, draft: { ...defaultRequestSpec(tab.draft?.protocol ?? "http"), ...tab.draft } };
}
