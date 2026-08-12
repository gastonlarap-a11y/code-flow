import { useEffect, useMemo, useRef, useState } from "react";
import { RefreshCw, ShieldCheck, Ticket as TicketIcon } from "lucide-react";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import { Select } from "../common/Select";
import { StopSquare } from "../../lib/ui/icons";
import { listBranches, reviewChanges } from "../../lib/ipc/commands";
import { parseAnalysis } from "../../lib/parseAnalysis";
import { parseTicketVerdict, splitTicketReview, ticketVerdictFromStored } from "../../lib/parseTicketVerdict";
import { preferredBaseBranch } from "../../lib/branches";
import { isRefusal, isTicketRefusal } from "../../lib/analyzeRefusal";
import { useJobsStore, EMPTY_JOBS } from "../../state/jobsStore";
import { useAiPanelStore } from "../../state/aiPanelStore";
import { useChatStore } from "../../state/chatStore";
import { usePrStore } from "../../state/prStore";
import { useRepoStore } from "../../state/repoStore";
import { useTicketStore } from "../../state/ticketStore";
import { uncommittedCount } from "../../lib/fileStatus";
import { ChatAgentPicker } from "./ChatAgentPicker";
import { useT } from "../../state/languageStore";
import { ThinkingOrb } from "../common/ThinkingOrb";
import { ElapsedTime } from "../common/ElapsedTime";
import { RunStats } from "../common/RunStats";
import { CopyAnswer } from "../common/CopyAnswer";
import { renderMarkdown } from "../../lib/markdown";
import { FindingCard, QualityGateBadges, SHORT_SUMMARY_MAX } from "./FindingCard";
import { ReviewLevelSelector } from "./ReviewLevelSelector";
import { TicketVerdictPanel, VerdictSummary } from "./TicketVerdictPanel";
import { AiErrorBanner } from "./AiErrorBanner";
import { AiRunLog } from "./AiRunLog";
import type { Project } from "../../types/domain";

/**
 * The review of local changes, over the two axes it always had.
 *
 * <b>What is reviewed</b> — the uncommitted diff, or everything the branch contributes over a base —
 * and <b>what it is judged against</b> — code quality alone, or the branch's work item too. Those
 * were welded together into two tabs, so two of the four combinations did not exist; the one that
 * was wanted and missing is a whole-branch review with no ticket, before opening a pull request.
 *
 * <b>The state machine below is older than the controls and survives them.</b> Its rules came out of
 * real failures: a refusal is not an analysis and must not stand in for one, a clean tree shows an
 * empty state rather than a spinner that never ends, a stopped run offers to start again. The only
 * one that changed is `loading`, and it had to — see `autoStartEligible`.
 */
