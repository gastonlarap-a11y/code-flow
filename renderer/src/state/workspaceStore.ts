import { create } from "zustand";
import * as api from "../lib/ipc/commands";
import { pushErrorToast } from "./toastStore";
import { translate } from "./languageStore";
import { parseRecent, pushRecent } from "../lib/ui/recentProjects";
import { isLegacyDefault, nextProjectColor } from "../lib/ui/projectColor";
import type { NewProject, Project, Workspace } from "../types/domain";

const LAST_WORKSPACE_KEY = "last_active_workspace_id";

/// Name of the workspace seeded on a fresh install, on every platform. Only applies when the
/// database has no workspaces at all — an existing install keeps whatever it already has.
const DEFAULT_WORKSPACE_NAME = "Flow";
const LAST_PROJECT_KEY = "last_active_project_id";
/** The MRU behind Home's "recent projects". A JSON array of ids in one app-setting — see
 * `lib/ui/recentProjects.ts` for why this is a setting rather than a column. */
const RECENT_PROJECTS_KEY = "recent_project_ids";

interface WorkspaceState {
  workspaces: Workspace[];
  projectsByWorkspace: Record<string, Project[]>;
  activeWorkspaceId: string | null;
  activeProjectId: string | null;
  /** Project ids, most recently opened first. Home reads it; `setActiveProject` writes it. */
  recentProjectIds: string[];
  loading: boolean;

  loadWorkspaces: () => Promise<void>;
  loadProjects: (workspaceId: string) => Promise<void>;
  addWorkspace: (name: string, icon: string, color: string) => Promise<Workspace>;
  removeWorkspace: (id: string) => Promise<void>;
  /** Renames a workspace. Rejects if the name is blank once trimmed (WS-009). */
  renameWorkspace: (id: string, name: string) => Promise<void>;
  setWorkspaceColor: (id: string, color: string) => Promise<void>;
  /** Sets or clears (both nulls) the workspace's commit-identity override. */
  setWorkspaceGitIdentity: (id: string, name: string | null, email: string | null) => Promise<void>;
  addProject: (input: NewProject) => Promise<Project>;
  removeProject: (id: string, workspaceId: string) => Promise<void>;
  setProjectColor: (id: string, workspaceId: string, color: string) => Promise<void>;
  moveProject: (id: string, fromWorkspaceId: string, toWorkspaceId: string) => Promise<void>;
  setActiveWorkspace: (id: string) => void;
  setActiveProject: (id: string) => void;
  /** Brings a project into focus from anywhere, crossing workspaces if it lives in another one —
   * awaitable, so a caller that needs `activeProject()` to already resolve (opening a PR from a
   * pasted link) can wait for the workspace's projects to load instead of racing them. */
  focusProject: (workspaceId: string, projectId: string) => Promise<void>;

  /**
   * Gives every repository still on the old hardcoded indigo a colour of its own.
   *
   * Repositories added before colours were handed out are all the same `#6366f1`, which is the one
   * value that cannot have been chosen: the swatch picker offers eight hues and none of them is
   * that. So this recolours exactly the defaulted ones and never touches a decision, which is what
   * makes doing it silently acceptable. Naturally idempotent — after the first pass nothing matches.
   */
  spreadLegacyColours: (projects: Project[]) => Promise<Project[]>;

  activeProject: () => Project | null;
}

