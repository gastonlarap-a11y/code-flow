import { useEffect, useMemo, useRef, useState } from "react";
import { RefreshCw, ShieldCheck } from "lucide-react";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import { StopSquare } from "../../lib/ui/icons";
import { analyzeWorkingChanges } from "../../lib/ipc/commands";
import { parseAnalysis } from "../../lib/parseAnalysis";
import { isRefusal } from "../../lib/analyzeRefusal";
import { useJobsStore, EMPTY_JOBS } from "../../state/jobsStore";
import { useAiPanelStore } from "../../state/aiPanelStore";
import { useChatStore } from "../../state/chatStore";
import { useRepoStore } from "../../state/repoStore";
import { uncommittedCount } from "../../lib/fileStatus";
import { ChatAgentPicker } from "./ChatAgentPicker";
import { useT } from "../../state/languageStore";
import { ThinkingOrb } from "../common/ThinkingOrb";
import { ElapsedTime } from "../common/ElapsedTime";
import { RunStats } from "../common/RunStats";
import { CopyAnswer } from "../common/CopyAnswer";
import { renderMarkdown } from "../../lib/markdown";
import { FindingCard, QualityGateBadges, SHORT_SUMMARY_MAX } from "./FindingCard";
import { AiErrorBanner } from "./AiErrorBanner";
import { AiRunLog } from "./AiRunLog";

/** Pre-commit change analysis, shown inline in the AI panel (alongside chat and PR review)
 * instead of a separate modal — so it shares the same "Activity" job tracking and the same
 * always-available surface as everything else Claude does for this project. */