export function AnalyzeSection({ project }: { project: Project }) {
  const t = useT();
  const projectId = project.id;
  const selectedJobId = useAiPanelStore((s) => s.selectedJobId);
  const scope = useAiPanelStore((s) => s.scope);
  const withTicket = useAiPanelStore((s) => s.withTicket);
  const setScope = useAiPanelStore((s) => s.setScope);
  const setWithTicket = useAiPanelStore((s) => s.setWithTicket);
  const jobs = useJobsStore((s) => s.byProject[projectId] ?? EMPTY_JOBS);

  const branch = useRepoStore((s) => s.status?.current_branch ?? "");
  const linked = useTicketStore((s) => s.linked);
  const lastReview = useTicketStore((s) => s.lastReview);
  const level = usePrStore((s) => s.reviewLevel);
  const setLevel = usePrStore((s) => s.setReviewLevel);

  const [baseRef, setBaseRef] = useState("");
  const [branches, setBranches] = useState<string[]>([]);

  useEffect(() => {
    if (branch) void useTicketStore.getState().loadBranchReview(projectId, branch);
  }, [projectId, branch]);

  // The base is a guess the user can change, never a silent default: reviewing against the wrong
  // branch produces a diff that is not the branch's contribution, and nothing about the answer
  // would say so.
  useEffect(() => {
    let cancelled = false;
    void listBranches(project.local_path)
      .then((list) => {
        if (cancelled) return;
        setBranches(list.filter((b) => !b.is_remote).map((b) => b.name));
        setBaseRef(preferredBaseBranch(list, branch));
      })
      .catch(() => {
        if (!cancelled) setBranches([]);
      });
    return () => {
      cancelled = true;
    };
  }, [project.local_path, branch]);

  // A specific past run is pinned by id when opened from the Activity list; otherwise show the
  // project's most recent review of either kind. Selecting by id is what stops every entry from
  // aliasing onto the newest run.
  //
  // A refusal is not a review, so it does not count as "the most recent" one — otherwise it stands
  // in for a result forever: the tab shows yesterday's red row instead of running, and the
  // auto-start never fires because it can see a job.
  const job = useMemo(
    () =>
      (selectedJobId
        ? jobs.find((j) => j.id === selectedJobId)
        : jobs.find(
            (j) =>
              (j.kind === "analyze-changes" || j.kind === "ticket-review") &&
              !isRefusal(j) &&
              !isTicketRefusal(j),
          )) ?? null,
    [jobs, selectedJobId],
  );

  // Whether there is anything uncommitted at all. Only meaningful for the working-tree scope — a
  // branch contributes over its base whether or not anything is pending — so every use of it below
  // is guarded by the scope. Same source as the Changes tab's badge, so the two cannot disagree.
  const uncommitted = useRepoStore((s) => uncommittedCount(s.status));
  const nothingToAnalyze = scope === "working" && uncommitted === 0;

  const runReview = () => {
    // Guarded here and not only in the sidecar: the point is that no job is created, and the job is
    // created on this side. The sidecar refuses too, for the tree committed between the two.
    if (nothingToAnalyze) return;
    if (scope === "branch" && !baseRef) return;

    // The workspace's active SDD/Harness agent (if any) reviews as that role.
    const agent = useChatStore.getState().agentByProject[projectId] ?? null;
    const stamp = new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });

    const id = useJobsStore.getState().run({
      projectId,
      kind: withTicket ? "ticket-review" : "analyze-changes",
      // A per-run time stamp in the label so each review is identifiable in the Activity list
      // instead of every entry reading the same title.
      label: withTicket && linked ? `${linked.external_id} · ${stamp}` : `${t("analyze.title")} · ${stamp}`,
      task: (jobId) =>
        reviewChanges({ projectId, jobId, branch, scope, withTicket, baseRef, level, agent }),
    });

    // Pin this section to the run it just started, so its own result shows here and the Activity
    // list highlights the right row.
    useAiPanelStore.getState().showAnalyzeJob(id);
  };

  /**
   * Whether a run is about to start by itself.
   *
   * <b>This names a premise that used to be silent.</b> `loading` treated "no job" as "one is on
   * its way", which was true only because a run always was — the section auto-started on open. The
   * moment a combination exists that does not auto-start, that reading leaves the panel spinning
   * for ever. Only the cheapest combination starts by itself, and only on a fresh open.
   */
  const autoStartEligible = scope === "working" && !withTicket && !nothingToAnalyze && !selectedJobId;

  // Guarded with a ref rather than just checking `job`: React StrictMode double-invokes effects in
  // dev, and both invocations would otherwise see the same (still-null) `job` and each start their
  // own run — two job entries for one open.
  const startedRef = useRef(false);
  useEffect(() => {
    if (autoStartEligible && !job && !startedRef.current) {
      startedRef.current = true;
      runReview();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, autoStartEligible]);

  const [logExpanded, setLogExpanded] = useState(false);

  // A pinned refusal (opened from Activity) reads as the empty state — it is what it says. Unpinned
  // ones never reach here: they are filtered out of `job` above.
  const idle =
    (job !== null && (isRefusal(job) || isTicketRefusal(job))) || (nothingToAnalyze && !selectedJobId);
  const loading = !idle && (job?.status === "running" || (!job && autoStartEligible));
  // Everything below reads from the shown job, and when `idle` wins there is no job worth showing —
  // otherwise a stale run's banner or stop-notice renders underneath the empty state.
  const cancelled = !idle && job?.status === "cancelled";
  const error = !idle && job?.status === "error" ? job.error : null;

  // The live answer wins over the stored one: while a run is finishing, the last review is history.
  const answer = !idle && job?.status === "done" ? job.result : null;
  const parsed = useMemo(() => {
    if (answer) {
      const { findings } = splitTicketReview(answer);
      return { verdict: parseTicketVerdict(answer), analysis: parseAnalysis(findings), answer };
    }
    // Only when a ticket is in the question: the stored review is a ticket review, and showing it
    // under an analysis nobody asked for would misreport what is on screen.
    if (withTicket && lastReview && !job) {
      const { findings } = splitTicketReview(lastReview.review_md);
      return {
        verdict: ticketVerdictFromStored(lastReview),
        analysis: parseAnalysis(findings),
        answer: lastReview.review_md,
      };
    }
    return null;
  }, [answer, withTicket, lastReview, job]);

  const findings = parsed?.analysis.findings ?? [];
  const summary = parsed?.analysis.summary ?? "";
  const counts = {
    critical: findings.filter((f) => f.severity === "critical").length,
    warning: findings.filter((f) => f.severity === "warning").length,
    info: findings.filter((f) => f.severity === "info").length,
  };

  return (
    <div className="flex h-full flex-col">
      <div className="flex-1 overflow-auto p-4">
        <div className="mb-4 rounded-xl border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-3">
          <div className="flex items-start gap-3">
            <ShieldCheck size={16} className="mt-0.5 shrink-0 text-[var(--cf-accent)]" aria-hidden />
            <div className="min-w-0 flex-1">
              <p className="mb-0.5 text-body font-semibold">{t("analyze.title")}</p>
              {!loading && !error && parsed && (
                <QualityGateBadges grades={parsed.analysis.grades} findings={findings} />
              )}
              {!loading && !error && (findings.length > 0 || parsed?.verdict) && (
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
                  {parsed?.verdict && <VerdictSummary verdict={parsed.verdict} />}
                </div>
              )}
            </div>
            <ChatAgentPicker projectId={projectId} />
            <IconButton
              label="analyze.reanalyze"
              icon={RefreshCw}
              pending={loading}
              // Nothing to review is not something re-running fixes, so the button says so by being
              // unavailable rather than starting a run that fails.
              disabled={nothingToAnalyze || (scope === "branch" && !baseRef)}
              className="shrink-0"
              onClick={runReview}
            />
          </div>

          <div className="mt-3 flex flex-wrap items-center gap-2">
            <ScopeChoice value={scope} onChange={setScope} disabled={loading} />
            {scope === "branch" && (
              <Select
                value={baseRef}
                onChange={setBaseRef}
                disabled={loading || branches.length === 0}
                options={branches.map((name) => ({ value: name, label: name }))}
                ariaLabel={t("ticketReview.against")}
                size="sm"
              />
            )}
            {/* Only with a ticket: the depth directive is part of the ticket standard, and the
                analysis template never reads it — offering it there would promise a control that
                does nothing. */}
            {withTicket && <ReviewLevelSelector value={level} onChange={setLevel} disabled={loading} />}
          </div>

          <TicketChoice
            checked={withTicket}
            onChange={setWithTicket}
            disabled={loading}
            ticket={linked}
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
            <Button variant="ghost" size="sm" onClick={runReview}>
              {t("analyze.reanalyze")}
            </Button>
          </div>
        )}

        {!loading && error && <AiErrorBanner error={error} />}

        {/* Nothing has run and nothing is coming — the combinations that do not auto-start. */}
        {!idle && !loading && !cancelled && !error && !parsed && (
          <div className="flex flex-col items-center justify-center gap-2 py-10 text-center">
            <ShieldCheck size={28} className="text-[var(--cf-text-muted)]" aria-hidden />
            <p className="max-w-xs text-body text-[var(--cf-text-muted)]">{t("ticketReview.neverRun")}</p>
            <Button
              variant="primary"
              size="sm"
              disabled={scope === "branch" && !baseRef}
              onClick={runReview}
            >
              {t("ticketReview.run")}
            </Button>
          </div>
        )}

        {!loading && !cancelled && !error && parsed && (
          <div className="space-y-4">
            {parsed.verdict && <TicketVerdictPanel verdict={parsed.verdict} />}

            {findings.length === 0 ? (
              summary.length > 0 && summary.length > SHORT_SUMMARY_MAX ? (
                // Nothing matched the expected "### finding" format at all — rather than lose the
                // model's actual answer, render the raw response as markdown instead of a wall of
                // unstyled plain text.
                <div
                  className="cf-markdown-preview rounded-lg border border-[var(--cf-border)] bg-[var(--cf-surface)] p-4"
                  dangerouslySetInnerHTML={{ __html: renderMarkdown(summary) }}
                />
              ) : (
                <div className="flex flex-col items-center justify-center gap-2 py-6 text-center">
                  <ShieldCheck size={28} className="text-[var(--cf-success)]" />
                  <p className="max-w-xs text-body text-[var(--cf-text-muted)]">
                    {summary || t("analyze.noFindings")}
                  </p>
                </div>
              )
            ) : (
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
                      // A critical finding opens with its detail — and therefore with "Fix with
                      // AI", which lives inside the card. Collapsed by default, the one thing worth
                      // acting on immediately was the one thing you had to go looking for.
                      defaultOpen={finding.severity === "critical"}
                      projectId={projectId}
                      resolutionKey={job ? `job:${job.id}:${finding.id}` : undefined}
                    />
                  ))}
                </div>
              </div>
            )}

            {/* The end of the answer. The copy button is tied to there being an answer, not to
                there being a footer: an older run stored before the stamp existed has no footer and
                is still something a reader wants to lift out whole. */}
            <div className="flex flex-wrap items-center gap-2">
              <CopyAnswer text={parsed.answer} />
              <RunStats footer={parsed.analysis.footer} className="ml-auto" />
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

