import { ArrowDown, ArrowUp, ChevronDown, Folder, GitBranch } from "lucide-react";
import { WorkspaceMenu } from "./WorkspaceMenu";
import { useRepoStore } from "../../state/repoStore";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { useUiStore } from "../../state/uiStore";
import { useT } from "../../state/languageStore";
import { useShortcutHint } from "../../lib/useShortcutHint";
import { Tooltip } from "../common/Tooltip";
import { Button } from "../common/Button";

/**
 * Where you are: workspace, project, branch — the three scopes the status bar used to spell out
 * along the bottom edge, now at the top where the rest of the context lives.
 *
 * They are pills rather than labels because two of the three became clickable in the process. The
 * old bar showed the workspace as plain text (with a comment explaining that making it look
 * clickable would promise something it did not do) and the project not at all beyond its name; both
 * now open the command bar scoped to their own list, which is the promise a pill should make.
 */

function ProjectPill() {
  const project = useWorkspaceStore((s) => s.activeProject());
  const openCommandPalette = useUiStore((s) => s.openCommandPalette);

  if (!project) return null;

  return (
    // The full path is the tooltip: the name alone does not disambiguate two checkouts of the same
    // repository, which is exactly when a user looks here.
    <Tooltip label={project.local_path}>
      <button
        onClick={() => openCommandPalette("projects")}
        className="cf-focusable flex h-7 min-w-0 items-center gap-1.5 rounded-control px-2 text-ui font-medium text-[var(--cf-text)] transition-colors hover:bg-black/[0.04] dark:hover:bg-white/[0.06]"
      >
        <Folder size={14} style={{ color: project.color }} aria-hidden />
        <span className="max-w-[140px] truncate">{project.name}</span>
        <ChevronDown size={12} className="shrink-0 text-[var(--cf-text-muted)]" aria-hidden />
      </button>
    </Tooltip>
  );
}

/**
 * The branch, with its ahead/behind counts folded in.
 *
 * The status bar kept these as a separate cluster a few pixels away. A header has less room than a
 * full-width footer, and the counts describe the branch rather than sitting beside it, so they moved
 * inside the control. Clicking opens the branch list — the modal that used to do this is gone; it is
 * the `#` scope of the command bar now.
 */
function BranchPill() {
  const status = useRepoStore((s) => s.status);
  const branches = useRepoStore((s) => s.branches);
  const openCommandPalette = useUiStore((s) => s.openCommandPalette);
  const t = useT();
  const hint = useShortcutHint();

  const current = branches.find((b) => b.is_head);
  const ahead = current?.ahead ?? 0;
  const behind = current?.behind ?? 0;

  return (
    <Button
      variant="ghost"
      size="sm"
      icon={GitBranch}
      tooltip={hint("branch.switcher", t("shortcuts.cmdBranchSwitcher"))}
      onClick={() => openCommandPalette("branches")}
      className="!text-[var(--cf-text)]"
    >
      <span className="max-w-[160px] truncate">
        {status?.current_branch ?? (status?.is_detached ? t("statusbar.detachedHead") : "—")}
      </span>
      {(ahead > 0 || behind > 0) && (
        <span className="flex items-center gap-1 text-[var(--cf-text-muted)] tabular-nums">
          {ahead > 0 && (
            <span className="flex items-center gap-0.5">
              <ArrowUp size={11} aria-hidden />
              {ahead}
            </span>
          )}
          {behind > 0 && (
            <span className="flex items-center gap-0.5">
              <ArrowDown size={11} aria-hidden />
              {behind}
            </span>
          )}
        </span>
      )}
      <ChevronDown size={12} className="text-[var(--cf-text-muted)]" aria-hidden />
    </Button>
  );
}

export function HeaderContext() {
  const project = useWorkspaceStore((s) => s.activeProject());

  return (
    <div className="flex min-w-0 items-center gap-1">
      {/* A real menu rather than a label: it lists the workspaces, switches between them and is the
          only place in the app that creates one. It used to be a pill that opened the command
          palette, which switched but could not create — and without a chevron nobody read it as a
          control at all. */}
      <WorkspaceMenu />
      {project && (
        <>
          <span className="h-3 w-px shrink-0 bg-[var(--cf-border)]" aria-hidden />
          <ProjectPill />
          <span className="h-3 w-px shrink-0 bg-[var(--cf-border)]" aria-hidden />
          <BranchPill />
        </>
      )}
    </div>
  );
}
