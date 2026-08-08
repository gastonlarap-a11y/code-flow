import { beforeEach, describe, expect, test, vi } from "vitest";
import type { ApiCollection, ApiFolder, ApiRequestRow } from "../types/api";
import type { ApiTab } from "./apiTabsStore";
import { defaultRequestSpec } from "../types/api";

/**
 * The renderer half of `XLANG-011`: SQLite cascades a collection/folder delete, and this store
 * mirrors that cascade in memory instead of reloading the tree — then hands the orphaned request
 * ids to `apiTabsStore`, which turns their tabs back into scratch tabs. A tab losing its saved
 * row must NOT lose the user's unsaved draft; that detach-not-close behaviour is the part nobody
 * forgives losing, so it is pinned here against the real tabs store, not a mock of it.
 */

const toasts: string[] = [];

vi.mock("../lib/ipc/apiCommands", () => ({
  apiCreateCollection: vi.fn(),
  apiCreateFolder: vi.fn(),
  apiCreateRequest: vi.fn(),
  apiDeleteCollection: vi.fn(() => Promise.resolve()),
  apiDeleteFolder: vi.fn(() => Promise.resolve()),
  apiDeleteRequest: vi.fn(() => Promise.resolve()),
  apiDuplicateCollection: vi.fn(),
  apiDuplicateRequest: vi.fn(),
  apiLoadTree: vi.fn(),
  apiMoveNode: vi.fn(() => Promise.resolve()),
  apiReorderCollections: vi.fn(),
  apiStreamDisconnect: vi.fn(() => Promise.resolve()),
  apiUpdateCollection: vi.fn(() => Promise.resolve()),
  apiUpdateFolder: vi.fn(),
  apiUpdateRequest: vi.fn(() => Promise.resolve()),
}));

vi.mock("../lib/ipc/commands", () => ({
  getSetting: vi.fn(() => Promise.resolve(null)),
  setSetting: vi.fn(() => Promise.resolve()),
}));

vi.mock("./toastStore", () => ({
  pushErrorToast: (message: string) => toasts.push(message),
}));

const api = vi.mocked(await import("../lib/ipc/apiCommands"));
const { useApiTreeStore } = await import("./apiTreeStore");
const { useApiTabsStore } = await import("./apiTabsStore");

const initialTree = useApiTreeStore.getState();
const initialTabs = useApiTabsStore.getState();

function collection(id: string): ApiCollection {
  return {
    id,
    workspace_id: "ws",
    name: id,
    description: "",
    auth: "",
    pre_script: "",
    post_script: "",
    variables: "[]",
    sort_order: 0,
    created_at: "",
    updated_at: "",
  };
}

function folder(id: string, collectionId: string, parentId: string | null): ApiFolder {
  return {
    id,
    collection_id: collectionId,
    parent_id: parentId,
    name: id,
    description: "",
    auth: "",
    pre_script: "",
    post_script: "",
    sort_order: 0,
    created_at: "",
  };
}

function request(id: string, collectionId: string, folderId: string | null): ApiRequestRow {
  return {
    id,
    collection_id: collectionId,
    folder_id: folderId,
    name: id,
    protocol: "http",
    method: "GET",
    url: "https://x.test",
    spec: "{}",
    sort_order: 0,
    created_at: "",
    updated_at: "",
  };
}

function tab(id: string, requestId: string | null, collectionId: string | null): ApiTab {
  return {
    id,
    requestId,
    draft: { ...defaultRequestSpec("http"), url: `https://draft.test/${id}` },
    name: id,
    dirty: false,
    collectionId,
    folderId: null,
  };
}

beforeEach(() => {
  toasts.length = 0;
  vi.resetAllMocks();
  useApiTreeStore.setState(initialTree, true);
  useApiTabsStore.setState(initialTabs, true);

  useApiTreeStore.setState({
    workspaceId: "ws",
    collections: [collection("col-1"), collection("col-2")],
    folders: [folder("f1", "col-1", null), folder("f2", "col-1", "f1"), folder("g1", "col-2", null)],
    requests: [
      request("r-root", "col-1", null),
      request("r-nested", "col-1", "f2"),
      request("r-other", "col-2", null),
    ],
  });
  useApiTabsStore.setState({
    workspaceId: "ws",
    openTabs: [tab("tab-nested", "r-nested", "col-1"), tab("tab-other", "r-other", "col-2")],
    activeTabId: "tab-nested",
  });
});

describe("deleteCollection mirrors the SQLite cascade (XLANG-011)", () => {
  test("drops the collection's folders and requests, and detaches — not closes — their tabs", async () => {
    await useApiTreeStore.getState().deleteCollection("col-1");

    const tree = useApiTreeStore.getState();
    expect(tree.collections.map((c) => c.id)).toEqual(["col-2"]);
    expect(tree.folders.map((f) => f.id)).toEqual(["g1"]);
    expect(tree.requests.map((r) => r.id)).toEqual(["r-other"]);

    const tabs = useApiTabsStore.getState().openTabs;
    const detached = tabs.find((t) => t.id === "tab-nested");
    // The tab survives as a scratch tab: still open, unsaved draft intact, marked dirty.
    expect(detached).toMatchObject({ requestId: null, collectionId: null, folderId: null, dirty: true });
    expect(detached?.draft.url).toBe("https://draft.test/tab-nested");
    // The other workspace tab keeps its row.
    expect(tabs.find((t) => t.id === "tab-other")).toMatchObject({ requestId: "r-other" });
  });

  test("a backend failure leaves the tree and the tabs untouched, with a toast", async () => {
    api.apiDeleteCollection.mockRejectedValueOnce(new Error("db locked"));
    await useApiTreeStore.getState().deleteCollection("col-1");

    expect(useApiTreeStore.getState().collections).toHaveLength(2);
    expect(useApiTabsStore.getState().openTabs.find((t) => t.id === "tab-nested")?.requestId).toBe("r-nested");
    expect(toasts).toHaveLength(1);
  });
});

describe("deleteFolder mirrors the cascade recursively", () => {
  test("removes the folder's whole subtree and detaches the requests underneath", async () => {
    await useApiTreeStore.getState().deleteFolder("f1");

    const tree = useApiTreeStore.getState();
    // f2 is a child of f1, so it goes too; g1 belongs to another collection and stays.
    expect(tree.folders.map((f) => f.id)).toEqual(["g1"]);
    // r-nested lived in f2; r-root hangs directly off the collection and survives.
    expect(tree.requests.map((r) => r.id)).toEqual(["r-root", "r-other"]);
    expect(useApiTabsStore.getState().openTabs.find((t) => t.id === "tab-nested")?.requestId).toBeNull();
  });
});

describe("deleteRequest detaches exactly its own tab", () => {
  test("the deleted request's tab becomes scratch; every other tab is untouched", async () => {
    await useApiTreeStore.getState().deleteRequest("r-other");

    const tabs = useApiTabsStore.getState().openTabs;
    expect(tabs.find((t) => t.id === "tab-other")?.requestId).toBeNull();
    expect(tabs.find((t) => t.id === "tab-nested")?.requestId).toBe("r-nested");
  });
});
