import { useState } from "react";
import {
  ChevronDown,
  ChevronRight,
  CircleDot,
  Code2,
  Folder,
  FolderInput,
  GitBranch,
  GitMerge,
  Loader2,
  Plus,
  Trash2,
  Unlink,
} from "lucide-react";
import { useWorkspaceStore } from "../../../state/workspaceStore";
import { useRepoStore } from "../../../state/repoStore";
import { useUiStore } from "../../../state/uiStore";
import { revealInFileManager, openInVsCode } from "../../../lib/ipc/commands";
import type { Project } from "../../../types/domain";
import { CollapsibleSection } from "../../common/CollapsibleSection";
import { SkeletonRows } from "../../common/Skeleton";
import { IconButton } from "../../common/IconButton";
import { RowActions } from "../../common/RowActions";
import { confirmAction } from "../../../state/confirmStore";
import { pushErrorToast } from "../../../state/toastStore";
import { useT } from "../../../state/languageStore";
import {
  RemoteBranchesSection,
  RemoteUrlSection,
  StashesSection,
  UnpushedCommitsSection,
  CreateBranchForm,
} from "./GitSection";
import { PullRequestsSection } from "./PullRequestsSection";

export function ProjectRow({ project }: { project: Project }) {
  const activeProjectId = useWorkspaceStore((s) => s.activeProjectId);
  const setActiveProject = useWorkspaceStore((s) => s.setActiveProject);
  const workspaces = useWorkspaceStore((s) => s.workspaces);
  const moveProject = useWorkspaceStore((s) => s.moveProject);
  const branches = useRepoStore((s) => s.branches);
  const checkoutBranch = useRepoStore((s) => s.checkoutBranch);
  const checkoutDetached = useRepoStore((s) => s.checkoutDetached);
  const deleteBranch = useRepoStore((s) => s.deleteBranch);
  const mergeBranch = useRepoStore((s) => s.mergeBranch);
  const checkingOutBranch = useRepoStore((s) => s.checkingOutBranch);
  const projectLoading = useRepoStore((s) => s.projectLoading);
  const repoPath = useRepoStore((s) => s.repoPath);
  const setActiveView = useUiStore((s) => s.setActiveView);
  const t = useT();

  const isActive = project.id === activeProjectId;
  /**
   * Whether this project's git state is still on its way.
   *
   * Derived from the repo the store is actually pointing at, not from `projectLoading` alone. That
   * flag is set by an effect, so on the first render after a click it still reads `false` from the
   * *previous* project: the detail block below would mount against stale data, unmount a frame later
   * when the flag caught up, and mount a third time when loading finished — remounting
   * `PullRequestsSection`, and re-running its host detection, twice per switch. Comparing the paths
   * is true from the very first render.
   */
  const loading = projectLoading || repoPath !== project.local_path;
  const [expanded, setExpanded] = useState(isActive);
  const [showCreateBranch, setShowCreateBranch] = useState(false);
  const [revealing, setRevealing] = useState(false);
  const [openingVsCode, setOpeningVsCode] = useState(false);

  const select = () => {
    setActiveProject(project.id);
    setExpanded(true);
  };

  const otherWorkspaces = workspaces.filter((w) => w.id !== project.workspace_id);

  return (
    <div>
      <div
        className={`group relative flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-body ${
          isActive
            ? "bg-[var(--cf-accent-soft)] text-[var(--cf-text)]"
            : "text-[var(--cf-text-muted)] hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
        }`}
      >
        {/* The project's colour chip and its "reveal in the file manager" button are one control.
            It keeps the swatch background, so the colour beats the variant's own text colour. */}
        <IconButton
          label="sidebar.revealInFileManager"
          icon={Folder}
          pending={revealing}
          onClick={async (e: React.MouseEvent) => {
            e.stopPropagation();
            setRevealing(true);
            try {
              await revealInFileManager(project.local_path);
            } finally {
              setRevealing(false);
            }
          }}
          className="shrink-0 !text-white"
          style={{ background: project.color }}
        />
        {/* `h-6`, not the intrinsic height of one line of text: this is the row's primary action
            and it was a 20px-tall target. */}
        <button onClick={select} className="flex h-6 flex-1 min-w-0 items-center gap-2 text-left">
          <span className="flex-1 min-w-0 truncate font-medium">{project.name}</span>
        </button>
        {/* Everything this row can do, in one place that is always there. "Move to workspace" used
            to be a hand-rolled dropdown with its own open state, positioned absolutely under the
            row; it is now one entry per destination, which is the same choice with none of the
            plumbing. */}
        <RowActions
          className="shrink-0"
          actions={[
            {
              id: "vscode",
              labelKey: "sidebar.openInVsCode",
              icon: Code2,
              disabled: openingVsCode,
              onSelect: () => {
                setOpeningVsCode(true);
                void openInVsCode(project.local_path)
                  .catch((err: unknown) => pushErrorToast(String(err)))
                  .finally(() => setOpeningVsCode(false));
              },
            },
            ...otherWorkspaces.map((ws) => ({
              id: `move-${ws.id}`,
              labelKey: "sidebar.moveToNamedWorkspace" as const,
              labelParams: { name: ws.name },
              icon: FolderInput,
              onSelect: () => void moveProject(project.id, project.workspace_id, ws.id),
            })),
          ]}
        />
        {/* Never hidden: the chevron is what says this row has something under it. An expander you
            can only find by hovering is a row that looks like it does not expand. */}
        {isActive && (
          <IconButton
            label={expanded ? "sidebar.collapseProject" : "sidebar.expandProject"}
            icon={expanded ? ChevronDown : ChevronRight}
            onClick={(e: React.MouseEvent) => {
              e.stopPropagation();
              setExpanded((v) => !v);
            }}
            className="shrink-0"
          />
        )}

      </div>

      {isActive && expanded && loading && (
        <div className="ml-6 mt-1 border-l border-[var(--cf-border)] pl-3">
          <SkeletonRows count={5} className="p-0" />
        </div>
      )}

      {isActive && expanded && !loading && (
        <div className="ml-6 mt-1 space-y-3 border-l border-[var(--cf-border)] pl-3">
          <CollapsibleSection
            icon={GitBranch}
            title={t("sidebar.localBranches")}
            action={
              <IconButton
                label="sidebar.newBranch"
                icon={Plus}
                onClick={() => setShowCreateBranch((v) => !v)}
                active={showCreateBranch}
              />
            }
          >
            {showCreateBranch && (
              <CreateBranchForm branches={branches} onDone={() => setShowCreateBranch(false)} />
            )}
            <div className="space-y-0.5">
              {branches
                .filter((b) => !b.is_remote)
                .map((b) => {
                  const isCheckingOut = checkingOutBranch === b.name;
                  return (
                    <div key={b.name} className="group flex items-center">
                      <button
                        onClick={() => checkoutBranch(b.name)}
                        disabled={checkingOutBranch !== null}
                        className={`flex flex-1 min-w-0 items-center gap-1.5 truncate rounded-md px-1.5 py-0.5 text-left text-body disabled:cursor-wait ${
                          b.is_head
                            ? "font-semibold text-[var(--cf-accent)]"
                            : "text-[var(--cf-text-muted)] hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
                        }`}
                      >
                        {isCheckingOut ? (
                          <Loader2 size={10} className="shrink-0 animate-spin" />
                        ) : (
                          <CircleDot size={10} className={`shrink-0 ${b.is_head ? "opacity-100" : "opacity-30"}`} />
                        )}
                        <span className="flex-1 min-w-0 truncate">{b.name}</span>
                        {(b.ahead > 0 || b.behind > 0) && (
                          <span className="shrink-0 text-badge text-[var(--cf-text-muted)]">
                            {b.ahead > 0 && `↑${b.ahead}`}
                            {b.behind > 0 && `↓${b.behind}`}
                          </span>
                        )}
                      </button>
                      <RowActions
                        className="ml-1 shrink-0"
                        actions={[
                          {
                            id: "detached",
                            labelKey: "sidebar.checkoutDetached",
                            icon: Unlink,
                            disabled: checkingOutBranch !== null,
                            onSelect: () => checkoutDetached(b.name),
                          },
                          // The current branch cannot be merged into itself, and deleting it is not
                          // a thing git will do — so those two are absent rather than disabled.
                          ...(b.is_head
                            ? []
                            : [
                                {
                                  id: "merge",
                                  labelKey: "sidebar.mergeIntoCurrent" as const,
                                  icon: GitMerge,
                                  disabled: checkingOutBranch !== null,
                                  onSelect: () => {
                                    void mergeBranch(b.name).then((outcome) => {
                                      if (outcome?.status === "conflicts") setActiveView("changes");
                                    });
                                  },
                                },
                                {
                                  id: "delete",
                                  labelKey: "sidebar.deleteBranch" as const,
                                  icon: Trash2,
                                  danger: true,
                                  onSelect: () => {
                                    void confirmAction(
                                      t("sidebar.deleteBranchConfirm", { name: b.name }),
                                    ).then((ok) => ok && void deleteBranch(b.name, false));
                                  },
                                },
                              ]),
                        ]}
                      />
                    </div>
                  );
                })}
              {branches.filter((b) => !b.is_remote).length === 0 && (
                <p className="px-1.5 text-ui text-[var(--cf-text-muted)]">{t("sidebar.noBranches")}</p>
              )}
            </div>
          </CollapsibleSection>

          <RemoteBranchesSection branches={branches} />

          <RemoteUrlSection />

          <StashesSection />

          <UnpushedCommitsSection />

          <PullRequestsSection project={project} />
        </div>
      )}
    </div>
  );
}
