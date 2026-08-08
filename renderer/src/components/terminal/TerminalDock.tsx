import { Suspense, useState } from "react";
import { lazyRetry } from "../../lib/lazyRetry";
import { motion } from "framer-motion";
import { ChevronDown, Pencil, Plus, SplitSquareHorizontal, TerminalSquare, X } from "lucide-react";
import { IconButton } from "../common/IconButton";
import { Tooltip } from "../common/Tooltip";
import { activeGroup, useTerminalStore, type TerminalTab } from "../../state/terminalStore";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { useLayoutStore } from "../../state/layoutStore";
import { ResizeHandle } from "../common/ResizeHandle";
import { CARD } from "../common/panelChrome";
// xterm and its CSS, which nothing else in the renderer imports. The boundary is here rather than
// around `TerminalDock` in `App.tsx` on purpose: `AnimatePresence` needs the animated component as
// its direct child to intercept the unmount, and this component's root is the `motion.div` that
// carries `exit`. A `Suspense` between the two would leave the dock without its slide-out.
const TerminalPane = lazyRetry(() =>
  import("./TerminalPane").then((m) => ({ default: m.TerminalPane })),
);
import { useT } from "../../state/languageStore";
import { EmptyState } from "../common/EmptyState";

const MIN_HEIGHT = 120;
const MAX_HEIGHT = 640;

