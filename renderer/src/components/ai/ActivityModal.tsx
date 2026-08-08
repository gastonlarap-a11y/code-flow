import { useMemo, useState } from "react";
import { useDialog } from "../../lib/useDialog";
import { Pencil, Search, Trash2, X } from "lucide-react";
import { IconButton } from "../common/IconButton";
import { Tooltip } from "../common/Tooltip";
import { useJobsStore, EMPTY_JOBS } from "../../state/jobsStore";
import { useChatHistoryStore, EMPTY_CONVERSATIONS } from "../../state/activityStore";
import { usePrStore } from "../../state/prStore";
import { useChatStore } from "../../state/chatStore";
import { useAiPanelStore } from "../../state/aiPanelStore";
import { confirmAction } from "../../state/confirmStore";
import { useT } from "../../state/languageStore";
import {
  mergeActivityEntries,
  entryKey,
  entryTitle,
  entryVisual,
  entryRunCount,
  findActiveEntryKey,
  type ActivityEntry,
} from "../../lib/activityEntries";

export function ActivityModal({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const t = useT();
  const jobs = useJobsStore((s) => s.byProject[projectId] ?? EMPTY_JOBS);
  const renameJob = useJobsStore((s) => s.rename);
  const removeJob = useJobsStore((s) => s.remove);
  const conversations = useChatHistoryStore((s) => s.byProject[projectId] ?? EMPTY_CONVERSATIONS);
  const removeConversation = useChatHistoryStore((s) => s.remove);
  const renameConversation = useChatHistoryStore((s) => s.rename);
  const prsByProject = usePrStore((s) => s.prsByProject);
  const selectedPr = usePrStore((s) => s.selectedPr);
  const selectPr = usePrStore((s) => s.selectPr);
  const analyzeOpen = useAiPanelStore((s) => s.tab === "analyze");
  const analyzeJobId = useAiPanelStore((s) => s.selectedJobId);
  const activeSessionId = useChatStore((s) => s.byProject[projectId]?.conversationId ?? null);
  const switchTo = useChatStore((s) => s.switchTo);
  const [query, setQuery] = useState("");
  const [renamingKey, setRenamingKey] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState("");

  const entries = useMemo(() => mergeActivityEntries(jobs, conversations), [jobs, conversations]);
  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return entries;
    return entries.filter((e) => entryTitle(e).toLowerCase().includes(q));
  }, [entries, query]);

  const activeEntryKey = findActiveEntryKey(entries, {
    selectedPrId: selectedPr?.id ?? null,
    analyzeOpen,
    analyzeJobId,
    activeSessionId,
  });

  const open = (entry: ActivityEntry) => {
    if (entry.type === "chat") {
      // `selectPr(null)` also moves the panel to the chat tab, which is where the conversation
      // being opened will show up.
      selectPr(null);
      void switchTo(projectId, entry.conv.session_id);
      // A row whose newest entry is a decision still opens the PR it was taken on.
    } else if (entry.job.kind === "pr-review" || entry.job.kind === "pr-action") {
      const pr = prsByProject[projectId]?.find((p) => p.id === entry.job.meta.prId);
      if (!pr) return;
      selectPr(pr);
    } else if (entry.job.kind === "analyze-changes") {
      useAiPanelStore.getState().showAnalyzeJob(entry.job.id);
    }
    onClose();
  };

  const handleDelete = async (entry: ActivityEntry) => {
    const runs = entry.type === "job" ? entry.runs.length : 1;
    // When the activity bundles history (a PR / pre-commit with several runs) spell that out, so a
    // single click deleting the whole thing isn't a surprise.
    const message = runs > 1 ? t("ai.confirmDeleteWithHistory", { n: runs }) : t("chatHistory.confirmDelete");
    if (!(await confirmAction(message))) return;
    // Deleting a chat updates the persisted conversation list; the chat panel reconciles against
    // that list and resets itself if the open conversation no longer exists (see AiPanel
    // ChatSection) — which also covers a chat that spans several session ids.
    if (entry.type === "chat") await removeConversation(projectId, entry.conv.session_id);
    // A job row owns every run of that activity — remove them all so the whole history goes, not
    // just the latest run (which would leave the row behind with one fewer run each click).
    else await Promise.all(entry.runs.map((j) => removeJob(projectId, j.id)));
  };

  const startRename = (entry: ActivityEntry) => {
    setRenamingKey(entryKey(entry));
    setRenameValue(entryTitle(entry));
  };

  const commitRename = async (entry: ActivityEntry) => {
    const title = renameValue.trim();
    setRenamingKey(null);
    if (!title || title === entryTitle(entry)) return;
    if (entry.type === "chat") await renameConversation(projectId, entry.conv.session_id, title);
    else await renameJob(projectId, entry.job.id, title);
  };

  const { titleId, dialogProps } = useDialog();


  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center bg-black/30 pt-16" onClick={onClose}>
      <div
        {...dialogProps}
        onClick={(e) => e.stopPropagation()}
        className="flex max-h-[75vh] w-[540px] flex-col overflow-hidden rounded-xl border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] shadow-[var(--cf-shadow)]"
      >
        <div className="flex items-center justify-between border-b border-[var(--cf-border)] px-3 py-2">
          {/* A real heading: it is what `aria-labelledby` points at. */}
          <h2 id={titleId} className="text-relaxed font-semibold">
            {t("ai.activityModalTitle")}
          </h2>
          <IconButton label="common.close" icon={X} onClick={onClose} />
        </div>

        <div className="flex items-center gap-2 border-b border-[var(--cf-border)] px-3 py-2">
          <Search size={13} className="shrink-0 text-[var(--cf-text-muted)]" />
          <input
            autoFocus
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => e.key === "Escape" && onClose()}
            placeholder={t("ai.activitySearch")}
            aria-label={t("ai.activitySearch")}
            className="flex-1 bg-transparent text-body outline-none"
          />
        </div>

        <div className="flex-1 overflow-auto p-2">
          {filtered.length === 0 ? (
            <p className="px-2 py-6 text-center text-body text-[var(--cf-text-muted)]">{t("ai.noMatches")}</p>
          ) : (
            <div className="space-y-1">
              {filtered.map((entry) => {
                const { icon: Icon, color, spinning } = entryVisual(entry);
                const isActive = entryKey(entry) === activeEntryKey;
                const isRenaming = renamingKey === entryKey(entry);
                return (
                  <div
                    key={entryKey(entry)}
                    className={`group flex items-center gap-2 rounded-lg border p-2.5 ${
                      isActive
                        ? "border-[var(--cf-accent)] bg-[var(--cf-accent-soft)]"
                        : "border-[var(--cf-border)] hover:bg-black/[0.02] dark:hover:bg-white/[0.03]"
                    }`}
                  >
                    {isRenaming ? (
                      <input
                        autoFocus
                        value={renameValue}
                        onChange={(e) => setRenameValue(e.target.value)}
                        onBlur={() => void commitRename(entry)}
                        onKeyDown={(e) => {
                          if (e.key === "Enter") void commitRename(entry);
                          else if (e.key === "Escape") setRenamingKey(null);
                        }}
                        aria-label={t("ai.rename")}
                        className="min-w-0 flex-1 rounded-md border border-[var(--cf-accent)] bg-transparent px-1.5 py-0.5 text-body font-medium text-[var(--cf-text)] outline-none"
                      />
                    ) : (
                      <button onClick={() => open(entry)} className="flex min-w-0 flex-1 items-center gap-2 text-left">
                        <Icon size={13} className={spinning ? "shrink-0 animate-spin" : "shrink-0"} style={{ color }} />
                        <div className="min-w-0 flex-1">
                          <div className="flex items-center gap-1.5">
                            <p className="truncate text-body font-medium text-[var(--cf-text)]">{entryTitle(entry)}</p>
                            {entryRunCount(entry) > 1 && (
                              <Tooltip label={t("ai.runCount", { n: entryRunCount(entry) })}>
                                <span className="shrink-0 rounded-full bg-black/[0.06] px-1.5 text-badge font-semibold text-[var(--cf-text-muted)] dark:bg-white/[0.1]">
                                  ×{entryRunCount(entry)}
                                </span>
                              </Tooltip>
                            )}
                          </div>
                          <p className="mt-0.5 text-badge text-[var(--cf-text-muted)]">
                            {new Date(
                              entry.type === "job" ? entry.job.createdAt : entry.conv.updated_at,
                            ).toLocaleString()}
                          </p>
                        </div>
                      </button>
                    )}
                    {/* Dimmed, never hidden — `opacity-0` puts these out of reach of a keyboard
                        and of a touch screen. */}
                    {!isRenaming && (
                      <div className="flex shrink-0 items-center gap-1 opacity-55 group-hover:opacity-100 group-focus-within:opacity-100">
                        <IconButton label="ai.rename" icon={Pencil} onClick={() => startRename(entry)} />
                        <IconButton
                          label="chatHistory.delete"
                          icon={Trash2}
                          variant="danger"
                          onClick={() => void handleDelete(entry)}
                        />
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
