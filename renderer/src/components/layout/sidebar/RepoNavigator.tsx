import { useState } from "react";
import { GitBranchPlus, Link2, Plus } from "lucide-react";
import { useWorkspaceStore } from "../../../state/workspaceStore";
import { useUiStore } from "../../../state/uiStore";
import { useLayoutStore } from "../../../state/layoutStore";
import { pickFolder } from "../../../lib/ipc/commands";
import { IconButton } from "../../common/IconButton";
import { ResizeHandle } from "../../common/ResizeHandle";
import { CARD } from "../../common/panelChrome";
import { CloneRepoModal } from "../CloneRepoModal";
import { useT } from "../../../state/languageStore";
import { pushErrorToast } from "../../../state/toastStore";
import { ProjectRow } from "./ProjectRow";

const SIDEBAR_MIN = 200;
const SIDEBAR_MAX = 440;

/**
 * The context panel of the three repo modules: workspace, projects, and everything a project
 * expands into.
 *
 * This was `Sidebar.tsx` — the app's only aside, doing navigation and content at once. Navigation
 * left for `NavigationSidebar`; what stayed is content, and content belongs to a module rather than
 * to the window, which is why it renders through `ContextPanel` now. It no longer decides whether
 * it is visible either: that is `contextPanelOpen`, and the control for it sits in the navigation
 * sidebar, because a button that hides a panel cannot live inside the panel it hides.
 */
export function RepoNavigator() {
  const activeWorkspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);
  const projectsByWorkspace = useWorkspaceStore((s) => s.projectsByWorkspace);
  const addProject = useWorkspaceStore((s) => s.addProject);
  const sidebarWidth = useLayoutStore((s) => s.sizes.sidebarWidth);
  const setSize = useLayoutStore((s) => s.setSize);
  const commitSize = useLayoutStore((s) => s.commitSize);
  const openPrLinkModal = useUiStore((s) => s.openPrLinkModal);
  const t = useT();
  const [showCloneModal, setShowCloneModal] = useState(false);

  const projects = activeWorkspaceId ? projectsByWorkspace[activeWorkspaceId] ?? [] : [];

  // The guard that used to be here — `if (!activeWorkspaceId) return;` — is now the button's
  // `disabled` instead. A handler that returns on its first line is indistinguishable from a broken
  // one, and that is precisely what it looked like when a failed workspace load left the id null.
  const handleAddProject = async () => {
    if (!activeWorkspaceId) return;
    try {
      const folder = await pickFolder();
      if (folder === null) return;
      const name = folder.split(/[\\/]/).filter(Boolean).pop() ?? folder;
      await addProject({
        workspace_id: activeWorkspaceId,
        name,
        local_path: folder,
        remote_url: null,
        color: "#6366f1",
        icon: "git-branch",
        ado_org: null,
        ado_project: null,
        ado_repo_id: null,
        github_owner: null,
        github_repo: null,
        github_host: null,
      });
    } catch (e) {
      pushErrorToast(t("toast.addProjectFailed", { error: String(e) }));
    }
  };

  return (
    <>
      <aside
        style={{ width: sidebarWidth }}
        className={`flex shrink-0 flex-col overflow-hidden ${CARD}`}
      >
        {/* No workspace switcher here any more: it is in the header, where it is reachable from
            every module instead of only the three this panel serves. */}
        <div className="min-h-0 flex-1 overflow-y-auto px-3 pb-3 pt-3">
          <div className="mb-1 flex items-center justify-between px-1">
            <span className="text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
              {t("sidebar.projects")}
            </span>
            {/* These were the app's last three sub-24px controls — 20px boxes named only by a native
                `title`, which is a tooltip and not an accessible name. */}
            <div className="-mr-1 flex items-center gap-0.5">
              {/* Deliberately here, above the project list, rather than inside one project's
                  Pull Requests section: the whole point is that the link decides which repo
                  it belongs to. */}
              <IconButton label="prLink.menuItem" icon={Link2} onClick={openPrLinkModal} />
              {/* Both need a workspace to put the repository in. Disabled rather than silently
                  inert: a control that looks live and does nothing is the symptom that made a
                  backend failure read as a broken button. */}
              <IconButton
                label="sidebar.cloneRepo"
                icon={GitBranchPlus}
                disabled={!activeWorkspaceId}
                onClick={() => setShowCloneModal(true)}
              />
              <IconButton
                label="sidebar.addProject"
                icon={Plus}
                disabled={!activeWorkspaceId}
                onClick={handleAddProject}
              />
            </div>
          </div>

          <div className="space-y-0.5">
            {projects.map((project) => (
              <ProjectRow key={project.id} project={project} />
            ))}
            {projects.length === 0 && (
              <p className="px-1.5 py-1 text-ui text-[var(--cf-text-muted)]">{t("sidebar.noProjects")}</p>
            )}
          </div>
        </div>

        {showCloneModal && activeWorkspaceId && (
          <CloneRepoModal workspaceId={activeWorkspaceId} onClose={() => setShowCloneModal(false)} />
        )}
      </aside>
      <ResizeHandle
        axis="x"
        value={sidebarWidth}
        min={SIDEBAR_MIN}
        max={SIDEBAR_MAX}
        onChange={(w) => setSize("sidebarWidth", w)}
        onCommit={(w) => commitSize("sidebarWidth", w)}
      />
    </>
  );
}