/** Rendered by App.tsx inside an `AnimatePresence` so mount/unmount slides the dock in/out. */
export function TerminalDock() {
  const t = useT();
  const project = useWorkspaceStore((s) => s.activeProject());
  const byProject = useTerminalStore((s) => s.byProject);
  const openNew = useTerminalStore((s) => s.openNew);
  const closeTab = useTerminalStore((s) => s.close);
  const focus = useTerminalStore((s) => s.focus);
  const rename = useTerminalStore((s) => s.rename);
  const togglePanel = useTerminalStore((s) => s.togglePanel);
  const height = useLayoutStore((s) => s.sizes.terminalPanelHeight);
  const setSize = useLayoutStore((s) => s.setSize);
  const commitSize = useLayoutStore((s) => s.commitSize);

  const activeProjectId = project?.id ?? null;
  const activeProj = activeProjectId ? byProject[activeProjectId] : undefined;
  const visibleIds = activeGroup(activeProj);

  // Inline tab renaming — same start/commit-on-blur-or-Enter/cancel-on-Escape shape the
  // activity list uses, so both rename affordances in the app behave identically.
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState("");

  const startRename = (tab: TerminalTab) => {
    setRenamingId(tab.id);
    setRenameValue(tab.title);
  };

  const commitRename = () => {
    const id = renamingId;
    setRenamingId(null);
    if (id && project) rename(project.id, id, renameValue);
  };

  // Every terminal ever opened — across every project — stays mounted (hidden via CSS unless
  // it belongs to the active project *and* is part of its currently active split group), so
  // switching projects never kills a shell or discards its scrollback; only explicitly closing
  // a tab does.
  const allPanes = Object.entries(byProject).flatMap(([projectId, proj]) =>
    proj.tabs.map((tab) => ({
      projectId,
      tab,
      visible: projectId === activeProjectId && visibleIds.includes(tab.id),
    })),
  );

  return (
    <motion.div
      initial={{ height: 0, opacity: 0 }}
      animate={{ height, opacity: 1 }}
      exit={{ height: 0, opacity: 0 }}
      transition={{ duration: 0.18, ease: "easeOut" }}
      className={`flex shrink-0 flex-col overflow-hidden ${CARD}`}
    >
      <ResizeHandle
        axis="y"
        value={height}
        min={MIN_HEIGHT}
        max={MAX_HEIGHT}
        invert
        onChange={(h) => setSize("terminalPanelHeight", h)}
        onCommit={(h) => commitSize("terminalPanelHeight", h)}
      />
      <div className="flex h-8 shrink-0 items-center gap-1 border-b border-[var(--cf-border)] px-2">
        <TerminalSquare size={13} className="mr-1 shrink-0 text-[var(--cf-text-muted)]" />
        <div className="flex flex-1 items-center gap-1 overflow-x-auto">
          {(activeProj?.tabs ?? []).map((tab) => {
            const isVisible = visibleIds.includes(tab.id);
            const isRenaming = renamingId === tab.id;
            return (
              <div
                key={tab.id}
                onClick={() => project && !isRenaming && focus(project.id, tab.id)}
                // Guarded: while the editor is open this same handler still sees double
                // clicks bubbling out of the input, and re-starting the rename would reset
                // the field to the old title mid-edit.
                onDoubleClick={() => !isRenaming && startRename(tab)}
                className={`group flex shrink-0 cursor-pointer items-center gap-1.5 rounded-md px-2 py-1 text-ui ${
                  isVisible
                    ? "bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]"
                    : "text-[var(--cf-text-muted)] hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
                }`}
              >
                {isRenaming ? (
                  <input
                    autoFocus
                    value={renameValue}
                    onChange={(e) => setRenameValue(e.target.value)}
                    onClick={(e) => e.stopPropagation()}
                    onBlur={commitRename}
                    onKeyDown={(e) => {
                      if (e.key === "Enter") commitRename();
                      else if (e.key === "Escape") setRenamingId(null);
                    }}
                    aria-label={t("terminal.rename")}
                    className="w-28 min-w-0 rounded-sm border border-[var(--cf-accent)] bg-transparent px-1 text-ui text-[var(--cf-text)] outline-none"
                  />
                ) : (
                  <>
                    {/* Titled by hand, so it can be any length — truncate and let the tooltip
                        carry the full text rather than letting one tab shove the rest away. */}
                    <Tooltip label={tab.title}>
                      <span className="max-w-[150px] truncate">{tab.title}</span>
                    </Tooltip>
                    {/* Dimmed, never hidden: renaming a terminal was reachable only by resting the
                        pointer on its tab. */}
                    <IconButton
                      label="terminal.rename"
                      icon={Pencil}
                      className="opacity-55 group-hover:opacity-100 group-focus-within:opacity-100"
                      onClick={(e: React.MouseEvent) => {
                        e.stopPropagation();
                        startRename(tab);
                      }}
                    />
                    <IconButton
                      label="terminal.close"
                      icon={X}
                      onClick={(e: React.MouseEvent) => {
                        e.stopPropagation();
                        if (project) void closeTab(project.id, tab.id);
                      }}
                    />
                  </>
                )}
              </div>
            );
          })}
        </div>
        <IconButton
          label="terminal.new"
          icon={Plus}
          disabled={!project}
          onClick={() => project && void openNew(project.id, project.local_path)}
        />
        <IconButton
          label="terminal.split"
          icon={SplitSquareHorizontal}
          disabled={!project || (activeProj?.tabs.length ?? 0) === 0}
          onClick={() => project && void openNew(project.id, project.local_path, { split: true })}
        />
        <IconButton label="terminal.hide" icon={ChevronDown} onClick={togglePanel} />
      </div>
      <div className="relative flex min-h-0 flex-1">
        {!project ? (
          <div className="absolute inset-0">
            <EmptyState icon={TerminalSquare} title={t("terminal.noProject")} />
          </div>
        ) : (activeProj?.tabs.length ?? 0) === 0 ? (
          <div className="absolute inset-0">
            <EmptyState icon={TerminalSquare} title={t("terminal.emptyHint")} />
          </div>
        ) : null}
        {allPanes.map(({ tab, visible }) => (
          <div
            key={tab.id}
            className={
              visible
                ? `flex min-w-0 flex-1 flex-col ${tab.id !== visibleIds[visibleIds.length - 1] ? "border-r border-[var(--cf-border)]" : ""}`
                : "hidden"
            }
          >
            <Suspense fallback={null}>
              <TerminalPane sessionId={tab.id} visible={visible} />
            </Suspense>
          </div>
        ))}
      </div>
    </motion.div>
  );
}
