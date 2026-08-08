/**
 * The collection tree: collections, folders and request rows, with their CRUD and drag-move.
 *
 * Deleting a collection or folder mirrors SQLite's cascade in memory (see XLANG-011 in
 * `docs/business-rules/13-cross-language-contracts.md`) and hands the orphaned request ids to
 * `apiTabsStore`, which turns their tabs back into scratch tabs instead of closing them.
 */

import { create } from "zustand";
import {
  apiCreateCollection,
  apiCreateFolder,
  apiCreateRequest,
  apiDeleteCollection,
  apiDeleteFolder,
  apiDeleteRequest,
  apiDuplicateCollection,
  apiDuplicateRequest,
  apiLoadTree,
  apiMoveNode,
  apiReorderCollections,
  apiUpdateCollection,
  apiUpdateFolder,
  apiUpdateRequest,
} from "../lib/ipc/apiCommands";
import { pushErrorToast } from "./toastStore";
import { guarded } from "./apiShared";
import { useApiTabsStore } from "./apiTabsStore";
import type { ApiCollection, ApiFolder, ApiRequestRow, ApiRequestSpec } from "../types/api";

interface ApiTreeState {
  workspaceId: string | null;
  collections: ApiCollection[];
  folders: ApiFolder[];
  requests: ApiRequestRow[];

  hydrate: (
    workspaceId: string,
    collections: ApiCollection[],
    folders: ApiFolder[],
    requests: ApiRequestRow[],
  ) => void;
  reset: (workspaceId: string | null) => void;
  reloadTree: () => Promise<void>;

  createCollection: (name: string) => Promise<ApiCollection | null>;
  updateCollection: (collection: ApiCollection) => Promise<void>;
  deleteCollection: (id: string) => Promise<void>;
  duplicateCollection: (id: string) => Promise<void>;
  reorderCollections: (ids: string[]) => Promise<void>;

  createFolder: (collectionId: string, parentId: string | null, name: string) => Promise<ApiFolder | null>;
  updateFolder: (folder: ApiFolder) => Promise<void>;
  deleteFolder: (id: string) => Promise<void>;

  createRequest: (
    collectionId: string,
    folderId: string | null,
    name: string,
    spec: ApiRequestSpec,
  ) => Promise<ApiRequestRow | null>;
  updateRequest: (request: ApiRequestRow) => Promise<void>;
  deleteRequest: (id: string) => Promise<void>;
  duplicateRequest: (id: string) => Promise<void>;
  moveNode: (
    kind: "folder" | "request",
    id: string,
    collectionId: string,
    parentId: string | null,
    index: number,
  ) => Promise<void>;

  /**
   * Mirrors a row `apiTabsStore.saveTab` already wrote through IPC. The tabs store owns that
   * error path (a failed save must leave the tab dirty), so this is a pure state write — no IPC,
   * no guard.
   */
  upsertRequestRow: (row: ApiRequestRow) => void;
}

