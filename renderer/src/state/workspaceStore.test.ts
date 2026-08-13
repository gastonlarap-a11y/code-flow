import { beforeEach, describe, expect, test, vi } from "vitest";
import type { Project, Workspace } from "../types/domain";

// The sidecar is the only thing this store talks to, so mocking `lib/ipc/commands` is what makes
// it testable at all under `environment: "node"` — the real module reaches `window.codeflow`
// through `lib/bridge/host`, and there is no `window` here.
vi.mock("../lib/ipc/commands", () => ({
  updateWorkspaceGitIdentity: vi.fn(() => Promise.resolve()),
  listWorkspaces: vi.fn(() => Promise.resolve([])),
  createWorkspace: vi.fn(() => Promise.resolve()),
  listProjects: vi.fn(() => Promise.resolve([])),
  getSetting: vi.fn(() => Promise.resolve(null)),
  setSetting: vi.fn(() => Promise.resolve()),
  updateProjectColor: vi.fn(() => Promise.resolve()),
}));

const toasts: string[] = [];
vi.mock("./toastStore", () => ({
  pushErrorToast: (message: string) => toasts.push(message),
}));

import * as api from "../lib/ipc/commands";
import { useWorkspaceStore } from "./workspaceStore";

const workspace = (id: string): Workspace => ({
  id,
  name: `Workspace ${id}`,
  icon: "folder",
  color: "#6366f1",
  sort_order: 0,
  created_at: "2026-01-01T00:00:00.0000000+00:00",
  ado_org: null,
  ado_project: null,
  git_name: null,
  git_email: null,
});

const project = (id: string, color: string): Project => ({
  id,
  workspace_id: "a",
  name: `Repo ${id}`,
  local_path: `/tmp/${id}`,
  remote_url: null,
  color,
  icon: "git-branch",
  sort_order: 0,
  created_at: "2026-01-01T00:00:00.0000000+00:00",
  ado_org: null,
  ado_project: null,
  ado_repo_id: null,
  github_owner: null,
  github_repo: null,
  github_host: null,
});

const initial = useWorkspaceStore.getState();

describe("setWorkspaceGitIdentity", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    vi.mocked(api.updateWorkspaceGitIdentity).mockResolvedValue(undefined);
    useWorkspaceStore.setState({ ...initial, workspaces: [workspace("a"), workspace("b")] }, true);
  });

  test("persists through the command and patches only the targeted workspace", async () => {
    await useWorkspaceStore.getState().setWorkspaceGitIdentity("a", "Work Person", "work@company.com");

    expect(api.updateWorkspaceGitIdentity).toHaveBeenCalledWith("a", "Work Person", "work@company.com");
    const [a, b] = useWorkspaceStore.getState().workspaces;
    expect(a).toMatchObject({ git_name: "Work Person", git_email: "work@company.com" });
    expect(b).toMatchObject({ git_name: null, git_email: null });
  });

  test("clearing sends both nulls and resets the pair", async () => {
    useWorkspaceStore.setState({
      workspaces: [{ ...workspace("a"), git_name: "Work Person", git_email: "work@company.com" }],
    });

    await useWorkspaceStore.getState().setWorkspaceGitIdentity("a", null, null);

    expect(api.updateWorkspaceGitIdentity).toHaveBeenCalledWith("a", null, null);
    expect(useWorkspaceStore.getState().workspaces[0]).toMatchObject({ git_name: null, git_email: null });
  });

  test("a rejected command leaves the state untouched", async () => {
    vi.mocked(api.updateWorkspaceGitIdentity).mockRejectedValueOnce(new Error("db locked"));

    await expect(
      useWorkspaceStore.getState().setWorkspaceGitIdentity("a", "Work Person", "work@company.com"),
    ).rejects.toThrow("db locked");

    expect(useWorkspaceStore.getState().workspaces[0]).toMatchObject({ git_name: null, git_email: null });
  });
});

