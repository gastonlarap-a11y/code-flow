/**
 * Turns CollectionTree's recursive render — collections, then per-container folders-then-requests,
 * recursing into expanded folders — into the flat list of visible rows the virtualizer renders.
 *
 * Row order is byte-for-byte the old render's: the draft input first in its container, folders
 * before requests (each sorted by `sort_order`), and the "no requests" notice only for an empty
 * expanded collection when no drag is in flight. `siblingIndex`/`siblingsOfKind` carry what the
 * drop-line overlay needs to place an insertion line without re-deriving the grouping.
 */

import type { ApiCollection, ApiFolder, ApiRequestRow } from "../types/api";

export interface CollectionDraft {
  kind: "folder" | "request";
  collectionId: string;
  parentId: string | null;
}

export type CollectionTreeRow =
  | { kind: "collection"; id: string; depth: 0; collection: ApiCollection }
  | {
      kind: "folder";
      id: string;
      depth: number;
      folder: ApiFolder;
      collectionId: string;
      parentId: string | null;
      siblingIndex: number;
      siblingsOfKind: number;
    }
  | {
      kind: "request";
      id: string;
      depth: number;
      request: ApiRequestRow;
      collectionId: string;
      parentId: string | null;
      siblingIndex: number;
      siblingsOfKind: number;
    }
  | { kind: "draft"; id: "draft"; depth: number; draft: CollectionDraft }
  | { kind: "empty"; id: string; depth: number; collectionId: string };

export interface FlattenCollectionTreeInput {
  collections: ApiCollection[];
  folders: ApiFolder[];
  requests: ApiRequestRow[];
  expanded: ReadonlySet<string>;
  draft: CollectionDraft | null;
  /** A drag in flight suppresses the empty-collection notice so the drop gap can own the space. */
  dragging: boolean;
}

export function flattenCollectionTree(input: FlattenCollectionTreeInput): CollectionTreeRow[] {
  const { collections, folders, requests, expanded, draft, dragging } = input;

  const key = (collectionId: string, parentId: string | null) => `${collectionId}\0${parentId ?? ""}`;
  const pushInto = <T>(map: Map<string, T[]>, k: string, value: T) => {
    const existing = map.get(k);
    if (existing) existing.push(value);
    else map.set(k, [value]);
  };
  const foldersByContainer = new Map<string, ApiFolder[]>();
  for (const folder of [...folders].sort((a, b) => a.sort_order - b.sort_order)) {
    pushInto(foldersByContainer, key(folder.collection_id, folder.parent_id), folder);
  }
  const requestsByContainer = new Map<string, ApiRequestRow[]>();
  for (const request of [...requests].sort((a, b) => a.sort_order - b.sort_order)) {
    pushInto(requestsByContainer, key(request.collection_id, request.folder_id), request);
  }

  const rows: CollectionTreeRow[] = [];

  const walkContainer = (collectionId: string, parentId: string | null, depth: number) => {
    const folderRows = foldersByContainer.get(key(collectionId, parentId)) ?? [];
    const requestRows = requestsByContainer.get(key(collectionId, parentId)) ?? [];
    const draftHere =
      draft && draft.collectionId === collectionId && draft.parentId === parentId ? draft : null;

    // Only a collection announces that it's empty; an empty folder just shows nothing, the way
    // every file explorer does.
    if (parentId === null && folderRows.length === 0 && requestRows.length === 0 && !draftHere && !dragging) {
      rows.push({ kind: "empty", id: `empty:${collectionId}`, depth, collectionId });
      return;
    }

    if (draftHere) rows.push({ kind: "draft", id: "draft", depth, draft: draftHere });
    folderRows.forEach((folder, siblingIndex) => {
      rows.push({
        kind: "folder",
        id: folder.id,
        depth,
        folder,
        collectionId,
        parentId,
        siblingIndex,
        siblingsOfKind: folderRows.length,
      });
      if (expanded.has(folder.id)) walkContainer(collectionId, folder.id, depth + 1);
    });
    requestRows.forEach((request, siblingIndex) => {
      rows.push({
        kind: "request",
        id: request.id,
        depth,
        request,
        collectionId,
        parentId,
        siblingIndex,
        siblingsOfKind: requestRows.length,
      });
    });
  };

  for (const collection of [...collections].sort((a, b) => a.sort_order - b.sort_order)) {
    rows.push({ kind: "collection", id: collection.id, depth: 0, collection });
    if (expanded.has(collection.id)) walkContainer(collection.id, null, 1);
  }
  return rows;
}