export const useApiTreeStore = create<ApiTreeState>((set, get) => ({
  workspaceId: null,
  collections: [],
  folders: [],
  requests: [],

  hydrate: (workspaceId, collections, folders, requests) =>
    set({ workspaceId, collections, folders, requests }),

  reset: (workspaceId) => set({ workspaceId, collections: [], folders: [], requests: [] }),

  reloadTree: async () => {
    const workspaceId = get().workspaceId;
    if (workspaceId === null) return;
    const tree = await apiLoadTree(workspaceId);
    set({ collections: tree.collections, folders: tree.folders, requests: tree.requests });
  },

  // ---------- collections ----------

  createCollection: async (name) => {
    const workspaceId = get().workspaceId;
    if (workspaceId === null) return null;
    return guarded(async () => {
      const collection = await apiCreateCollection(workspaceId, name);
      set((s) => ({ collections: [...s.collections, collection] }));
      return collection;
    });
  },

  updateCollection: async (collection) => {
    await guarded(async () => {
      await apiUpdateCollection(collection);
      set((s) => ({
        collections: s.collections.map((c) => (c.id === collection.id ? collection : c)),
      }));
    });
  },

  deleteCollection: async (id) => {
    await guarded(async () => {
      await apiDeleteCollection(id);
      // The cascade is in SQLite; mirroring it here avoids a full tree reload for a delete.
      const orphaned = get()
        .requests.filter((r) => r.collection_id === id)
        .map((r) => r.id);
      set((s) => ({
        collections: s.collections.filter((c) => c.id !== id),
        folders: s.folders.filter((f) => f.collection_id !== id),
        requests: s.requests.filter((r) => r.collection_id !== id),
      }));
      useApiTabsStore.getState().detachRequests(orphaned);
    });
  },

  duplicateCollection: async (id) => {
    await guarded(async () => {
      await apiDuplicateCollection(id);
      // A deep copy creates folders and requests too, so only a full reload is truthful.
      await get().reloadTree();
    });
  },

  reorderCollections: async (ids) => {
    const workspaceId = get().workspaceId;
    if (workspaceId === null) return;
    const previous = get().collections;
    set({ collections: sortByIds(previous, ids) });
    try {
      await apiReorderCollections(workspaceId, ids);
    } catch (e) {
      set({ collections: previous });
      pushErrorToast(String(e));
    }
  },

  // ---------- folders ----------

  createFolder: async (collectionId, parentId, name) =>
    guarded(async () => {
      const folder = await apiCreateFolder(collectionId, parentId, name);
      set((s) => ({ folders: [...s.folders, folder] }));
      return folder;
    }),

  updateFolder: async (folder) => {
    await guarded(async () => {
      await apiUpdateFolder(folder);
      set((s) => ({ folders: s.folders.map((f) => (f.id === folder.id ? folder : f)) }));
    });
  },

  deleteFolder: async (id) => {
    await guarded(async () => {
      await apiDeleteFolder(id);
      const removed = descendantFolderIds(get().folders, id);
      const orphaned = get()
        .requests.filter((r) => r.folder_id !== null && removed.has(r.folder_id))
        .map((r) => r.id);
      set((s) => ({
        folders: s.folders.filter((f) => !removed.has(f.id)),
        requests: s.requests.filter((r) => r.folder_id === null || !removed.has(r.folder_id)),
      }));
      useApiTabsStore.getState().detachRequests(orphaned);
    });
  },

  // ---------- requests ----------

  createRequest: async (collectionId, folderId, name, spec) =>
    guarded(async () => {
      const request = await apiCreateRequest(
        collectionId,
        folderId,
        name,
        spec.protocol,
        JSON.stringify(spec),
      );
      set((s) => ({ requests: [...s.requests, request] }));
      return request;
    }),

  updateRequest: async (request) => {
    await guarded(async () => {
      await apiUpdateRequest(request);
      set((s) => ({ requests: s.requests.map((r) => (r.id === request.id ? request : r)) }));
    });
  },

  deleteRequest: async (id) => {
    await guarded(async () => {
      await apiDeleteRequest(id);
      set((s) => ({ requests: s.requests.filter((r) => r.id !== id) }));
      useApiTabsStore.getState().detachRequests([id]);
    });
  },

  duplicateRequest: async (id) => {
    await guarded(async () => {
      const copy = await apiDuplicateRequest(id);
      set((s) => ({ requests: [...s.requests, copy] }));
    });
  },

  moveNode: async (kind, id, collectionId, parentId, index) => {
    const previous = { folders: get().folders, requests: get().requests };
    // Applied before the round trip on purpose: a drop that snaps back for 40ms and then lands
    // reads as a bug, so the tree commits immediately and only rolls back on a real failure.
    set(
      kind === "folder"
        ? moveFolderLocally(previous, id, collectionId, parentId, index)
        : { requests: moveRequestLocally(previous.requests, id, collectionId, parentId, index) },
    );
    try {
      await apiMoveNode(kind, id, collectionId, parentId, index);
    } catch (e) {
      set(previous);
      pushErrorToast(String(e));
      await get().reloadTree().catch(() => {});
    }
  },

  upsertRequestRow: (row) => {
    set((s) => ({
      requests: s.requests.some((r) => r.id === row.id)
        ? s.requests.map((r) => (r.id === row.id ? row : r))
        : [...s.requests, row],
    }));
  },
}));

