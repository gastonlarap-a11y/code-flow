import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Ban,
  Check,
  Copy,
  ExternalLink,
  GitMerge,
  Link2,
  RefreshCw,
  Sparkles,
  ThumbsDown,
  ThumbsUp,
  X,
  type LucideIcon,
} from "lucide-react";
import { StopSquare } from "../../lib/ui/icons";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import { Tooltip } from "../common/Tooltip";
import { renderMarkdown } from "../../lib/markdown";
import { listCommentThreads, targetKey, targetProjectId, type PrTarget } from "../../lib/prTarget";
import { parseAnalysis, buildFixpack, formatFindingAsComment, formatSummaryComment } from "../../lib/parseAnalysis";
import { Checkbox } from "../common/Checkbox";
import { FindingCard, QualityGateBadges, SeverityCountBadges, SHORT_SUMMARY_MAX } from "./FindingCard";
import { PrCommentCard, PrCommentsSkeleton } from "./PrCommentCard";
import { useUiStore } from "../../state/uiStore";
import { usePrStore } from "../../state/prStore";
import { useJobsStore, EMPTY_JOBS } from "../../state/jobsStore";
import { confirmAction } from "../../state/confirmStore";
import { useT } from "../../state/languageStore";
import { isOwnGithubAuthor, loadGithubConnections } from "../../lib/githubConnections";
import type { GithubConnection } from "../../types/domain";
import { ThinkingOrb } from "../common/ThinkingOrb";
import { ElapsedTime } from "../common/ElapsedTime";
import { RunStats } from "../common/RunStats";
import { CopyAnswer } from "../common/CopyAnswer";
import { AiRunLog } from "./AiRunLog";
import { ChatAgentPicker } from "./ChatAgentPicker";
import { ReviewLevelSelector } from "./ReviewLevelSelector";
import { AiErrorBanner } from "./AiErrorBanner";
import { useCopy } from "../../lib/ui/useCopy";
import type { PrDecision, PullRequestSummary, PrCommentThread } from "../../types/domain";

/**
 * One PR review, whatever it's backed by.
 *
 * `target` is the whole difference between a PR from a project's own list and one opened from a
 * pasted link with nothing cloned: the operations that act on the *host* — comment threads,
 * decisions, publishing — are identical, and the two that need a working copy (a diff built from
 * local git, applying a fix to a file) simply aren't offered when there isn't one.
 */