export function AnalyzeSection({ projectId }: { projectId: string }) {
  const t = useT();
  const selectedJobId = useAiPanelStore((s) => s.selectedJobId);
  const jobs = useJobsStore((s) => s.byProject[projectId] ?? EMPTY_JOBS);
  // A specific past run is pinned by id when opened from the Activity list; otherwise show the
  // project's most recent analysis. Selecting by id is what stops every analyze entry from
  // aliasing onto the newest run.
  const job = useMemo(
    () =>
      (selectedJobId
        ? jobs.find((j) => j.id === selectedJobId)
        : // A refusal is not an analysis, so it does not count as "the most recent" one — otherwise
          // it stands in for a result forever: the tab shows yesterday's red row instead of running,
          // and the auto-start never fires because it can see a job. Rows like this are no longer
          // written, but existing installs have them; opening one from Activity still shows it.
          jobs.find((j) => j.kind === "analyze-changes" && !isRefusal(j))) ?? null,
    [jobs, selectedJobId],
  );

  // Whether there is anything to analyse at all. The analysis reads the *uncommitted* diff, so on a
  // clean tree there is no work — and starting one anyway used to file a job, invoke the model with
  // a blank prompt, fail, and leave a permanent red row in Activity for a request nobody made.
  // Same source as the Changes tab's badge, so the two can never disagree.
  const uncommitted = useRepoStore((s) => uncommittedCount(s.status));
  const nothingToAnalyze = uncommitted === 0;

  const runAnalysis = () => {
    // Guarded here and not only in the sidecar: the point is that no job is created, and the job is
    // created on this side. The sidecar refuses too, for the tree committed between the two.
    if (nothingToAnalyze) return;
    // The workspace's active SDD/Harness agent (if any) analyzes as that role.
    const agent = useChatStore.getState().agentByProject[projectId] ?? null;
    const id = useJobsStore.getState().run({
      projectId,
      kind: "analyze-changes",
      // A per-run time stamp in the label so each analysis is identifiable in the Activity
      // list instead of every entry reading the same "Análisis de cambios".
      label: `${t("analyze.title")} · ${new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`,
      task: (jobId) => analyzeWorkingChanges(projectId, jobId, agent),
    });
    // Pin this section to the run it just started, so its own result shows here and the
    // Activity list highlights the right row.
    useAiPanelStore.getState().showAnalyzeJob(id);
  };

  // Auto-start only when landing on the section fresh (no pinned historical run) with nothing to
  // show yet — reopening, or selecting a past run, must never kick off a new Claude invocation.
  // Guarded with a ref rather than just checking `job`: React StrictMode double-invokes effects
  // in dev, and both invocations would otherwise see the same (still-null) `job` and each start
  // their own analysis — producing two job entries for one open.
  const startedRef = useRef(false);
  useEffect(() => {
    if (!selectedJobId && !job && !startedRef.current && !nothingToAnalyze) {
      startedRef.current = true;
      runAnalysis();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, selectedJobId, nothingToAnalyze]);

  const [logExpanded, setLogExpanded] = useState(false);
  // With no job *and* nothing to analyse there is nothing coming, so the spinner would never end —
  // `!job` alone used to mean "a run is on its way", which was true only because one always was.
  // A pinned refusal (opened from Activity) reads as the empty state too — it is what it says.
  // Unpinned ones never reach here: they are filtered out of `job` above.
  const idle = (job !== null && isRefusal(job)) || (nothingToAnalyze && !selectedJobId);
  const loading = !idle && (job?.status === "running" || !job);
  // Everything below reads from the shown job, and when `idle` wins there is no job worth showing —
  // otherwise a stale run's banner or stop-notice renders underneath the empty state.
  const cancelled = !idle && job?.status === "cancelled";
  const error = !idle && job?.status === "error" ? job.error : null;
  const parsed = useMemo(
    () => (!idle && job?.status === "done" && job.result ? parseAnalysis(job.result) : null),
    [idle, job],
  );
  const findings = parsed?.findings ?? [];
  const summary = parsed?.summary ?? "";
  const footer = parsed?.footer ?? null;
  // The model's answer as it was stored, which is what the copy button hands over: the parsed
  // pieces are for rendering, not for pasting into a ticket.
  const answer = parsed ? (job?.result ?? null) : null;

  const counts = {
    critical: findings.filter((f) => f.severity === "critical").length,
    warning: findings.filter((f) => f.severity === "warning").length,
    info: findings.filter((f) => f.severity === "info").length,
  };

  return (
    <div className="flex h-full flex-col">
      <div className="flex-1 overflow-auto p-4">
        <div className="mb-4 flex items-start gap-3 rounded-xl border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-3">
          <ShieldCheck size={16} className="mt-0.5 shrink-0 text-[var(--cf-accent)]" />
          <div className="min-w-0 flex-1">
            <p className="mb-0.5 text-body font-semibold">{t("analyze.title")}</p>
            {!loading && !error && parsed && <QualityGateBadges grades={parsed.grades} findings={findings} />}
            {!loading && !error && findings.length > 0 && (
              <div className="mt-1 flex flex-wrap items-center gap-1.5 text-badge">
                {counts.critical > 0 && (
                  <span className="rounded-full px-1.5 py-0.5 font-medium" style={{ background: "color-mix(in oklab, var(--cf-danger) 16%, transparent)", color: "var(--cf-danger)" }}>
                    {counts.critical} {t("analyze.critical")}
                  </span>
                )}
                {counts.warning > 0 && (
                  <span className="rounded-full px-1.5 py-0.5 font-medium" style={{ background: "color-mix(in oklab, var(--cf-warning) 16%, transparent)", color: "var(--cf-warning)" }}>
                    {counts.warning} {t("analyze.warning")}
                  </span>
                )}
                {counts.info > 0 && (
                  <span className="rounded-full px-1.5 py-0.5 font-medium" style={{ background: "color-mix(in oklab, var(--cf-accent) 16%, transparent)", color: "var(--cf-accent)" }}>
                    {counts.info} {t("analyze.info")}
                  </span>
                )}
              </div>
            )}
          </div>
          <ChatAgentPicker projectId={projectId} />
          <IconButton
            label="analyze.reanalyze"
            icon={RefreshCw}
            pending={loading}
            // Nothing to analyse is not something re-running fixes, so the button says so by being
            // unavailable rather than by starting a run that fails.
            disabled={nothingToAnalyze}
            className="shrink-0"
            onClick={runAnalysis}
          />
        </div>

        {idle && (
          <div className="flex flex-col items-center justify-center gap-2 py-10 text-center">
            <ShieldCheck size={28} className="text-[var(--cf-text-muted)]" aria-hidden />
            <p className="text-body font-medium text-[var(--cf-text)]">{t("analyze.nothingToAnalyze")}</p>
            <p className="max-w-xs text-ui text-[var(--cf-text-muted)]">{t("analyze.nothingToAnalyzeHint")}</p>
          </div>
        )}

        {loading && (
          <div className="space-y-3">
            <div className="flex flex-col items-center justify-center gap-3 py-6 text-center">
              <ThinkingOrb size="lg" />
              <p className="text-body text-[var(--cf-text-muted)]">{t("ai.working")}</p>
              {/* A run that is merely slow and one that is wedged looked identical; the clock is
                  what separates them, and it is why the stop button below is worth finding. */}
              {job && (
                <ElapsedTime since={job.createdAt} className="text-badge text-[var(--cf-text-muted)]" />
              )}
            </div>
            {job && (
              <AiRunLog
                runId={job.id}
                running
                expanded={logExpanded}
                onToggle={() => setLogExpanded((v) => !v)}
              />
            )}
          </div>
        )}

        {cancelled && (
          <div className="flex flex-col items-center justify-center gap-2 py-10 text-center">
            <StopSquare size={20} className="text-[var(--cf-text-muted)]" aria-hidden />
            <p className="text-body text-[var(--cf-text-muted)]">{t("ai.runStopped")}</p>
            {/* Unguarded on purpose: this state is only reached once the backend has answered, and
                it answers after the process tree is dead, so there is no run left to overlap. */}
            <Button variant="ghost" size="sm" onClick={runAnalysis}>
              {t("analyze.reanalyze")}
            </Button>
          </div>
        )}

        {!loading && error && <AiErrorBanner error={error} />}

        {!idle && !loading && !cancelled && !error && findings.length === 0 && (
          summary.length > 0 && summary.length > SHORT_SUMMARY_MAX ? (
            // Nothing matched the expected "### finding" format at all — rather than lose
            // the model's actual answer, render the raw response as markdown instead of a
            // wall of unstyled plain text.
            <div
              className="cf-markdown-preview rounded-lg border border-[var(--cf-border)] bg-[var(--cf-surface)] p-4"
              dangerouslySetInnerHTML={{ __html: renderMarkdown(summary) }}
            />
          ) : (
            <div className="flex flex-col items-center justify-center gap-2 py-10 text-center">
              <ShieldCheck size={28} className="text-[var(--cf-success)]" />
              <p className="max-w-xs text-body text-[var(--cf-text-muted)]">
                {summary || t("analyze.noFindings")}
              </p>
            </div>
          )
        )}

        {!loading && !cancelled && !error && findings.length > 0 && (
          <div className="space-y-3">
            {summary && (
              <div
                className="cf-markdown-preview rounded-lg border border-[var(--cf-border)] bg-[var(--cf-surface)] px-3.5 py-2.5"
                dangerouslySetInnerHTML={{ __html: renderMarkdown(summary) }}
              />
            )}
            <div className="space-y-2">
              {findings.map((finding) => (
                <FindingCard
                  key={finding.id}
                  finding={finding}
                  // A critical finding opens with its detail — and therefore with "Fix with AI",
                  // which lives inside the card. Collapsed by default, the one thing worth acting
                  // on immediately was the one thing you had to go looking for.
                  defaultOpen={finding.severity === "critical"}
                  projectId={projectId}
                  resolutionKey={job ? `job:${job.id}:${finding.id}` : undefined}
                />
              ))}
            </div>
          </div>
        )}

        {/* The end of the answer. The copy button is tied to there being an answer, not to there
            being a footer: an older run stored before the stamp existed has no footer and is still
            something a reader wants to lift out whole. */}
        {answer && !loading && !cancelled && !error && (
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <CopyAnswer text={answer} />
            <RunStats footer={footer} className="ml-auto" />
          </div>
        )}
      </div>
    </div>
  );
}
