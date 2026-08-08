import { beforeEach, describe, expect, test, vi } from "vitest";
import type { ApiCollection, ApiEnvironment, ApiVariable } from "../types/api";

/**
 * `setVariable` is the one write that crosses scopes: "environment" targets the active
 * environment, "global" the Globals row, and "collection" leaves this store entirely and goes
 * through `apiTreeStore.updateCollection`. A script's `pm.environment.set` and the quick-edit
 * popover both land here, so which row receives the write — and that a missing target is a
 * silent no-op, not a crash — is behaviour users see.
 */

const toasts: string[] = [];

vi.mock("../lib/ipc/apiCommands", () => ({
  apiCreateCollection: vi.fn(),
  apiCreateEnvironment: vi.fn(),
  apiCreateFolder: vi.fn(),
  apiCreateRequest: vi.fn(),
  apiDeleteCollection: vi.fn(),
  apiDeleteEnvironment: vi.fn(() => Promise.resolve()),
  apiDeleteFolder: vi.fn(),
  apiDeleteRequest: vi.fn(),
  apiDuplicateCollection: vi.fn(),
  apiDuplicateEnvironment: vi.fn(),
  apiDuplicateRequest: vi.fn(),
  apiListEnvironments: vi.fn(),
  apiLoadTree: vi.fn(),
  apiMoveNode: vi.fn(),
  apiReorderCollections: vi.fn(),
  apiStreamDisconnect: vi.fn(),
  apiUpdateCollection: vi.fn(() => Promise.resolve()),
  apiUpdateEnvironment: vi.fn(() => Promise.resolve()),
  apiUpdateFolder: vi.fn(),
  apiUpdateRequest: vi.fn(),
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
const { useApiEnvironmentStore } = await import("./apiEnvironmentStore");
const { useApiTreeStore } = await import("./apiTreeStore");

const initialEnv = useApiEnvironmentStore.getState();
const initialTree = useApiTreeStore.getState();

function environment(id: string, overrides: Partial<ApiEnvironment> = {}): ApiEnvironment {
  return {
    id,
    workspace_id: "ws",
    name: id,
    variables: "[]",
    is_global: false,
    sort_order: 0,
    created_at: "",
    ...overrides,
  };
}

function collection(id: string, overrides: Partial<ApiCollection> = {}): ApiCollection {
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
    ...overrides,
  };
}

function variablesOf(json: string | undefined): ApiVariable[] {
  return JSON.parse(json ?? "[]") as ApiVariable[];
}

beforeEach(() => {
  toasts.length = 0;
  vi.resetAllMocks();
  useApiEnvironmentStore.setState(initialEnv, true);
  useApiTreeStore.setState(initialTree, true);

  useApiEnvironmentStore.setState({
    workspaceId: "ws",
    environments: [environment("env-dev"), environment("globals", { is_global: true })],
    activeEnvironmentId: "env-dev",
  });
  useApiTreeStore.setState({ workspaceId: "ws", collections: [collection("col-1")] });
});

describe("setVariable routes the write to the scope's owner", () => {
  test("environment scope writes the active environment's variables", async () => {
    await useApiEnvironmentStore.getState().setVariable("environment", "token", "abc");

    expect(api.apiUpdateEnvironment).toHaveBeenCalledTimes(1);
    const call = api.apiUpdateEnvironment.mock.calls[0];
    if (!call) throw new Error("expected apiUpdateEnvironment to have been called");
    const written = call[0];
    expect(written.id).toBe("env-dev");
    expect(variablesOf(written.variables)[0]).toMatchObject({ key: "token", currentValue: "abc", enabled: true });
    // The in-memory row moved too, so the editor shows the write without a reload.
    expect(
      variablesOf(useApiEnvironmentStore.getState().environments.find((e) => e.id === "env-dev")?.variables),
    ).toHaveLength(1);
  });

  test("global scope writes the Globals row even when another environment is active", async () => {
    await useApiEnvironmentStore.getState().setVariable("global", "org", "acme");
    const call = api.apiUpdateEnvironment.mock.calls[0];
    if (!call) throw new Error("expected apiUpdateEnvironment to have been called");
    expect(call[0].id).toBe("globals");
  });

  test("collection scope leaves this store and goes through apiTreeStore.updateCollection", async () => {
    await useApiEnvironmentStore.getState().setVariable("collection", "path", "/v1", "col-1");

    expect(api.apiUpdateEnvironment).not.toHaveBeenCalled();
    expect(api.apiUpdateCollection).toHaveBeenCalledTimes(1);
    const call = api.apiUpdateCollection.mock.calls[0];
    if (!call) throw new Error("expected apiUpdateCollection to have been called");
    const written = call[0];
    expect(written.id).toBe("col-1");
    expect(variablesOf(written.variables)[0]).toMatchObject({ key: "path", currentValue: "/v1" });
    const [updatedCollection] = useApiTreeStore.getState().collections;
    if (!updatedCollection) throw new Error("expected a collection");
    expect(variablesOf(updatedCollection.variables)).toHaveLength(1);
  });

  test("an existing variable is updated in place, not duplicated", async () => {
    useApiEnvironmentStore.setState({
      environments: [
        environment("env-dev", {
          variables: JSON.stringify([
            { id: "v1", key: "token", initialValue: "seed", currentValue: "old", secret: false, enabled: false, description: "d" },
          ]),
        }),
        environment("globals", { is_global: true }),
      ],
    });
    await useApiEnvironmentStore.getState().setVariable("environment", "token", "new");

    const call = api.apiUpdateEnvironment.mock.calls[0];
    if (!call) throw new Error("expected apiUpdateEnvironment to have been called");
    const vars = variablesOf(call[0].variables);
    expect(vars).toHaveLength(1);
    // `currentValue` moves and the row re-enables; `initialValue` is untouched — it is the
    // shareable seed, not the session value.
    expect(vars[0]).toMatchObject({ key: "token", currentValue: "new", initialValue: "seed", enabled: true });
  });

  test("no environment active means the environment scope has no owner: a silent no-op", async () => {
    useApiEnvironmentStore.setState({ activeEnvironmentId: null });
    await useApiEnvironmentStore.getState().setVariable("environment", "k", "v");
    expect(api.apiUpdateEnvironment).not.toHaveBeenCalled();
  });
});

describe("deleteEnvironment and the active selection", () => {
  test("deleting the active environment clears the selection and persists the blank", async () => {
    await useApiEnvironmentStore.getState().deleteEnvironment("env-dev");

    expect(useApiEnvironmentStore.getState().activeEnvironmentId).toBeNull();
    expect(commands.setSetting).toHaveBeenCalledWith("api_active_environment:ws", "");
  });

  test("deleting an inactive environment leaves the selection alone", async () => {
    useApiEnvironmentStore.setState({
      environments: [environment("env-dev"), environment("env-x"), environment("globals", { is_global: true })],
    });
    await useApiEnvironmentStore.getState().deleteEnvironment("env-x");

    expect(useApiEnvironmentStore.getState().activeEnvironmentId).toBe("env-dev");
    expect(commands.setSetting).not.toHaveBeenCalled();
  });
});

describe("hydrate validates the persisted active id", () => {
  test("a stale id — its environment was deleted elsewhere — drops to null instead of dangling", () => {
    useApiEnvironmentStore.getState().hydrate("ws", [environment("env-dev")], "env-deleted");
    expect(useApiEnvironmentStore.getState().activeEnvironmentId).toBeNull();
  });

  test("a surviving id is kept", () => {
    useApiEnvironmentStore.getState().hydrate("ws", [environment("env-dev")], "env-dev");
    expect(useApiEnvironmentStore.getState().activeEnvironmentId).toBe("env-dev");
  });
});