export function PrReviewSection({ target, pr }: { target: PrTarget; pr: PullRequestSummary }) {
  const t = useT();
  const projectId = targetProjectId(target);
  const bucket = targetKey(target);
  const linkOnly = target.kind === "link";
  const reviewPr = usePrStore((s) => s.reviewPr);
  const reviewLevel = usePrStore((s) => s.reviewLevel);
  const setReviewLevel = usePrStore((s) => s.setReviewLevel);
  const postReview = usePrStore((s) => s.postReview);
  const selectPr = usePrStore((s) => s.selectPr);
  const closeLinkPr = usePrStore((s) => s.closeLinkPr);
  const posting = usePrStore((s) => s.posting);
  const posted = usePrStore((s) => s.posted);
  const actOnPr = usePrStore((s) => s.actOnPr);
  const prActionBusy = usePrStore((s) => s.prActionBusy);
  const jobs = useJobsStore((s) => s.byProject[bucket] ?? EMPTY_JOBS);
  const job = useMemo(
    () => jobs.find((j) => j.kind === "pr-review" && j.meta.prId === pr.id) ?? null,
    [jobs, pr.id],
  );

  // Whether this pull request is the signed-in user's own. GitHub refuses to record an approval on
  // it (`XLANG-013`), and the failure is a rule rather than a condition — no retry and no credential
  // changes it — so the button is disabled rather than left to produce an error. Read from the saved
  // connections, which already carry the login: no API call, and nothing to wait for on open.
  const [githubConnections, setGithubConnections] = useState<GithubConnection[]>([]);
  useEffect(() => {
    let cancelled = false;
    void loadGithubConnections()
      .catch(() => [])
      .then((connections) => {
        if (!cancelled) setGithubConnections(connections);
      });
    return () => {
      cancelled = true;
    };
  }, []);
  const ownPullRequest = pr.provider === "github" && isOwnGithubAuthor(pr.author, githubConnections);

  const [logExpanded, setLogExpanded] = useState(false);
  const loading = job?.status === "running";
  const error = job?.status === "error" ? job.error : null;
  const reviewText = job?.status === "done" ? job.result : null;
  const parsed = useMemo(() => (reviewText ? parseAnalysis(reviewText) : null), [reviewText]);
  const findings = parsed?.findings ?? [];
  const summary = parsed?.summary ?? "";

  // Human selection of which findings to post (default: all), plus whether to post the summary
  // thread. Reset whenever a new review result arrives.
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [postSummary, setPostSummary] = useState(true);
  useEffect(() => {
    setSelectedIds(new Set(findings.map((f) => f.id)));
  }, [reviewText]); // eslint-disable-line react-hooks/exhaustive-deps
  const toggleSelected = (id: string) =>
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const [fixpackCopied, copyFixpack] = useCopy();
  const runReview = () => reviewPr(target, pr.id);
  const publish = async () => {
    if (!parsed || !job) return;
    const chosen = findings.filter((f) => selectedIds.has(f.id));
    if (chosen.length === 0 && !postSummary) return;
    const confirmKey = pr.provider === "github" ? "chat.confirmPostGithub" : "chat.confirmPost";
    if (!(await confirmAction(t(confirmKey, { id: pr.id, n: chosen.length }), false))) return;
    const items = chosen.map((f) => ({
      file: f.location?.file ?? null,
      category: f.category,
      content: formatFindingAsComment(f),
      location: f.location,
    }));
    // `chosen`, not every finding: the summary describes what actually gets posted.
    const summary = postSummary ? formatSummaryComment(parsed, new Date().toISOString().slice(0, 10), chosen) : null;
    void postReview(target, pr.id, job.id, items, postSummary, summary);
  };

  // A decision already on the record (here or on the website) retires the button that would take
  // it again — and a merged/closed PR retires all three, since there's nothing left to decide.
  const decision = usePrStore((s) => s.decisionByPr[`${bucket}:${pr.id}`] ?? "none");
  const loadPrDecision = usePrStore((s) => s.loadPrDecision);
  useEffect(() => {
    void loadPrDecision(target, pr.id);
    // `target` is rebuilt on every render by the caller, so the identity that matters is what it
    // addresses — the bucket key and the PR.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loadPrDecision, bucket, pr.id]);

  const prClosed = pr.status === "merged" || pr.status === "closed";
  const doPrAction = async (action: "approve" | "request_changes" | "close") => {
    const confirmKey =
      action === "approve"
        ? "pr.confirmApprove"
        : action === "request_changes"
          ? "pr.confirmRequestChanges"
          : "pr.confirmClose";
    // Request-changes and close are destructive-ish (they push a state the author sees), so they
    // get the emphasized confirm; approve gets the plain one.
    if (!(await confirmAction(t(confirmKey, { id: pr.id }), action !== "approve"))) return;
    void actOnPr(target, pr.id, action);
  };

  // Existing comment threads on the PR — e.g. from a human reviewer — refetched fresh every
  // time this PR is opened rather than cached, since they can change outside of CodeFlow at
  // any time (someone replies, resolves a thread, etc.).
  const [openThreads, setOpenThreads] = useState<PrCommentThread[]>([]);
  const [threadsLoading, setThreadsLoading] = useState(true);
  // A monotonic token so only the newest fetch writes state: switching PRs or hitting the manual
  // reload while a request is still in flight bumps the token, and the stale response is ignored.
  const threadsReqRef = useRef(0);
  const loadThreads = useCallback(() => {
    const token = ++threadsReqRef.current;
    setThreadsLoading(true);
    return listCommentThreads(target, pr.id)
      .then((threads) => {
        if (threadsReqRef.current === token) setOpenThreads(threads);
      })
      .catch(() => {
        if (threadsReqRef.current === token) setOpenThreads([]);
      })
      .finally(() => {
        if (threadsReqRef.current === token) setThreadsLoading(false);
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [bucket, pr.id]);
  useEffect(() => {
    void loadThreads();
  }, [loadThreads]);

  // Footer buttons truncate when the panel is narrow, so their label doubles as the tooltip.
  const publishLabel = posted
    ? pr.provider === "github"
      ? t("chat.postedGithub")
      : t("chat.posted")
    : posting
      ? t("chat.posting")
      : t("chat.postToPr");
  const reviewLabel = loading ? t("chat.reviewing") : reviewText ? t("chat.reviewAgain") : t("chat.reviewWithClaude");

  return (
    <div className="flex h-full flex-col">
      <div className="flex-1 overflow-auto p-4">
        <div className="mb-4 flex items-start gap-3 rounded-xl border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-3">
          <div className="min-w-0 flex-1">
            <p className="mb-0.5 truncate text-body font-semibold">
              #{pr.id} {pr.title}
            </p>
            <p className="text-badge text-[var(--cf-text-muted)]">
              {t("chat.prBy", { author: pr.author })} · {t("chat.prBranches", { source: pr.source_branch, target: pr.target_branch })}
            </p>
            <a
              href={pr.url}
              target="_blank"
              rel="noreferrer"
              className="mt-1 inline-flex items-center gap-1 text-badge text-[var(--cf-accent)] hover:underline"
            >
              <ExternalLink size={10} />
              {pr.provider === "github" ? t("chat.viewOnGithub") : t("chat.viewOnAdo")}
            </a>
            {!loading && !error && parsed && (
              <div className="mt-1.5">
                <QualityGateBadges grades={parsed.grades} findings={findings} />
              </div>
            )}
          </div>
          <IconButton
            label="chat.backToChat"
            icon={X}
            className="shrink-0"
            onClick={() => (linkOnly ? closeLinkPr() : selectPr(null))}
          />
        </div>

        {/* Says which repository this PR belongs to — no project in the sidebar is naming it —
            and what the missing clone costs, since the difference shows up in the findings. */}
        {linkOnly && <LinkReviewNotice />}

        {threadsLoading ? (
          <PrCommentsSkeleton label={t("pr.loadingComments")} />
        ) : (
          <div className="mb-4 space-y-2">
            <div className="flex items-center gap-1.5">
              <p className="text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
                {openThreads.length > 0 ? t("pr.openComments", { n: openThreads.length }) : t("pr.noComments")}
              </p>
              {/* The git host is the source of truth for comments and it changes outside CodeFlow
                  (someone replies or resolves a thread), so this lets the user pull the latest
                  without reopening the PR. */}
              <IconButton label="pr.refreshComments" icon={RefreshCw} onClick={() => void loadThreads()} />
            </div>
            {openThreads.map((thread) => (
              <PrCommentCard
                key={thread.id}
                thread={thread}
                projectId={projectId}
                prSourceBranch={pr.source_branch}
                resolutionKey={`pr:${pr.id}:thread:${thread.id}`}
              />
            ))}
          </div>
        )}

        {loading && job && (
          <div className="space-y-2">
            <div className="flex items-center gap-3 rounded-lg border border-[var(--cf-border)] p-4 text-ui text-[var(--cf-text-muted)]">
              <ThinkingOrb size="sm" />
              {t("ai.working")}
              {/* A review is the longer of the two runs and the one worth timing: without a clock,
                  slow and wedged look the same, and the stop button is a scroll away in the log. */}
              <ElapsedTime since={job.createdAt} className="ml-auto text-badge" />
            </div>
            <AiRunLog
              runId={job.id}
              running
              expanded={logExpanded}
              onToggle={() => setLogExpanded((v) => !v)}
            />
          </div>
        )}

        {job?.status === "cancelled" && (
          <div className="flex items-center gap-2 rounded-lg border border-dashed border-[var(--cf-border)] p-3 text-ui text-[var(--cf-text-muted)]">
            <StopSquare size={14} aria-hidden />
            {t("ai.runStopped")}
            {/* No re-run offered on a settled PR — same rule as the footer. */}
            {!prClosed && (
              <Button variant="ghost" size="sm" className="ml-auto" onClick={runReview}>
                {t("pr.reviewAgain")}
              </Button>
            )}
          </div>
        )}

        {!loading && error && <AiErrorBanner error={error} compact />}

        {!loading && !error && reviewText && findings.length === 0 && (
          summary.length > SHORT_SUMMARY_MAX ? (
            <div
              className="cf-markdown-preview rounded-lg border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-4"
              dangerouslySetInnerHTML={{ __html: renderMarkdown(summary) }}
            />
          ) : (
            <p className="select-text rounded-lg border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-3 text-ui leading-relaxed text-[var(--cf-text)]">
              {summary}
            </p>
          )
        )}

        {!loading && !error && reviewText && findings.length > 0 && (
          <div className="space-y-3">
            {summary && (
              <div
                className="cf-markdown-preview rounded-lg border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] px-3.5 py-2.5"
                dangerouslySetInnerHTML={{ __html: renderMarkdown(summary) }}
              />
            )}
            {/* Explicit "Claude's findings" header (with a severity tally) so the AI-generated
                findings read as a distinct section from the human "Open comments" above them —
                previously they ran together with no divider and looked like one blurry list. */}
            <div>
              <div className="mb-2 flex items-center justify-between gap-2 border-t border-[var(--cf-border)] pt-3">
                <p className="flex items-center gap-1.5 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
                  <Sparkles size={11} className="text-[var(--cf-accent)]" />
                  {t("pr.findingsHeader", { n: findings.length })}
                </p>
                <SeverityCountBadges findings={findings} />
              </div>
              <div className="space-y-2">
                {findings.map((finding) => (
                  <div key={finding.id} className="flex items-start gap-2">
                    <Tooltip label={t("pr.selectToPost")}>
                      <span className="mt-2 shrink-0">
                        <Checkbox checked={selectedIds.has(finding.id)} onChange={() => toggleSelected(finding.id)} />
                      </span>
                    </Tooltip>
                    <div className="min-w-0 flex-1">
                      <FindingCard
                        finding={finding}
                        // As in the pre-commit analysis: a critical finding opens with its detail,
                        // so "Fix with AI" is visible instead of one expand away.
                        defaultOpen={finding.severity === "critical"}
                        projectId={projectId}
                        prSourceBranch={pr.source_branch}
                        resolutionKey={job ? `job:${job.id}:${finding.id}` : undefined}
                      />
                    </div>
                  </div>
                ))}
              </div>
            </div>
          </div>
        )}

        {!loading && !error && !reviewText && (
          <p className="text-ui text-[var(--cf-text-muted)]">{t("chat.awaitingReview")}</p>
        )}

        {/* The end of the answer: what it cost and how much of the change it saw, and the one
            control that copies the whole thing. Both belong here rather than up in the summary —
            this is where a reader stops reading, and a copy button placed anywhere else was a
            button nobody found. The row renders for a review with no findings too, which is the
            case that used to offer no way to copy it at all. */}
        {!loading && !error && reviewText && (
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <CopyAnswer text={reviewText} />
            <RunStats footer={parsed?.footer ?? null} className="ml-auto" />
          </div>
        )}
      </div>

      {/* Footer laid out as stacked rows (PR decision → review options → primary actions) instead of
          one packed strip: the panel can be as narrow as PANEL_MIN, and cramming the level selector,
          the toggles and both call-to-actions into a single line wrapped their labels onto two lines,
          which rendered as oversized buttons. Every label here stays one line and truncates. */}
      <div className="@container shrink-0 space-y-2 border-t border-[var(--cf-border)] p-2.5">
        {(prClosed || decision !== "none") && <PrDecisionState status={pr.status} decision={decision} />}
        {!prClosed && decision !== "approved" && (
          <div className="flex items-center gap-1.5">
            <PrActionButton
              variant="success"
              icon={ThumbsUp}
              label={t("pr.approve")}
              busy={prActionBusy === "approve"}
              disabled={prActionBusy !== null || ownPullRequest}
              // A disabled button with no explanation reads as a bug, and the footer has no room
              // for the reason anywhere else.
              tooltip={ownPullRequest ? t("pr.cannotApproveOwnHint") : undefined}
              onClick={() => doPrAction("approve")}
            />
            <PrActionButton
              variant="warning"
              icon={ThumbsDown}
              // Already asked for changes: asking again says nothing new. Approving stays open,
              // because approving once the author has pushed the fixes is the point of the flow.
              label={t("pr.requestChanges")}
              busy={prActionBusy === "request_changes"}
              disabled={prActionBusy !== null || decision === "changes_requested"}
              onClick={() => doPrAction("request_changes")}
            />
            <PrActionButton
              variant="danger"
              icon={Ban}
              label={t("pr.close")}
              busy={prActionBusy === "close"}
              disabled={prActionBusy !== null}
              onClick={() => doPrAction("close")}
            />
          </div>
        )}
        {/* Everything below acts *on* the pull request — running a review of it, publishing to it.
            A merged or closed PR is settled, so none of it is offered: the state chip above is the
            whole footer. Its findings stay readable above, they just have nowhere left to go. */}
        {!prClosed && (
          <>
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5">
              <ReviewLevelSelector value={reviewLevel} onChange={setReviewLevel} disabled={loading} />
              {/* Agents are picked per project; a link session has none to pick for. */}
              {projectId && <ChatAgentPicker projectId={projectId} />}
              {reviewText && !loading && findings.length > 0 && (
                <>
                  <Button
                    variant="ghost"
                    size="sm"
                    icon={fixpackCopied ? Check : Copy}
                    tooltip={t("pr.fixpackHint")}
                    className="shrink-0"
                    onClick={() => parsed && copyFixpack(buildFixpack(parsed, pr.id))}
                  >
                    {t("pr.fixpack")}
                  </Button>
                  <Tooltip label={t("pr.postSummaryHint")}>
                  <label className="flex shrink-0 items-center gap-1.5 py-1 text-badge text-[var(--cf-text-muted)]">
                    <Checkbox checked={postSummary} onChange={setPostSummary} />
                    {t("pr.postSummary")}
                  </label>
                  </Tooltip>
                </>
              )}
            </div>
            <div className="flex items-center gap-1.5">
              {reviewText && !loading && (
                <Button
                  variant="secondary"
                  size="sm"
                  {...(posted ? { icon: Check } : {})}
                  pending={posting}
                  disabled={posted}
                  className="min-w-0 flex-1"
                  onClick={publish}
                >
                  <span className="truncate">{publishLabel}</span>
                </Button>
              )}
              <Button
                variant="primary"
                size="sm"
                icon={Sparkles}
                pending={loading}
                className="min-w-0 flex-1"
                onClick={runReview}
              >
                <span className="truncate">{reviewLabel}</span>
              </Button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

/**
 * The banner a link-only review wears: which repository the PR is in (nothing else on screen says
 * so), what this review can't see, and the way out of that — cloning it, which turns the same PR
 * into a project-backed review with no loss of place.
 */
function LinkReviewNotice() {
  const t = useT();
  const linkPr = usePrStore((s) => s.linkPr);
  const openCloneOffer = useUiStore((s) => s.openPrLinkModal);
  if (!linkPr) return null;
  return (
    <div className="mb-4 rounded-lg border border-dashed border-[var(--cf-border)] px-3 py-2">
      <p className="flex items-center gap-1.5 text-badge font-medium text-[var(--cf-text)]">
        <Link2 size={11} className="shrink-0 text-[var(--cf-text-muted)]" />
        <span className="min-w-0 truncate">{linkPr.repoLabel}</span>
      </p>
      <p className="mt-1 text-badge leading-relaxed text-[var(--cf-text-muted)]">{t("prLink.quickNote")}</p>
      <Button variant="ghost" size="sm" className="mt-1" onClick={openCloneOffer}>
        {t("prLink.cloneInstead")}
      </Button>
    </div>
  );
}

/**
 * What this pull request has settled into, shown in place of the decision it no longer takes:
 * merged or closed (nothing left to decide, for anyone), or approved by this user (they already
 * decided). Rendered as a statement rather than a disabled button row, because a row of greyed-out
 * buttons says "this is broken" where a state chip says "this is done".
 *
 * The PR's own end state outranks the personal vote — once it's merged, "you approved it" stopped
 * being the useful thing to say.
 */
function PrDecisionState({ status, decision }: { status: PullRequestSummary["status"]; decision: PrDecision }) {
  const t = useT();
  const state =
    status === "merged"
      ? { icon: GitMerge, tone: PR_STATE_TONES.accent, label: t("pr.stateMerged"), hint: t("pr.stateLockedHint") }
      : status === "closed"
        ? { icon: Ban, tone: PR_STATE_TONES.danger, label: t("pr.stateClosed"), hint: t("pr.stateLockedHint") }
        : decision === "approved"
          ? { icon: ThumbsUp, tone: PR_STATE_TONES.success, label: t("pr.stateApproved"), hint: t("pr.stateApprovedHint") }
          : {
              icon: ThumbsDown,
              tone: PR_STATE_TONES.warning,
              label: t("pr.stateChangesRequested"),
              hint: t("pr.stateChangesRequestedHint"),
            };
  const Icon = state.icon;
  return (
    <Tooltip label={state.hint}>
      <div
        className={`flex items-center justify-center gap-1.5 rounded-md border border-dashed border-[var(--cf-border)] px-2 py-1.5 text-badge font-medium ${state.tone}`}
      >
        <Icon size={12} className="shrink-0" />
        <span className="truncate">{state.label}</span>
      </div>
    </Tooltip>
  );
}

/** Tone classes spelled out statically: an interpolated `--cf-${tone}` arbitrary value would
 * never be generated by Tailwind. */
const PR_STATE_TONES = {
  accent: "text-[var(--cf-accent)]",
  success: "text-[var(--cf-success)]",
  warning: "text-[var(--cf-warning)]",
  danger: "text-[var(--cf-danger)]",
} as const;

/**
 * One of the three PR decision buttons (approve / request changes / close).
 *
 * A thin wrapper over `Button` rather than its own control: what it adds is the layout — the three
 * share the footer row evenly and, below ~300px, drop their labels rather than rendering stubs
 * ("Solicitar cam…"). The tones moved into `controlStyles`, where the static tone map it used to
 * carry belongs.
 */
function PrActionButton({
  variant,
  icon,
  label,
  busy,
  disabled,
  tooltip,
  onClick,
}: {
  variant: "success" | "warning" | "danger";
  icon: LucideIcon;
  label: string;
  busy: boolean;
  disabled: boolean;
  /** Why the button is unavailable — a disabled Approve on your own PR. `Button` anchors it on a
   * wrapping span, because a disabled button fires no pointer events of its own. */
  tooltip?: string | undefined;
  onClick: () => void;
}) {
  return (
    <Button
      variant={variant}
      size="sm"
      icon={icon}
      pending={busy}
      disabled={disabled}
      className="min-w-0 flex-1"
      {...(tooltip ? { tooltip } : {})}
      onClick={onClick}
    >
      <span className="truncate @max-[300px]:hidden">{label}</span>
    </Button>
  );
}
