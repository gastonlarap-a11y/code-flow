import { useCallback, useEffect, useRef, useState } from "react";
import {
  AlertTriangle,
  ExternalLink,
  FolderGit2,
  GitBranchPlus,
  GitPullRequest,
  Link2,
  Search,
  Sparkles,
} from "lucide-react";
import { Button } from "../common/Button";
import { Modal } from "../common/Modal";
import { resolvePrLink } from "../../lib/ipc/commands";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { usePrStore } from "../../state/prStore";
import { useUiStore } from "../../state/uiStore";
import { pushErrorToast } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import { ReviewLevelSelector } from "../ai/ReviewLevelSelector";
import { CloneRepoModal } from "./CloneRepoModal";
import type { PrLinkResolution, PullRequestSummary } from "../../types/domain";

/** Cheap "is this worth auto-resolving?" test for whatever happens to be on the clipboard — the
 * backend is the real parser, this only decides whether to spend a round-trip on open. */
function looksLikePrLink(text: string): boolean {
  const value = text.trim().toLowerCase();
  if (value.length > 500 || !/^https?:\/\//.test(value)) return false;
  return value.includes("/pull/") || value.includes("/pullrequest/") || value.includes("/pullrequests/");
}

function PrPreview({ pr }: { pr: PullRequestSummary }) {
  const t = useT();
  return (
    <div className="rounded-lg border border-[var(--cf-border)] bg-black/[0.02] p-2.5 dark:bg-white/[0.03]">
      <p className="flex items-start gap-1.5 text-body font-semibold">
        <GitPullRequest size={14} className="mt-0.5 shrink-0 text-[var(--cf-accent)]" aria-hidden />
        <span className="min-w-0 flex-1">
          #{pr.id} {pr.title}
        </span>
      </p>
      <p className="mt-1 pl-[20px] text-badge text-[var(--cf-text-muted)]">
        {t("chat.prBy", { author: pr.author })} ·{" "}
        {t("chat.prBranches", { source: pr.source_branch, target: pr.target_branch })}
      </p>
      <a
        href={pr.url}
        target="_blank"
        rel="noreferrer"
        className="cf-focusable mt-1 inline-flex items-center gap-1 rounded pl-[20px] text-badge text-[var(--cf-accent)] hover:underline"
      >
        <ExternalLink size={12} aria-hidden />
        {pr.provider === "github" ? t("chat.viewOnGithub") : t("chat.viewOnAdo")}
      </a>
    </div>
  );
}

/**
 * "Review a PR from its link" — paste the URL a teammate sent you and CodeFlow works out which
 * of your repositories it belongs to, links that repository to its host if it wasn't already,
 * and hands the pull request to the normal review pipeline. No hunting through the sidebar, and
 * no need to know which project (or even which workspace) the repo lives in.
 *
 * When the repository isn't on this machine at all there are two honest answers, and both are
 * offered rather than picked for the user: review straight from the API diff (instant, but the
 * model never sees the surrounding codebase), or clone it once and get the full review.
 */
export function OpenPrLinkModal({ onClose }: { onClose: () => void }) {
  const t = useT();
  const activeWorkspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);
  const reviewLevel = usePrStore((s) => s.reviewLevel);
  const setReviewLevel = usePrStore((s) => s.setReviewLevel);
  const openSettings = useUiStore((s) => s.openSettings);

  const [url, setUrl] = useState("");
  const [resolving, setResolving] = useState(false);
  const [resolution, setResolution] = useState<PrLinkResolution | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [showClone, setShowClone] = useState(false);
  // Only the newest lookup may write state — the field stays editable while one is in flight.
  const requestRef = useRef(0);

  const resolve = useCallback(async (value: string): Promise<PrLinkResolution | null> => {
    const link = value.trim();
    if (!link) return null;
    const token = ++requestRef.current;
    setResolving(true);
    setError(null);
    setResolution(null);
    try {
      const result = await resolvePrLink(link);
      if (requestRef.current !== token) return null;
      setResolution(result);
      return result;
    } catch (e) {
      if (requestRef.current === token) setError(String(e));
      return null;
    } finally {
      if (requestRef.current === token) setResolving(false);
    }
  }, []);

  // Opening this modal is already the statement of intent, so a pull-request link sitting on the
  // clipboard is filled in and looked up straight away — the common case ("someone just sent me
  // this PR") becomes a single click. Clipboard reads can be denied; that's simply a no-op.
  useEffect(() => {
    let cancelled = false;
    void navigator.clipboard
      ?.readText()
      .then((text) => {
        if (cancelled || !looksLikePrLink(text)) return;
        setUrl(text.trim());
        void resolve(text);
      })
      .catch(() => {});
    return () => {
      cancelled = true;
    };
  }, [resolve]);

  /** Puts the PR on screen exactly as selecting it in the sidebar would, crossing into its
   * workspace/project first, and optionally launches the review straight away. */
  const openPr = async (ready: Extract<PrLinkResolution, { status: "Ready" }>, review: boolean) => {
    try {
      await useWorkspaceStore.getState().focusProject(ready.workspace_id, ready.project_id);
    } catch (e) {
      pushErrorToast(String(e));
      return;
    }
    usePrStore.getState().selectPr(ready.pr);
    useUiStore.getState().openAiPanel();
    // The sidebar's own list is refreshed in the background so this PR shows as selected there
    // too — the review itself doesn't wait on it.
    void usePrStore.getState().loadPullRequests(ready.project_id);
    if (review) usePrStore.getState().reviewPr({ kind: "project", projectId: ready.project_id }, ready.pr.id);
    onClose();
  };

  // After cloning, the new repository's remote is what makes the link resolvable — so the same
  // URL is looked up again and, this time, goes straight into the review the user asked for.
  const onCloned = async () => {
    const result = await resolve(url);
    if (result?.status === "Ready") void openPr(result, true);
  };

  /** Hands the PR to the panel with no clone behind it. From here on it is an ordinary review —
   * same findings, same comment threads, same approve / request changes / close, same Activity —
   * just reading its diff from the host instead of from a working copy. */
  const openWithoutCloning = (found: Extract<PrLinkResolution, { status: "NoLocalRepo" }>) => {
    if (!activeWorkspaceId) return;
    usePrStore.getState().openLinkPr({
      url: url.trim(),
      pr: found.pr,
      repoLabel: found.repo_label,
      cloneUrl: found.clone_url,
      workspaceId: activeWorkspaceId,
    });
    useUiStore.getState().openAiPanel();
    usePrStore.getState().reviewPr({ kind: "link", url: url.trim(), workspaceId: activeWorkspaceId }, found.pr.id);
    onClose();
  };

  const lookupBody = (
    <>
      <p className="mb-2 text-body text-[var(--cf-text-muted)]">{t("prLink.subtitle")}</p>

      <div className="mb-3 flex items-center gap-2">
        <input
          autoFocus
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") void resolve(url);
          }}
          placeholder={t("prLink.placeholder")}
          aria-label={t("prLink.placeholder")}
          className="min-w-0 flex-1 rounded-md border border-[var(--cf-border)] bg-transparent px-2 py-1.5 font-mono text-ui outline-none focus:border-[var(--cf-accent)]"
        />
        <Button
          icon={Search}
          pending={resolving}
          disabled={!url.trim()}
          className="shrink-0"
          onClick={() => void resolve(url)}
        >
          {resolving ? t("prLink.searching") : t("prLink.find")}
        </Button>
      </div>

      {error && (
        <p className="mb-3 flex items-start gap-1.5 rounded-lg border border-[var(--cf-danger)]/40 p-2.5 text-body text-[var(--cf-danger)]">
          <AlertTriangle size={14} className="mt-0.5 shrink-0" aria-hidden />
          <span className="min-w-0 flex-1 break-words">{error}</span>
        </p>
      )}

      {resolution?.status === "Unrecognized" && (
        <p className="mb-3 rounded-lg border border-[var(--cf-border)] p-2.5 text-body text-[var(--cf-text-muted)]">
          {t("prLink.unrecognized")}
        </p>
      )}

      {/* Two states, one shape. "No token saved" and "the saved token was refused" lead to the same
          place — Settings, for this provider — but telling someone whose PAT expired to connect the
          organisation would be telling them to redo something they already did. */}
      {(resolution?.status === "NeedsToken" || resolution?.status === "Expired") && (
        <p className="mb-3 rounded-lg border border-[var(--cf-border)] p-2.5 text-body text-[var(--cf-text-muted)]">
          {resolution.status === "Expired"
            ? t("prLink.expired", { identifier: resolution.identifier })
            : t("prLink.needsToken", { identifier: resolution.identifier })}{" "}
          {/* An inline link inside a sentence, not a control on its own row — `Button` would break
              the paragraph it belongs to. */}
          <button
            onClick={() => {
              openSettings("integrations", resolution.provider);
              onClose();
            }}
            className="cf-focusable rounded text-[var(--cf-accent)] hover:underline"
          >
            {t("statusbar.settings")}
          </button>
        </p>
      )}

      {resolution?.status === "NoLocalRepo" && (
        <div className="mb-3 space-y-2">
          <PrPreview pr={resolution.pr} />
          <p className="text-body text-[var(--cf-text-muted)]">
            {t("prLink.noLocalRepo", { repo: resolution.repo_label })}
          </p>
          {!activeWorkspaceId && <p className="text-body text-[var(--cf-danger)]">{t("prLink.noWorkspace")}</p>}
        </div>
      )}

      {resolution?.status === "Ready" && (
        <div className="mb-3 space-y-2">
          <PrPreview pr={resolution.pr} />
          <p className="flex items-center gap-1.5 text-body text-[var(--cf-text-muted)]">
            <FolderGit2 size={14} className="shrink-0" aria-hidden />
            {t("prLink.foundIn", { project: resolution.project_name })}
          </p>
        </div>
      )}

      <div className="flex items-center justify-end gap-2">
        {resolution?.status === "Ready" && (
          <>
            <ReviewLevelSelector value={reviewLevel} onChange={setReviewLevel} disabled={false} />
            <div className="flex-1" />
            <Button onClick={() => void openPr(resolution, false)}>{t("prLink.open")}</Button>
            <Button variant="primary" icon={Sparkles} onClick={() => void openPr(resolution, true)}>
              {t("prLink.review")}
            </Button>
          </>
        )}

        {resolution?.status === "NoLocalRepo" && (
          <>
            {/* The deeper option, kept secondary: it downloads the repository, and the point of
                this screen is that a link alone is enough. */}
            <Button icon={GitBranchPlus} disabled={!activeWorkspaceId} onClick={() => setShowClone(true)}>
              {t("prLink.cloneAndReview")}
            </Button>
            <Button
              variant="primary"
              icon={Sparkles}
              disabled={!activeWorkspaceId}
              onClick={() => openWithoutCloning(resolution)}
            >
              {t("prLink.quickReview")}
            </Button>
          </>
        )}

        {resolution?.status !== "Ready" && resolution?.status !== "NoLocalRepo" && (
          <Button variant="ghost" onClick={onClose}>
            {t("common.cancel")}
          </Button>
        )}
      </div>
    </>
  );

  return (
    <>
      <Modal title="prLink.title" icon={Link2} size="lg" onClose={onClose}>
        {lookupBody}
      </Modal>

      {/* A sibling, not a child: nested inside the backdrop above, a click anywhere on the clone
          modal's own overlay would bubble up and close this one out from under it. */}
      {showClone && activeWorkspaceId && resolution?.status === "NoLocalRepo" && (
        <CloneRepoModal
          workspaceId={activeWorkspaceId}
          initialUrl={resolution.clone_url}
          onCloned={() => void onCloned()}
          onClose={() => setShowClone(false)}
        />
      )}
    </>
  );
}
