import { afterEach, beforeEach, describe, expect, test, vi } from "vitest";
import type { ApiRequestRow } from "../types/api";
import type { ApiTab } from "./apiTabsStore";
import { defaultRequestSpec } from "../types/api";

/**
 * What this pins, in order of consequence:
 *
 * 1. `saveTab`'s error path: the IPC write happens *inside this store's guard* — a failed save
 *    must leave the tab dirty and the tree untouched. Delegating to the tree store's own guarded
 *    actions would swallow the failure and mark the tab clean; this suite is what keeps that
 *    refactor from ever looking safe.
 * 2. Persistence cadence: structural changes (open/close/save/detach) write `api_open_tabs`
 *    straight through; draft edits coalesce into one trailing debounced write.
 * 3. `hydrate` only trusts a version-1 blob, and a corrupt one restores as no tabs, not a throw.
 */

const toasts: string[] = [];

vi.mock("../lib/ipc/apiCommands", () => ({
  apiCreateRequest: vi.fn(),
  apiStreamDisconnect: vi.fn(() => Promise.resolve()),
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
const commands = vi.mocked(await import("../lib/ipc/commands"));
const { useApiTabsStore } = await import("./apiTabsStore");
const { useApiTreeStore } = await import("./apiTreeStore");

const initialTabs = useApiTabsStore.getState();
const initialTree = useApiTreeStore.getState();

function row(id: string, overrides: Partial<ApiRequestRow> = {}): ApiRequestRow {
  return {
    id,
    collection_id: "col-1",
    folder_id: null,
    name: id,
    protocol: "http",
    method: "GET",
    url: "https://x.test",
    spec: "{}",
    sort_order: 0,
    created_at: "",
    updated_at: "",
    ...overrides,
  };
}

function tab(id: string, overrides: Partial<ApiTab> = {}): ApiTab {
  return {
    id,
    requestId: null,
    draft: defaultRequestSpec("http"),
    name: id,
    dirty: false,
    collectionId: null,
    folderId: null,
    ...overrides,
  };
}

beforeEach(() => {
  toasts.length = 0;
  vi.resetAllMocks();
  useApiTabsStore.setState(initialTabs, true);
  useApiTreeStore.setState(initialTree, true);
  useApiTabsStore.setState({ workspaceId: "ws" });
  useApiTreeStore.setState({ workspaceId: "ws", requests: [row("r-1")] });
});

afterEach(() => {
  vi.useRealTimers();
});

describe("saveTab against an existing request", () => {
  test("writes the draft through IPC, mirrors the row in the tree and marks the tab clean", async () => {
    const draft = { ...defaultRequestSpec("http"), method: "POST", url: "https://new.test" };
    useApiTabsStore.setState({
      openTabs: [tab("t1", { requestId: "r-1", draft, dirty: true, collectionId: "col-1" })],
    });

    const saved = await useApiTabsStore.getState().saveTab("t1");

    expect(saved).toMatchObject({ id: "r-1", method: "POST", url: "https://new.test" });
    expect(api.apiUpdateRequest).toHaveBeenCalledWith(expect.objectContaining({ url: "https://new.test" }));
    expect(useApiTreeStore.getState().requests.find((r) => r.id === "r-1")?.url).toBe("https://new.test");
    const [firstTab] = useApiTabsStore.getState().openTabs;
    if (!firstTab) throw new Error("expected a tab");
    expect(firstTab.dirty).toBe(false);
  });

  test("a failed IPC write leaves the tab dirty and the tree untouched — the save did not happen", async () => {
    api.apiUpdateRequest.mockRejectedValueOnce(new Error("disk full"));
    useApiTabsStore.setState({
      openTabs: [tab("t1", { requestId: "r-1", draft: { ...defaultRequestSpec("http"), url: "https://new.test" }, dirty: true })],
    });

    const saved = await useApiTabsStore.getState().saveTab("t1");

    expect(saved).toBeNull();
    const [firstTab] = useApiTabsStore.getState().openTabs;
    if (!firstTab) throw new Error("expected a tab");
    expect(firstTab.dirty).toBe(true);
    expect(useApiTreeStore.getState().requests.find((r) => r.id === "r-1")?.url).toBe("https://x.test");
    expect(toasts).toHaveLength(1);
  });
});

describe("saveTab on a scratch tab", () => {
  test("with nowhere to go it returns null without touching IPC — the UI's cue to open the picker", async () => {
    useApiTabsStore.setState({ openTabs: [tab("t1")] });
    expect(await useApiTabsStore.getState().saveTab("t1")).toBeNull();
    expect(api.apiCreateRequest).not.toHaveBeenCalled();
  });

  test("with a target it files the request and rebinds the tab to the created row", async () => {
    api.apiCreateRequest.mockResolvedValueOnce(row("r-new", { folder_id: "f-1", name: "t1" }));
    useApiTabsStore.setState({ openTabs: [tab("t1", { dirty: true })] });

    const saved = await useApiTabsStore.getState().saveTab("t1", { collectionId: "col-1", folderId: "f-1" });

    expect(saved?.id).toBe("r-new");
    expect(useApiTreeStore.getState().requests.map((r) => r.id)).toContain("r-new");
    expect(useApiTabsStore.getState().openTabs[0]).toMatchObject({
      requestId: "r-new",
      dirty: false,
      collectionId: "col-1",
      folderId: "f-1",
    });
  });
});

describe("persistence cadence", () => {
  test("opening a scratch tab writes api_open_tabs straight through, under the workspace key", () => {
    useApiTabsStore.getState().openScratchTab();
    expect(commands.setSetting).toHaveBeenCalledTimes(1);
    const call = commands.setSetting.mock.calls[0];
    if (!call) throw new Error("expected setSetting to have been called");
    const [key, payload] = call;
    expect(key).toBe("api_open_tabs:ws");
    expect(JSON.parse(payload)).toMatchObject({ version: 1, tabs: [expect.any(Object)] });
  });

  test("draft edits coalesce into one trailing write after the debounce", () => {
    vi.useFakeTimers();
    useApiTabsStore.setState({ openTabs: [tab("t1")], activeTabId: "t1" });

    const update = useApiTabsStore.getState().updateDraft;
    update("t1", { url: "https://a" });
    update("t1", { url: "https://ab" });
    update("t1", { url: "https://abc" });
    expect(commands.setSetting).not.toHaveBeenCalled();

    vi.advanceTimersByTime(600);
    expect(commands.setSetting).toHaveBeenCalledTimes(1);
    const call = commands.setSetting.mock.calls[0];
    if (!call) throw new Error("expected setSetting to have been called");
    const persisted = JSON.parse(call[1]) as { tabs: ApiTab[] };
    const [persistedTab] = persisted.tabs;
    if (!persistedTab) throw new Error("expected a persisted tab");
    expect(persistedTab.draft.url).toBe("https://abc");
  });
});

describe("detachRequests", () => {
  test("turns the matching tabs into scratch tabs and persists; unrelated tabs keep their row", () => {
    useApiTabsStore.setState({
      openTabs: [tab("t1", { requestId: "r-1", collectionId: "col-1" }), tab("t2", { requestId: "r-2" })],
    });
    useApiTabsStore.getState().detachRequests(["r-1"]);

    const tabs = useApiTabsStore.getState().openTabs;
    expect(tabs[0]).toMatchObject({ requestId: null, collectionId: null, dirty: true });
    const secondTab = tabs[1];
    if (!secondTab) throw new Error("expected a second tab");
    expect(secondTab.requestId).toBe("r-2");
    expect(commands.setSetting).toHaveBeenCalledTimes(1);
  });

  test("no matching tab, no state churn and no write", () => {
    useApiTabsStore.setState({ openTabs: [tab("t1", { requestId: "r-1" })] });
    useApiTabsStore.getState().detachRequests(["r-elsewhere"]);
    const [firstTab] = useApiTabsStore.getState().openTabs;
    if (!firstTab) throw new Error("expected a tab");
    expect(firstTab.dirty).toBe(false);
    expect(commands.setSetting).not.toHaveBeenCalled();
  });
});

describe("hydrate", () => {
  test("restores a version-1 blob and falls back to the first tab when the active id is stale", () => {
    const blob = JSON.stringify({
      version: 1,
      tabs: [tab("t1"), tab("t2")],
      activeTabId: "t-gone",
    });
    useApiTabsStore.getState().hydrate("ws", blob);
    expect(useApiTabsStore.getState().openTabs).toHaveLength(2);
    expect(useApiTabsStore.getState().activeTabId).toBe("t1");
  });

  test("an unparseable blob restores as no tabs, not a throw", () => {
    useApiTabsStore.getState().hydrate("ws", "{broken");
    expect(useApiTabsStore.getState().openTabs).toEqual([]);
    expect(useApiTabsStore.getState().activeTabId).toBeNull();
  });

  test("a draft written by an older version is rehydrated over the current spec defaults", () => {
    const stale = { ...tab("t1"), draft: { protocol: "http", url: "https://old.test" } };
    const blob = JSON.stringify({ version: 1, tabs: [stale], activeTabId: "t1" });
    useApiTabsStore.getState().hydrate("ws", blob);
    const [hydratedTab] = useApiTabsStore.getState().openTabs;
    if (!hydratedTab) throw new Error("expected a hydrated tab");
    const draft = hydratedTab.draft;
    expect(draft.url).toBe("https://old.test");
    // A field the old version never wrote arrives from the defaults instead of undefined.
    expect(draft.method).toBe(defaultRequestSpec("http").method);
  });
});