export const useWorkspaceStore = create<WorkspaceState>((set, get) => ({
  workspaces: [],
  projectsByWorkspace: {},
  activeWorkspaceId: null,
  activeProjectId: null,
  recentProjectIds: [],
  loading: false,

  loadWorkspaces: async () => {
    set({ loading: true });
    try {
      // Atomic: query then (only if truly empty) create the default, all in one async
      // flow. This used to be a separate effect keyed on workspaces.length, which raced
      // with this load and created a duplicate default workspace on every app start.
      // Read before anything can be selected: `setActiveProject` folds the new id into whatever is
      // here, and starting from an empty list would silently truncate the history to one entry.
      set({ recentProjectIds: parseRecent(await api.getSetting(RECENT_PROJECTS_KEY).catch(() => null)) });

      let workspaces = await api.listWorkspaces();
      if (workspaces.length === 0) {
        const defaultWorkspace = await api.createWorkspace(DEFAULT_WORKSPACE_NAME, "briefcase", "#6366f1");
        workspaces = [defaultWorkspace];
      }
      set({ workspaces });
      if (!get().activeWorkspaceId && workspaces.length > 0) {
        const lastId = await api.getSetting(LAST_WORKSPACE_KEY).catch(() => null);
        const restored = lastId ? workspaces.find((w) => w.id === lastId) : undefined;
        // `workspaces.length > 0` was just checked above, so `workspaces[0]` is always defined.
        const target = restored ?? workspaces[0]!;
        set({ activeWorkspaceId: target.id });
        await get().loadProjects(target.id);
      }
    } catch (e) {
      // Without this the failure was invisible twice over: the only caller is a `void
      // loadWorkspaces()` in an effect, so the rejection went nowhere, and the state it left behind
      // — no workspaces, no active id — is exactly what makes the sidebar's add-project button
      // return without doing anything. One backend error, three symptoms, no message.
      pushErrorToast(translate("toast.workspacesLoadFailed", { error: String(e) }));
    } finally {
      set({ loading: false });
    }
  },

  loadProjects: async (workspaceId) => {
    const projects = await get().spreadLegacyColours(await api.listProjects(workspaceId));
    set((s) => ({ projectsByWorkspace: { ...s.projectsByWorkspace, [workspaceId]: projects } }));
    if (!get().activeProjectId && projects.length > 0) {
      const lastId = await api.getSetting(LAST_PROJECT_KEY).catch(() => null);
      const restored = lastId ? projects.find((p) => p.id === lastId) : undefined;
      // Through `setActiveProject` rather than a bare `set`, so the one path that records recency
      // is the one path that selects a project. `projects.length > 0` was just checked above.
      get().setActiveProject((restored ?? projects[0]!).id);
    }
  },

  addWorkspace: async (name, icon, color) => {
    const ws = await api.createWorkspace(name, icon, color);
    set((s) => ({ workspaces: [...s.workspaces, ws] }));
    return ws;
  },

  removeWorkspace: async (id) => {
    await api.deleteWorkspace(id);
    set((s) => {
      const { [id]: _removed, ...restProjects } = s.projectsByWorkspace;
      const workspaces = s.workspaces.filter((w) => w.id !== id);
      const wasActive = s.activeWorkspaceId === id;
      return {
        workspaces,
        projectsByWorkspace: restProjects,
        activeWorkspaceId: wasActive ? null : s.activeWorkspaceId,
        activeProjectId: wasActive ? null : s.activeProjectId,
      };
    });
    const workspaces = get().workspaces;
    if (get().activeWorkspaceId === null && workspaces.length > 0) {
      // `workspaces.length > 0` was just checked, so `workspaces[0]` is always defined.
      get().setActiveWorkspace(workspaces[0]!.id);
    }
  },

  renameWorkspace: async (id, name) => {
    const trimmed = name.trim();
    // The sidecar trims too and refuses a blank; mirroring it here keeps the optimistic update
    // showing what was actually stored rather than what was typed.
    await api.renameWorkspace(id, trimmed);
    set((s) => ({ workspaces: s.workspaces.map((w) => (w.id === id ? { ...w, name: trimmed } : w)) }));
  },

  setWorkspaceColor: async (id, color) => {
    await api.updateWorkspaceColor(id, color);
    set((s) => ({ workspaces: s.workspaces.map((w) => (w.id === id ? { ...w, color } : w)) }));
  },

  setWorkspaceGitIdentity: async (id, name, email) => {
    await api.updateWorkspaceGitIdentity(id, name, email);
    set((s) => ({
      workspaces: s.workspaces.map((w) =>
        w.id === id ? { ...w, git_name: name, git_email: email } : w,
      ),
    }));
  },

  spreadLegacyColours: async (projects) => {
    const stale = projects.filter((p) => isLegacyDefault(p.color));
    if (stale.length === 0) return projects;

    // Seeded with what the other workspaces already hold, so the spread stays even across all of
    // them rather than restarting the palette per workspace.
    const taken = Object.values(get().projectsByWorkspace)
      .flat()
      .map((p) => p.color)
      .filter((color) => !isLegacyDefault(color));

    const assigned = new Map<string, string>();
    for (const project of stale) {
      const colour = nextProjectColor([...taken, ...assigned.values()]);
      assigned.set(project.id, colour);
      try {
        await api.updateProjectColor(project.id, colour);
      } catch {
        // A colour that could not be saved is not worth failing a workspace load over: the repo
        // keeps the indigo it had and the next load tries again.
        assigned.delete(project.id);
      }
    }

    return projects.map((p) => (assigned.has(p.id) ? { ...p, color: assigned.get(p.id)! } : p));
  },

  addProject: async (input) => {
    // The colour is decided here rather than at each of the three places that add a repository,
    // which all wrote the same indigo literal — so every repository in the sidebar was the same
    // colour and the per-project picker in Settings had nothing to distinguish. This is also the
    // only place that knows which colours are already taken. A caller may still name one.
    const taken = Object.values(get().projectsByWorkspace).flat().map((p) => p.color);
    const project = await api.createProject({ ...input, color: input.color ?? nextProjectColor(taken) });
    set((s) => ({
      projectsByWorkspace: {
        ...s.projectsByWorkspace,
        [input.workspace_id]: [...(s.projectsByWorkspace[input.workspace_id] ?? []), project],
      },
    }));
    // Adding a repository opens it, so it goes through the same door as any other selection and
    // lands at the top of the recents.
    get().setActiveProject(project.id);
    return project;
  },

  removeProject: async (id, workspaceId) => {
    await api.deleteProject(id);
    set((s) => ({
      projectsByWorkspace: {
        ...s.projectsByWorkspace,
        [workspaceId]: (s.projectsByWorkspace[workspaceId] ?? []).filter((p) => p.id !== id),
      },
      activeProjectId: s.activeProjectId === id ? null : s.activeProjectId,
    }));
  },

  setProjectColor: async (id, workspaceId, color) => {
    await api.updateProjectColor(id, color);
    set((s) => ({
      projectsByWorkspace: {
        ...s.projectsByWorkspace,
        [workspaceId]: (s.projectsByWorkspace[workspaceId] ?? []).map((p) =>
          p.id === id ? { ...p, color } : p,
        ),
      },
    }));
  },

  moveProject: async (id, fromWorkspaceId, toWorkspaceId) => {
    if (fromWorkspaceId === toWorkspaceId) return;
    await api.moveProjectToWorkspace(id, toWorkspaceId);
    set((s) => {
      const fromProjects = s.projectsByWorkspace[fromWorkspaceId];
      const project = fromProjects?.find((p) => p.id === id);
      if (!fromProjects || !project) return s;
      const moved = { ...project, workspace_id: toWorkspaceId };
      return {
        projectsByWorkspace: {
          ...s.projectsByWorkspace,
          [fromWorkspaceId]: fromProjects.filter((p) => p.id !== id),
          [toWorkspaceId]: [...(s.projectsByWorkspace[toWorkspaceId] ?? []), moved],
        },
      };
    });
  },

  setActiveWorkspace: (id) => {
    set({ activeWorkspaceId: id, activeProjectId: null });
    void api.setSetting(LAST_WORKSPACE_KEY, id);
    void get().loadProjects(id);
  },

  setActiveProject: (id) => {
    const recentProjectIds = pushRecent(get().recentProjectIds, id);
    set({ activeProjectId: id, recentProjectIds });
    void api.setSetting(LAST_PROJECT_KEY, id);
    void api.setSetting(RECENT_PROJECTS_KEY, JSON.stringify(recentProjectIds));
  },

  focusProject: async (workspaceId, projectId) => {
    if (get().activeWorkspaceId !== workspaceId) {
      // Same effect as setActiveWorkspace, except the projects load is awaited rather than
      // fired and forgotten — the caller's next step depends on this workspace's list.
      set({ activeWorkspaceId: workspaceId, activeProjectId: null });
      void api.setSetting(LAST_WORKSPACE_KEY, workspaceId);
      await get().loadProjects(workspaceId);
    } else if (!get().projectsByWorkspace[workspaceId]) {
      await get().loadProjects(workspaceId);
    }
    get().setActiveProject(projectId);
  },

  activeProject: () => {
    const { activeWorkspaceId, activeProjectId, projectsByWorkspace } = get();
    if (!activeWorkspaceId || !activeProjectId) return null;
    return projectsByWorkspace[activeWorkspaceId]?.find((p) => p.id === activeProjectId) ?? null;
  },
}));