// ---------------------------------------------------------------------------
// Tree helpers
// ---------------------------------------------------------------------------

function moveRequestLocally(
  requests: ApiRequestRow[],
  id: string,
  collectionId: string,
  parentId: string | null,
  index: number,
): ApiRequestRow[] {
  const moving = requests.find((r) => r.id === id);
  if (!moving) return requests;
  const others = requests.filter((r) => r.id !== id);
  const moved: ApiRequestRow = { ...moving, collection_id: collectionId, folder_id: parentId };
  const siblings = others
    .filter((r) => r.collection_id === collectionId && r.folder_id === parentId)
    .sort((a, b) => a.sort_order - b.sort_order);
  siblings.splice(clamp(index, siblings.length), 0, moved);
  return renumber([...others, moved], siblings);
}

function moveFolderLocally(
  tree: { folders: ApiFolder[]; requests: ApiRequestRow[] },
  id: string,
  collectionId: string,
  parentId: string | null,
  index: number,
): { folders: ApiFolder[]; requests: ApiRequestRow[] } {
  const moving = tree.folders.find((f) => f.id === id);
  if (!moving) return tree;
  const others = tree.folders.filter((f) => f.id !== id);
  const moved: ApiFolder = { ...moving, collection_id: collectionId, parent_id: parentId };
  const siblings = others
    .filter((f) => f.collection_id === collectionId && f.parent_id === parentId)
    .sort((a, b) => a.sort_order - b.sort_order);
  siblings.splice(clamp(index, siblings.length), 0, moved);

  let folders = renumber([...others, moved], siblings);
  let requests = tree.requests;
  // Dragging a folder across collections takes its whole subtree with it; without this the
  // moved children would keep pointing at the old collection until the next reload.
  if (moving.collection_id !== collectionId) {
    const subtree = descendantFolderIds(folders, id);
    folders = folders.map((f) => (subtree.has(f.id) ? { ...f, collection_id: collectionId } : f));
    requests = requests.map((r) =>
      r.folder_id !== null && subtree.has(r.folder_id) ? { ...r, collection_id: collectionId } : r,
    );
  }
  return { folders, requests };
}

/** Rewrites `sort_order` for the destination's children only; everything else keeps its own. */
function renumber<T extends { id: string; sort_order: number }>(all: T[], ordered: T[]): T[] {
  const positions = new Map(ordered.map((item, position) => [item.id, position]));
  return all.map((item) => {
    const position = positions.get(item.id);
    return position === undefined ? item : { ...item, sort_order: position };
  });
}

function clamp(index: number, length: number): number {
  return Math.max(0, Math.min(index, length));
}

/** `id` itself plus every folder beneath it. */
function descendantFolderIds(folders: ApiFolder[], id: string): Set<string> {
  const found = new Set([id]);
  let grew = true;
  while (grew) {
    grew = false;
    for (const folder of folders) {
      if (folder.parent_id !== null && found.has(folder.parent_id) && !found.has(folder.id)) {
        found.add(folder.id);
        grew = true;
      }
    }
  }
  return found;
}

/** Anything `ids` doesn't mention keeps its relative order at the end, as the backend does. */
function sortByIds<T extends { id: string; sort_order: number }>(items: T[], ids: string[]): T[] {
  const position = new Map(ids.map((id, index) => [id, index]));
  const rank = (item: T) => position.get(item.id) ?? Number.MAX_SAFE_INTEGER;
  return [...items]
    .sort((a, b) => rank(a) - rank(b))
    .map((item, index) => ({ ...item, sort_order: index }));
}
