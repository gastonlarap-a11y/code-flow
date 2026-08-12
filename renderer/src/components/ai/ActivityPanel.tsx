import { useEffect, useMemo, useState } from "react";
import { ChevronDown, ChevronRight, History } from "lucide-react";
import { Button } from "../common/Button";
import { Tooltip } from "../common/Tooltip";
import {
  mergeActivityEntries,
  entryKey,
  entryTitle,
  entryTimestamp,
  entryVisual,
  entryRunCount,
  findActiveEntryKey,
  type ActivityEntry,
} from "../../lib/activityEntries";
import { useJobsStore, EMPTY_JOBS } from "../../state/jobsStore";
import { usePrStore } from "../../state/prStore";
import { useChatStore } from "../../state/chatStore";
import { useChatHistoryStore, EMPTY_CONVERSATIONS } from "../../state/activityStore";
import { useResolutionsStore } from "../../state/resolutionsStore";
import { useAiPanelStore } from "../../state/aiPanelStore";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";
import { ActivityModal } from "./ActivityModal";

function relativeTime(ts: number, t: (key: TranslationKey, vars?: Record<string, string | number>) => string): string {
  const mins = Math.round((Date.now() - ts) / 60000);
  if (mins < 1) return t("ai.justNow");
  if (mins < 60) return t("ai.minutesAgo", { n: mins });
  const hours = Math.round(mins / 60);
  if (hours < 24) return t("ai.hoursAgo", { n: hours });
  return t("ai.daysAgo", { n: Math.round(hours / 24) });
}

/** Unified "Activity" list — background jobs (PR review / pre-commit analysis) and past
 * chat conversations combined and sorted by recency, so there's one place to reopen anything
 * Claude has done for this project instead of two separate sections. */
export function ActivitySection({ projectId }: { projectId: string }) {
  const t = useT();
  const jobs = useJobsStore((s) => s.byProject[projectId] ?? EMPTY_JOBS);
  const jobsLoaded = useJobsStore((s) => s.loaded[projectId]);
  const loadJobHistory = useJobsStore((s) => s.load);
  const prsByProject = usePrStore((s) => s.prsByProject);
  const selectedPr = usePrStore((s) => s.selectedPr);
  const linkPr = usePrStore((s) => s.linkPr);
  const selectPr = usePrStore((s) => s.selectPr);
  const analyzeOpen = useAiPanelStore((s) => s.tab === "analyze");
  const analyzeJobId = useAiPanelStore((s) => s.selectedJobId);
  const conversations = useChatHistoryStore((s) => s.byProject[projectId] ?? EMPTY_CONVERSATIONS);
  const chatLoaded = useChatHistoryStore((s) => s.loaded[projectId]);
  const loadChatHistory = useChatHistoryStore((s) => s.load);
  const loadResolutions = useResolutionsStore((s) => s.load);
  const activeSessionId = useChatStore((s) => s.byProject[projectId]?.conversationId ?? null);
  const switchTo = useChatStore((s) => s.switchTo);
  const [collapsed, setCollapsed] = useState(true);
  const [showModal, setShowModal] = useState(false);

  useEffect(() => {
    if (!chatLoaded) void loadChatHistory(projectId);
    if (!jobsLoaded) void loadJobHistory(projectId);
    // Hydrate persisted "resolve with AI" outcomes so an already-resolved finding/comment shows
    // its ✓ state immediately when a PR/analysis is opened, instead of looking un-actioned.
    void loadResolutions(projectId);
  }, [projectId, chatLoaded, loadChatHistory, jobsLoaded, loadJobHistory, loadResolutions]);

  const entries = useMemo(() => mergeActivityEntries(jobs, conversations), [jobs, conversations]);
  if (entries.length === 0) return null;

  const activeEntryKey = findActiveEntryKey(entries, {
    // A link session's PR is shown the same way a selected one is, so its row highlights too.
    selectedPrId: selectedPr?.id ?? linkPr?.pr.id ?? null,
    analyzeOpen,
    analyzeJobId,
    activeSessionId,
  });

  const runningCount = jobs.filter((j) => j.status === "running").length;
  const topFive = entries.slice(0, 5);

  const openEntry = (entry: ActivityEntry) => {
    if (entry.type === "chat") {
      // `selectPr(null)` drops any PR *and* moves the panel to the chat tab, which is where this
      // conversation is about to appear. It used to take a second call to say the same thing.
      selectPr(null);
      void switchTo(projectId, entry.conv.session_id);
      return;
    }
    // A recorded decision opens the PR it was taken on, same as a review of it would.
    if (entry.job.kind === "pr-review" || entry.job.kind === "pr-action") {
      const pr = prsByProject[projectId]?.find((p) => p.id === entry.job.meta.prId);
      if (pr) selectPr(pr);
      // Both kinds are reviews of local changes and both render in the same tab; what tells them
      // apart is whether the answer carries a criteria table, which the row itself shows.
    } else if (entry.job.kind === "analyze-changes" || entry.job.kind === "ticket-review") {
      useAiPanelStore.getState().showAnalyzeJob(entry.job.id);
    }
  };

  return (
    <div className="shrink-0 border-b border-[var(--cf-border)]">
      <button
        onClick={() => setCollapsed((v) => !v)}
        aria-expanded={!collapsed}
        className="cf-focusable flex w-full items-center gap-1.5 px-3 py-2 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)] hover:bg-black/[0.02] dark:hover:bg-white/[0.03]"
      >
        <History size={11} />
        {t("ai.history")}
        {runningCount > 0 && (
          <span className="rounded-full bg-[var(--cf-accent-soft)] px-1.5 text-badge font-bold text-[var(--cf-accent)]">
            {runningCount}
          </span>
        )}
        <span className="ml-auto">
          {collapsed ? <ChevronRight size={12} /> : <ChevronDown size={12} />}
        </span>
      </button>
      {!collapsed && (
        <div className="space-y-0.5 px-1.5 pb-2">
          {topFive.map((entry) => {
            const { icon: Icon, color, spinning } = entryVisual(entry);
            const isActive = entryKey(entry) === activeEntryKey;
            const runCount = entryRunCount(entry);
            return (
              <Tooltip key={entryKey(entry)} label={entryTitle(entry)}>
              <button
                onClick={() => openEntry(entry)}
                className={`flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left ${
                  isActive ? "bg-[var(--cf-accent-soft)]" : "hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
                }`}
              >
                <Icon size={12} className={spinning ? "shrink-0 animate-spin" : "shrink-0"} style={{ color }} />
                <span className="min-w-0 flex-1 truncate text-body text-[var(--cf-text)]">{entryTitle(entry)}</span>
                {runCount > 1 && (
                  <Tooltip label={t("ai.runCount", { n: runCount })}>
                    <span className="shrink-0 rounded-full bg-black/[0.06] px-1.5 text-badge font-semibold text-[var(--cf-text-muted)] dark:bg-white/[0.1]">
                      ×{runCount}
                    </span>
                  </Tooltip>
                )}
                <span className="shrink-0 text-badge text-[var(--cf-text-muted)]">
                  {relativeTime(entryTimestamp(entry), t)}
                </span>
              </button>
              </Tooltip>
            );
          })}
          <Button variant="ghost" size="sm" className="w-full" onClick={() => setShowModal(true)}>
            {t("ai.viewAll")}
          </Button>
        </div>
      )}
      {showModal && <ActivityModal projectId={projectId} onClose={() => setShowModal(false)} />}
    </div>
  );
}
