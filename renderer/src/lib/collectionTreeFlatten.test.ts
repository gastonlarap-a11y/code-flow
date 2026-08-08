/**
 * The flatten is the virtualizer's contract with CollectionTree: row order is byte-for-byte the
 * old recursive render's — draft input first in its container, folders before requests (each by
 * `sort_order`), children only under expanded nodes, and the empty-collection notice only when
 * no drag is in flight. The drop-line overlay depends on `siblingIndex`/`siblingsOfKind`, so
 * those are pinned too.
 */

import { describe, expect, test } from "vitest";
import { flattenCollectionTree } from "./collectionTreeFlatten";
import type { ApiCollection, ApiFolder, ApiRequestRow } from "../types/api";

function collection(id: string, sortOrder: number): ApiCollection {
  return {
    id,
    workspace_id: "ws",
    name: id,
    description: "",
    auth: "",
    pre_script: "",
    post_script: "",
    variables: "[]",
    sort_order: sortOrder,
    created_at: "",
    updated_at: "",
  };
}

function folder(id: string, collectionId: string, parentId: string | null, sortOrder: number): ApiFolder {
  return {
    id,
    collection_id: collectionId,
    parent_id: parentId,
    name: id,
    description: "",
    auth: "",
    pre_script: "",
    post_script: "",
    sort_order: sortOrder,
    created_at: "",
  };
}

function request(id: string, collectionId: string, folderId: string | null, sortOrder: number): ApiRequestRow {
  return {
    id,
    collection_id: collectionId,
    folder_id: folderId,
    name: id,
    protocol: "http",
    method: "GET",
    url: "https://x.test",
    spec: "{}",
    sort_order: sortOrder,
    created_at: "",
    updated_at: "",
  };
}

const base = {
  expanded: new Set<string>(),
  draft: null,
  dragging: false,
};

describe("flattenCollectionTree", () => {
  test("collections order by sort_order and collapse to a single row each", () => {
    const rows = flattenCollectionTree({
      ...base,
      collections: [collection("b", 1), collection("a", 0)],
      folders: [folder("f", "a", null, 0)],
      requests: [],
    });
    expect(rows.map((r) => r.id)).toEqual(["a", "b"]);
  });

  test("an expanded container lists folders before requests, each by sort_order", () => {
    const rows = flattenCollectionTree({
      ...base,
      collections: [collection("col", 0)],
      folders: [folder("f-late", "col", null, 1), folder("f-early", "col", null, 0)],
      requests: [request("r-late", "col", null, 1), request("r-early", "col", null, 0)],
      expanded: new Set(["col"]),
    });
    expect(rows.map((r) => r.id)).toEqual(["col", "f-early", "f-late", "r-early", "r-late"]);
    expect(rows.slice(1).map((r) => r.depth)).toEqual([1, 1, 1, 1]);
  });

  test("an expanded folder's subtree interleaves between it and its next sibling", () => {
    const rows = flattenCollectionTree({
      ...base,
      collections: [collection("col", 0)],
      folders: [folder("f1", "col", null, 0), folder("f2", "col", null, 1)],
      requests: [request("r-in-f1", "col", "f1", 0), request("r-root", "col", null, 0)],
      expanded: new Set(["col", "f1"]),
    });
    expect(rows.map((r) => [r.id, r.depth])).toEqual([
      ["col", 0],
      ["f1", 1],
      ["r-in-f1", 2],
      ["f2", 1],
      ["r-root", 1],
    ]);
  });

  test("siblingIndex and siblingsOfKind count within the kind, not across the container", () => {
    const rows = flattenCollectionTree({
      ...base,
      collections: [collection("col", 0)],
      folders: [folder("f1", "col", null, 0)],
      requests: [request("r1", "col", null, 0), request("r2", "col", null, 1)],
      expanded: new Set(["col"]),
    });
    const r2 = rows.find((r) => r.id === "r2");
    expect(r2).toMatchObject({ kind: "request", siblingIndex: 1, siblingsOfKind: 2 });
    const f1 = rows.find((r) => r.id === "f1");
    expect(f1).toMatchObject({ kind: "folder", siblingIndex: 0, siblingsOfKind: 1 });
  });

  test("the draft input renders first in its container, and only there", () => {
    const rows = flattenCollectionTree({
      ...base,
      collections: [collection("col", 0)],
      folders: [folder("f1", "col", null, 0)],
      requests: [request("r-in-f1", "col", "f1", 0)],
      expanded: new Set(["col", "f1"]),
      draft: { kind: "request", collectionId: "col", parentId: "f1" },
    });
    expect(rows.map((r) => r.id)).toEqual(["col", "f1", "draft", "r-in-f1"]);
    expect(rows[2]).toMatchObject({ kind: "draft", depth: 2 });
  });

  test("an empty expanded collection announces itself — unless a drag needs the space", () => {
    const input = {
      ...base,
      collections: [collection("col", 0)],
      folders: [],
      requests: [],
      expanded: new Set(["col"]),
    };
    expect(flattenCollectionTree(input).map((r) => r.kind)).toEqual(["collection", "empty"]);
    expect(flattenCollectionTree({ ...input, dragging: true }).map((r) => r.kind)).toEqual(["collection"]);
  });

  test("an empty expanded folder shows nothing, the way every file explorer does", () => {
    const rows = flattenCollectionTree({
      ...base,
      collections: [collection("col", 0)],
      folders: [folder("f1", "col", null, 0)],
      requests: [],
      expanded: new Set(["col", "f1"]),
    });
    expect(rows.map((r) => r.id)).toEqual(["col", "f1"]);
  });

  test("a draft in an otherwise empty collection replaces the notice", () => {
    const rows = flattenCollectionTree({
      ...base,
      collections: [collection("col", 0)],
      folders: [],
      requests: [],
      expanded: new Set(["col"]),
      draft: { kind: "folder", collectionId: "col", parentId: null },
    });
    expect(rows.map((r) => r.kind)).toEqual(["collection", "draft"]);
  });
});