/** Which diff the review reads. A choice, not a tab strip — it governs no panel. */
function ScopeChoice({
  value,
  onChange,
  disabled,
}: {
  value: "working" | "branch";
  onChange: (scope: "working" | "branch") => void;
  disabled: boolean;
}) {
  const t = useT();
  const options = [
    { id: "working" as const, label: t("analyze.scopeWorking") },
    { id: "branch" as const, label: t("analyze.scopeBranch") },
  ];

  return (
    <div className="flex items-center rounded-md border border-[var(--cf-border)] p-0.5">
      {options.map((option) => (
        <button
          key={option.id}
          onClick={() => onChange(option.id)}
          disabled={disabled}
          aria-pressed={value === option.id}
          className={`cf-focusable rounded px-2 py-1 text-badge font-medium transition-colors disabled:opacity-50 ${
            value === option.id
              ? "bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]"
              : "text-[var(--cf-text-muted)] hover:text-[var(--cf-text)]"
          }`}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}

/**
 * Whether the branch's work item is judged too.
 *
 * Shown even with no ticket linked, saying where one is linked: a control that appears only once
 * you already know about the feature is a control nobody finds.
 */
function TicketChoice({
  checked,
  onChange,
  disabled,
  ticket,
}: {
  checked: boolean;
  onChange: (value: boolean) => void;
  disabled: boolean;
  ticket: { external_id: string; title: string } | null;
}) {
  const t = useT();

  return (
    <label className="mt-2 flex cursor-pointer items-start gap-2">
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled || !ticket}
        onChange={(e) => onChange(e.target.checked)}
        className="mt-0.5 size-3.5 shrink-0 accent-[var(--cf-accent-solid)]"
      />
      <span className="min-w-0 flex-1">
        <span className="flex items-center gap-1.5 text-ui">
          <TicketIcon size={13} className="shrink-0 text-[var(--cf-text-muted)]" aria-hidden />
          {t("analyze.withTicket")}
        </span>
        <span className="mt-0.5 block truncate text-badge text-[var(--cf-text-muted)]">
          {ticket ? `${ticket.external_id} · ${ticket.title}` : t("ticketReview.notLinkedHint")}
        </span>
      </span>
    </label>
  );
}