describe("loadWorkspaces", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    toasts.length = 0;
    useWorkspaceStore.setState({ ...initial }, true);
  });

  test("a rejected listing is reported rather than dropped", async () => {
    // The only caller is `void loadWorkspaces()` in an effect, so a rejection escaping this function
    // reaches nothing — which is how one backend failure turned into three separate "the button does
    // nothing" reports. It must settle, and it must say so.
    vi.mocked(api.listWorkspaces).mockRejectedValueOnce(new Error("the CodeFlow core is not running"));

    await expect(useWorkspaceStore.getState().loadWorkspaces()).resolves.toBeUndefined();

    expect(toasts).toHaveLength(1);
    expect(toasts[0]).toContain("the CodeFlow core is not running");
  });

  test("a failed load clears the loading flag and leaves no active workspace", async () => {
    // `activeWorkspaceId` staying null is what disables the sidebar's add-project button. That is
    // now the intended outcome — visibly disabled beats silently inert — so it is pinned here
    // alongside the toast that explains it.
    vi.mocked(api.listWorkspaces).mockRejectedValueOnce(new Error("boom"));

    await useWorkspaceStore.getState().loadWorkspaces();

    expect(useWorkspaceStore.getState().loading).toBe(false);
    expect(useWorkspaceStore.getState().activeWorkspaceId).toBeNull();
    expect(useWorkspaceStore.getState().workspaces).toEqual([]);
  });

  test("a failed seeding of the default workspace is reported too", async () => {
    // The empty-database path writes before it reads again, so it fails where a listing would not:
    // a database that opens read-only lists fine and refuses the insert.
    vi.mocked(api.listWorkspaces).mockResolvedValueOnce([]);
    vi.mocked(api.createWorkspace).mockRejectedValueOnce(new Error("attempt to write a readonly database"));

    await useWorkspaceStore.getState().loadWorkspaces();

    expect(toasts[0]).toContain("readonly database");
    expect(useWorkspaceStore.getState().activeWorkspaceId).toBeNull();
  });

  test("a healthy load selects a workspace and loads its projects", async () => {
    vi.mocked(api.listWorkspaces).mockResolvedValueOnce([workspace("a")]);

    await useWorkspaceStore.getState().loadWorkspaces();

    expect(toasts).toHaveLength(0);
    expect(useWorkspaceStore.getState().activeWorkspaceId).toBe("a");
    expect(api.listProjects).toHaveBeenCalledWith("a");
  });
});

describe("spreadLegacyColours", () => {
  const LEGACY = "#6366f1";

  // Its own reset: the one above lives inside another `describe` and does not reach here, so
  // without this the call counts of the previous test leak into these.
  beforeEach(() => {
    vi.resetAllMocks();
    vi.mocked(api.updateProjectColor).mockResolvedValue(undefined);
    useWorkspaceStore.setState({ ...initial }, true);
  });

  test("repositories still on the old default each get a colour of their own", async () => {
    const spread = await useWorkspaceStore
      .getState()
      .spreadLegacyColours([project("1", LEGACY), project("2", LEGACY), project("3", LEGACY)]);

    const colours = spread.map((p) => p.color);
    expect(colours).not.toContain(LEGACY);
    expect(new Set(colours).size).toBe(3);

    // Written through, not just shown: the next launch has to agree with this one.
    expect(api.updateProjectColor).toHaveBeenCalledTimes(3);
  });

  test("a colour somebody chose is never touched", async () => {
    // The whole safety argument. `#6366f1` is not one of the eight the picker offers, so anything
    // else came from a person and rewriting it would undo a decision.
    const chosen = project("1", "#03a447");

    const spread = await useWorkspaceStore.getState().spreadLegacyColours([chosen]);

    expect(spread[0]!.color).toBe("#03a447");
    expect(api.updateProjectColor).not.toHaveBeenCalled();
  });

  test("nothing to do costs nothing", async () => {
    const already = [project("1", "#6260ff")];

    expect(await useWorkspaceStore.getState().spreadLegacyColours(already)).toBe(already);
  });

  test("a colour that cannot be saved leaves the repository as it was", async () => {
    // Rather than showing a colour the next launch will not remember.
    vi.mocked(api.updateProjectColor).mockRejectedValueOnce(new Error("db locked"));

    const spread = await useWorkspaceStore.getState().spreadLegacyColours([project("1", LEGACY)]);

    expect(spread[0]!.color).toBe(LEGACY);
  });
});
