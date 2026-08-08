import { useMemo, useState } from "react";
import {
  ChevronDown,
  ChevronRight,
  Clock,
  Code2,
  FileText,
  Folder,
  FolderTree,
  GitCommitHorizontal,
  List,
  ListMinus,
  ListPlus,
  Minus,
  Plus,
  RotateCcw,
  ShieldCheck,
  Sparkles,
  Trash2,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { useRepoStore } from "../../state/repoStore";
import { useLayoutStore } from "../../state/layoutStore";
import { DiffView } from "./DiffView";
import { EmptyState } from "../common/EmptyState";
import { SkeletonRows } from "../common/Skeleton";
import { ResizeHandle } from "../common/ResizeHandle";
import { CollapsibleSection } from "../common/CollapsibleSection";
import { generateCommitMessage, scanStagedSecrets } from "../../lib/ipc/commands";
import { diffToText } from "../../lib/diffText";
import { parseClaudeError, type ClaudeErrorInfo } from "../../lib/claudeError";
import { confirmAction } from "../../state/confirmStore";
import { fileStatusLabelKey } from "../../lib/fileStatus";
import { buildFileTree, type FileTreeNode } from "../../lib/buildFileTree";
import { useT } from "../../state/languageStore";
import { ConflictsBanner } from "./ConflictsBanner";
import { SecretScanModal } from "./SecretScanModal";
import { useUiStore } from "../../state/uiStore";
import { useAiPanelStore } from "../../state/aiPanelStore";
import { IconButton } from "../common/IconButton";
import { Button } from "../common/Button";
import { Tooltip } from "../common/Tooltip";
import { CARD } from "../common/panelChrome";
import { usePreferencesStore } from "../../state/preferencesStore";
import type { TranslationKey } from "../../lib/i18n/translations";
import type { FileDiffInfo, FileStatusEntry, SecretHit } from "../../types/domain";

const LIST_MIN = 220;
const LIST_MAX = 520;

/** 1-based line of the first change in a file's diff, so opening it in the editor lands on the
 * change instead of at the top of the file. A deleted line has no counterpart on the new side —
 * fall back to the first line the hunk does map, and to the file top when it maps none. */
function firstChangedLine(files: FileDiffInfo[]): number | undefined {
  for (const file of files) {
    for (const hunk of file.hunks) {
      const changed = hunk.lines.find((l) => l.origin !== " " && l.new_lineno !== null);
      if (changed?.new_lineno) return changed.new_lineno;
      const anchor = hunk.lines.find((l) => l.new_lineno !== null);
      if (anchor?.new_lineno) return anchor.new_lineno;
    }
  }
  return undefined;
}

function UnpushedCommitsSection() {
  const unpushedCommits = useRepoStore((s) => s.unpushedCommits);
  const undoCommit = useRepoStore((s) => s.undoCommit);
  const busy = useRepoStore((s) => s.busy);
  const t = useT();

  if (unpushedCommits.length === 0) return null;

  return (
    <div className="mb-3">
      <CollapsibleSection
        icon={GitCommitHorizontal}
        title={t("changes.unpushedCommits", { n: unpushedCommits.length })}
        defaultOpen
      >
        <p className="mb-1.5 px-1 text-badge text-[var(--cf-text-muted)]">{t("changes.unpushedHint")}</p>
        <div className="space-y-0.5">
          {unpushedCommits.map((c, i) => (
            <div
              key={c.id}
              className="flex items-center gap-2 rounded-md px-1.5 py-1 text-ui hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
            >
              <span className="flex-1 min-w-0 truncate">{c.summary}</span>
              <span className="shrink-0 font-mono text-badge text-[var(--cf-text-muted)]">{c.short_id}</span>
              {/* Only the newest commit can be undone, and the label says so on the others rather
                  than leaving a dead control with no explanation. */}
              <IconButton
                label={i === 0 ? "changes.undoThis" : "changes.undoAboveFirst"}
                icon={RotateCcw}
                variant="danger"
                disabled={i !== 0 || busy}
                onClick={async () => {
                  if (await confirmAction(t("changes.undoConfirm", { summary: c.summary }))) {
                    void undoCommit(c.id);
                  }
                }}
                className="shrink-0"
              />
            </div>
          ))}
        </div>
      </CollapsibleSection>
    </div>
  );
}

interface FileAction {
  icon: LucideIcon;
  /** Names the control for both the tooltip and the `aria-label` — `IconButton` requires it. */
  labelKey: TranslationKey;
  onClick: () => void;
  danger?: boolean;
  /** This specific action is the one currently in flight — swaps its icon for a spinner. */
  pending?: boolean;
  /** A *different* action (on this row or another) is in flight — dims and blocks clicks
   * so two git-index-mutating actions can never race each other. */
  disabled?: boolean;
}

function FileRow({
  entry,
  selected,
  onSelect,
  actions,
  depth = 0,
  displayName,
}: {
  entry: FileStatusEntry;
  selected: boolean;
  onSelect: () => void;
  actions: FileAction[];
  /** Tree mode nests files under their directory, so indent by depth instead of showing
   * the full path — and show just the filename, since the path is implied by the nesting. */
  depth?: number;
  displayName?: string;
}) {
  const t = useT();
  return (
    <div
      onClick={onSelect}
      style={depth ? { paddingLeft: depth * 14 } : undefined}
      className={`group flex items-center gap-2 rounded-md px-2 py-1 text-body cursor-pointer ${
        selected ? "bg-[var(--cf-accent-soft)]" : "hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
      }`}
    >
      {/* The status letter is the one thing on this row that needs decoding, so its meaning is a
          tooltip rather than an OS `title` nobody sees on a keyboard. */}
      <Tooltip label={t(fileStatusLabelKey(entry.status))}>
        <span className="w-4 shrink-0 text-center text-badge uppercase text-[var(--cf-text-muted)]">
          {entry.status[0]}
        </span>
      </Tooltip>
      <span className="flex-1 min-w-0 truncate font-mono text-ui">{displayName ?? entry.path}</span>
      {/* Dimmed, never hidden. Staging a file was invisible until the pointer happened to rest on
          its exact row — which is no affordance at all for a keyboard or a touch user. */}
      <span className="flex shrink-0 items-center gap-0.5 opacity-55 group-hover:opacity-100 group-focus-within:opacity-100">
        {actions.map((action) => (
          <IconButton
            key={action.labelKey}
            label={action.labelKey}
            icon={action.icon}
            pending={action.pending ?? false}
            disabled={action.disabled}
            variant={action.danger ? "danger" : "ghost"}
            onClick={(e: React.MouseEvent) => {
              e.stopPropagation();
              action.onClick();
            }}
          />
        ))}
      </span>
    </div>
  );
}

function FileTreeSection({
  entries,
  isSelected,
  onSelectEntry,
  buildActions,
}: {
  entries: FileStatusEntry[];
  isSelected: (entry: FileStatusEntry) => boolean;
  onSelectEntry: (entry: FileStatusEntry) => void;
  buildActions: (entry: FileStatusEntry) => FileAction[];
}) {
  const [collapsedDirs, setCollapsedDirs] = useState<Set<string>>(new Set());
  const tree = useMemo(() => buildFileTree(entries), [entries]);

  const toggleDir = (path: string) =>
    setCollapsedDirs((prev) => {
      const next = new Set(prev);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });

  const renderNode = (node: FileTreeNode, depth: number): React.ReactNode => {
    if (node.type === "file") {
      return (
        <FileRow
          key={node.entry.path}
          entry={node.entry}
          selected={isSelected(node.entry)}
          onSelect={() => onSelectEntry(node.entry)}
          actions={buildActions(node.entry)}
          depth={depth}
          displayName={node.name}
        />
      );
    }
    const collapsed = collapsedDirs.has(node.path);
    return (
      <div key={node.path}>
        <div
          onClick={() => toggleDir(node.path)}
          style={{ paddingLeft: depth * 14 }}
          className="flex cursor-pointer items-center gap-1.5 rounded-md px-2 py-1 text-ui text-[var(--cf-text-muted)] hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
        >
          {collapsed ? <ChevronRight size={12} className="shrink-0" /> : <ChevronDown size={12} className="shrink-0" />}
          <Folder size={12} className="shrink-0" />
          <span className="truncate">{node.name}</span>
        </div>
        {!collapsed && node.children.map((child) => renderNode(child, depth + 1))}
      </div>
    );
  };

  return <>{tree.map((node) => renderNode(node, 0))}</>;
}

export function ChangesPanel() {
  const repoPath = useRepoStore((s) => s.repoPath);
  const status = useRepoStore((s) => s.status);
  const workingDiff = useRepoStore((s) => s.workingDiff);
  const stagedDiff = useRepoStore((s) => s.stagedDiff);
  const stageFile = useRepoStore((s) => s.stageFile);
  const unstageFile = useRepoStore((s) => s.unstageFile);
  const stageAll = useRepoStore((s) => s.stageAll);
  const unstageAll = useRepoStore((s) => s.unstageAll);
  const discardFile = useRepoStore((s) => s.discardFile);
  const discardAll = useRepoStore((s) => s.discardAll);
  const commitChanges = useRepoStore((s) => s.commitChanges);
  const busy = useRepoStore((s) => s.busy);
  const merging = useRepoStore((s) => s.merging);
  const conflicts = useRepoStore((s) => s.conflicts);
  const checkingOutBranch = useRepoStore((s) => s.checkingOutBranch);
  const projectLoading = useRepoStore((s) => s.projectLoading);
  const listWidth = useLayoutStore((s) => s.sizes.changesListWidth);
  const setSize = useLayoutStore((s) => s.setSize);
  const commitSize = useLayoutStore((s) => s.commitSize);

  const [selected, setSelected] = useState<{ path: string; staged: boolean } | null>(null);
  const [viewMode, setViewMode] = useState<"list" | "tree">("list");
  const openAiPanel = useUiStore((s) => s.openAiPanel);
  const openInEditor = useUiStore((s) => s.openInEditor);
  const [message, setMessage] = useState("");
  const [aiBusy, setAiBusy] = useState(false);
  const [aiError, setAiError] = useState<ClaudeErrorInfo | null>(null);
  const [scanning, setScanning] = useState(false);
  const [secretHits, setSecretHits] = useState<SecretHit[] | null>(null);
  const secretScanEnabled = usePreferencesStore((s) => s.secretScanEnabled);
  const [pending, setPending] = useState<{ path: string; kind: "stage" | "unstage" | "discard" | "all" } | null>(
    null,
  );
  const t = useT();

  // Feedback for the row action buttons is otherwise invisible until refreshStatus() comes
  // back (stage/unstage/discard all trigger a full status+diff refresh) — set the pending
  // state synchronously on click so the button shows a spinner immediately, and block the
  // other git-mutating buttons meanwhile so two of them can never race the same index.
  const runAction = async (path: string, kind: "stage" | "unstage" | "discard" | "all", fn: () => Promise<void>) => {
    setPending({ path, kind });
    try {
      await fn();
    } finally {
      setPending(null);
    }
  };

  const unstagedAndUntracked = useMemo(
    () => [...(status?.unstaged ?? []), ...(status?.untracked ?? [])],
    [status],
  );

  const selectedDiff = useMemo(() => {
    if (!selected) return [];
    const pool = selected.staged ? stagedDiff : workingDiff;
    return pool.filter((f) => (f.new_path ?? f.old_path) === selected.path);
  }, [selected, stagedDiff, workingDiff]);

  // Open the file in the app's own Editor tab (at the first changed line) instead of handing it
  // to the OS default app — the point is to inspect the change in place, not to leave the app.
  const openFile = (relPath: string, staged: boolean) => {
    const pool = staged ? stagedDiff : workingDiff;
    const files = pool.filter((f) => (f.new_path ?? f.old_path) === relPath);
    openInEditor(relPath, firstChangedLine(files));
  };

  if (!status) {
    // Both ways the state is empty *while a repository exists* — a checkout clears the working tree
    // because it belongs to the branch being left, and switching project clears everything before
    // reloading it. Either way there is a repository, it is just mid-switch, and saying "no
    // repository" is worse than the flicker it replaced. `checkingOutBranch` covered the first case
    // only, which is why every project switch flashed this empty state on its way through.
    return checkingOutBranch || projectLoading ? (
      <SkeletonRows count={8} className="cf-fade-in" />
    ) : (
      <EmptyState icon={FileText} title={t("changes.noRepo")} />
    );
  }

  const buildStagedActions = (entry: FileStatusEntry): FileAction[] => {
    const isPending = pending?.path === entry.path;
    const blocked = pending !== null && !isPending;
    return [
      { icon: Code2, labelKey: "changes.openInEditor", onClick: () => openFile(entry.path, true) },
      {
        icon: Minus,
        labelKey: "changes.unstage",
        onClick: () => runAction(entry.path, "unstage", () => unstageFile(entry.path)),
        pending: isPending && pending?.kind === "unstage",
        disabled: blocked,
      },
    ];
  };

  const buildUnstagedActions = (entry: FileStatusEntry): FileAction[] => {
    const isPending = pending?.path === entry.path;
    const blocked = pending !== null && !isPending;
    return [
      { icon: Code2, labelKey: "changes.openInEditor", onClick: () => openFile(entry.path, false) },
      {
        icon: Plus,
        labelKey: "changes.stage",
        onClick: () => runAction(entry.path, "stage", () => stageFile(entry.path)),
        pending: isPending && pending?.kind === "stage",
        disabled: blocked,
      },
      {
        // A trash can, not a circular arrow: discarding throws the change away (and deletes the
        // file outright when it's untracked) — the arrow reads as "reload/restart" and undersells it.
        icon: Trash2,
        labelKey: "changes.discardChanges",
        danger: true,
        onClick: async () => {
          if (await confirmAction(t("changes.discardConfirm", { path: entry.path }))) {
            void runAction(entry.path, "discard", () => discardFile(entry.path));
          }
        },
        pending: isPending && pending?.kind === "discard",
        disabled: blocked,
      },
    ];
  };

  const generateWithAi = async () => {
    setAiError(null);
    setAiBusy(true);
    try {
      const text = await generateCommitMessage(diffToText(stagedDiff));
      setMessage(text);
    } catch (e) {
      setAiError(parseClaudeError(String(e)));
    } finally {
      setAiBusy(false);
    }
  };

  const performCommit = async () => {
    await commitChanges(message.trim());
    setMessage("");
  };

  // Commit entry point: run the pre-commit secret scan first (when enabled). If it finds
  // credential-looking content, hold the commit and surface the SecretScanModal instead. A scan
  // that itself errors must not block committing — fall through to the commit in that case.
  const handleCommit = async () => {
    if (secretScanEnabled && repoPath) {
      setScanning(true);
      try {
        const hits = await scanStagedSecrets(repoPath);
        if (hits.length > 0) {
          setSecretHits(hits);
          return;
        }
      } catch {
        // scan failed — don't stand in the way of the commit
      } finally {
        setScanning(false);
      }
    }
    await performCommit();
  };

  return (
    <div className="flex h-full min-h-0 flex-col">
      {/* Not only while merging: a stash that conflicts marks the index without a `MERGE_HEAD`,
          and those conflicts need the same resolution UI. */}
      {(merging || conflicts.length > 0) && <ConflictsBanner />}
      <div className="relative flex min-h-0 flex-1 gap-1.5">
      <div
        style={{ width: listWidth }}
        className={`flex shrink-0 flex-col overflow-hidden ${CARD}`}
      >
        <div className="flex items-center justify-between border-b border-[var(--cf-border)] px-3 py-2">
          <span className="text-ui font-semibold text-[var(--cf-text-muted)]">{t("changes.changes")}</span>
          <div className="flex items-center gap-0.5 rounded-md border border-[var(--cf-border)] p-0.5">
            <IconButton
              label="changes.listView"
              icon={List}
              active={viewMode === "list"}
              onClick={() => setViewMode("list")}
              className={viewMode === "list" ? "bg-[var(--cf-accent-soft)]" : ""}
            />
            <IconButton
              label="changes.treeView"
              icon={FolderTree}
              active={viewMode === "tree"}
              onClick={() => setViewMode("tree")}
              className={viewMode === "tree" ? "bg-[var(--cf-accent-soft)]" : ""}
            />
          </div>
        </div>

        <div className="flex-1 overflow-auto p-2">
          <UnpushedCommitsSection />

          <div className="mb-3">
            <div className="mb-1 flex items-center justify-between px-1">
              <span className="text-badge font-semibold uppercase text-[var(--cf-text-muted)]">
                {t("changes.staged")} ({status.staged.length})
              </span>
              {status.staged.length > 0 && (
                <IconButton
                  label="changes.unstageAll"
                  icon={ListMinus}
                  pending={pending?.path === "__unstage_all__"}
                  disabled={pending !== null && pending.path !== "__unstage_all__"}
                  onClick={() => runAction("__unstage_all__", "all", () => unstageAll())}
                />
              )}
            </div>
            {viewMode === "tree" ? (
              <FileTreeSection
                entries={status.staged}
                isSelected={(entry) => selected?.path === entry.path && !!selected.staged}
                onSelectEntry={(entry) => setSelected({ path: entry.path, staged: true })}
                buildActions={(entry) => buildStagedActions(entry)}
              />
            ) : (
              status.staged.map((entry) => (
                <FileRow
                  key={entry.path}
                  entry={entry}
                  selected={selected?.path === entry.path && selected.staged}
                  onSelect={() => setSelected({ path: entry.path, staged: true })}
                  actions={buildStagedActions(entry)}
                />
              ))
            )}
          </div>

          <div>
            <div className="mb-1 flex items-center justify-between px-1">
              <span className="text-badge font-semibold uppercase text-[var(--cf-text-muted)]">
                {t("changes.changes")} ({unstagedAndUntracked.length})
              </span>
              <div className="flex items-center gap-1">
                {unstagedAndUntracked.length > 0 && (
                  <IconButton
                    label="analyze.button"
                    icon={ShieldCheck}
                    onClick={() => {
                      useAiPanelStore.getState().showAnalyze();
                      openAiPanel();
                    }}
                  />
                )}
                {unstagedAndUntracked.length > 0 && (
                  <IconButton
                    label="changes.discardAll"
                    icon={Trash2}
                    variant="danger"
                    pending={pending?.path === "__discard_all__"}
                    disabled={pending !== null && pending.path !== "__discard_all__"}
                    onClick={() => {
                      void confirmAction(
                        t("changes.discardAllConfirm", { n: unstagedAndUntracked.length }),
                        true,
                        t("changes.discardAll"),
                      ).then((ok) => {
                        if (!ok) return;
                        // The selected file may be one of the ones about to vanish — drop the
                        // selection rather than leaving the diff pane on a file that no longer differs.
                        if (selected && !selected.staged) setSelected(null);
                        void runAction("__discard_all__", "all", () => discardAll());
                      });
                    }}
                  />
                )}
                {unstagedAndUntracked.length > 0 && (
                  <IconButton
                    label="changes.stageAll"
                    icon={ListPlus}
                    pending={pending?.path === "__stage_all__"}
                    disabled={pending !== null && pending.path !== "__stage_all__"}
                    onClick={() => runAction("__stage_all__", "all", () => stageAll())}
                  />
                )}
              </div>
            </div>
            {viewMode === "tree" ? (
              <FileTreeSection
                entries={unstagedAndUntracked}
                isSelected={(entry) => selected?.path === entry.path && !selected.staged}
                onSelectEntry={(entry) => setSelected({ path: entry.path, staged: false })}
                buildActions={(entry) => buildUnstagedActions(entry)}
              />
            ) : (
              unstagedAndUntracked.map((entry) => (
                <FileRow
                  key={entry.path}
                  entry={entry}
                  selected={selected?.path === entry.path && !selected.staged}
                  onSelect={() => setSelected({ path: entry.path, staged: false })}
                  actions={buildUnstagedActions(entry)}
                />
              ))
            )}
          </div>
        </div>

        <div className="border-t border-[var(--cf-border)] p-2">
          <div className="relative">
            <textarea
              value={message}
              onChange={(e) => setMessage(e.target.value)}
              placeholder={t("changes.commitMessage")}
              rows={3}
              disabled={aiBusy}
              className="w-full resize-none rounded-md border border-[var(--cf-border)] bg-transparent px-2 py-1.5 pr-9 text-body outline-none focus:border-[var(--cf-accent)] disabled:opacity-50"
            />
            {/* The label carries the reason it is disabled — with nothing staged there is no diff to
                write a message from, and a dead sparkle explains none of that. */}
            <IconButton
              label={status.staged.length === 0 ? "changes.stageFirst" : "changes.generateWithAi"}
              icon={Sparkles}
              pending={aiBusy}
              disabled={status.staged.length === 0}
              onClick={generateWithAi}
              className="absolute right-1.5 top-1.5 !text-[var(--cf-accent)]"
            />
          </div>
          {aiError &&
            (aiError.isQuotaExceeded ? (
              <div className="mt-1.5 flex items-start gap-2 rounded-md bg-[color-mix(in_oklab,var(--cf-warning)_14%,transparent)] px-2 py-1.5 text-badge text-[var(--cf-text)]">
                <Clock size={13} className="mt-0.5 shrink-0 text-[var(--cf-warning)]" />
                <span>
                  {t("changes.quotaMessage")}{" "}
                  {aiError.resetHint ? t("changes.quotaRetry", { hint: aiError.resetHint }) : t("changes.quotaRetryLater")}
                </span>
              </div>
            ) : (
              <p className="mt-1 text-badge text-[var(--cf-danger)]">{aiError.message}</p>
            ))}
          <Button
            variant="primary"
            pending={scanning}
            disabled={busy || aiBusy || !message.trim() || status.staged.length === 0}
            onClick={handleCommit}
            className="mt-2 w-full"
          >
            {scanning ? t("secrets.scanning") : t("changes.commit")}
            {!scanning && status.staged.length > 0 ? ` (${status.staged.length})` : ""}
          </Button>
        </div>
      </div>

      <ResizeHandle
        axis="x"
        value={listWidth}
        min={LIST_MIN}
        max={LIST_MAX}
        onChange={(w) => setSize("changesListWidth", w)}
        onCommit={(w) => commitSize("changesListWidth", w)}
      />

      <div className={`min-h-0 flex-1 overflow-hidden ${CARD}`}>
        {selected ? (
          <DiffView files={selectedDiff} />
        ) : (
          <EmptyState icon={FileText} title={t("changes.selectFile")} subtitle={t("changes.selectFileHint")} />
        )}
      </div>
      </div>
      {secretHits && (
        <SecretScanModal
          hits={secretHits}
          onCancel={() => setSecretHits(null)}
          onCommitAnyway={async () => {
            setSecretHits(null);
            await performCommit();
          }}
        />
      )}
    </div>
  );
}
