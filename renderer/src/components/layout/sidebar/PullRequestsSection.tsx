import { useEffect, useRef, useState } from "react";
import type {
  CircleDot} from "lucide-react";
import {
  Archive,
  Cloud,
  GitFork,
  GitMerge,
  GitPullRequest,
  Globe,
  Lock,
  Plus,
  RefreshCw,
} from "lucide-react";
import { useUiStore } from "../../../state/uiStore";
import { usePrStore } from "../../../state/prStore";
import { autoLinkProject, openRepoInBrowser } from "../../../lib/ipc/commands";
import { loadGithubConnections } from "../../../lib/githubConnections";
import { loadAdoConnections } from "../../../lib/adoConnections";
import type { GithubConnection, Project, PullRequestSummary, VcsProvider } from "../../../types/domain";
import { CollapsibleSection } from "../../common/CollapsibleSection";
import { IconButton } from "../../common/IconButton";
import { Tooltip } from "../../common/Tooltip";
import { SkeletonRows } from "../../common/Skeleton";
import { ConnectAdoModal } from "../ConnectAdoModal";
import { ConnectGithubModal } from "../ConnectGithubModal";
import { CreatePrModal } from "../CreatePrModal";
import { pushErrorToast } from "../../../state/toastStore";
import { useT } from "../../../state/languageStore";
import type { TranslationKey } from "../../../lib/i18n/translations";

const PR_SECTIONS: { key: string; labelKey: TranslationKey }[] = [
  { key: "open", labelKey: "sidebar.openPRs" },
  { key: "draft", labelKey: "sidebar.draftPRs" },
  { key: "merged", labelKey: "sidebar.merged" },
  { key: "closed", labelKey: "sidebar.closed" },
];

const PR_STATUS_ICON: Record<string, typeof CircleDot> = {
  open: GitPullRequest,
  draft: GitPullRequest,
  merged: GitMerge,
  closed: Archive,
};

// A stable reference so the "no PRs loaded yet" fallback doesn't allocate a new array on
// every selector read — Zustand's snapshot check treats a fresh `[]` as "changed" forever,
// which spins the component into an infinite re-render loop.
const EMPTY_PRS: PullRequestSummary[] = [];

type LinkState =
  | { status: "checking" }
  | { status: "linked" }
  | { status: "needsToken"; provider: VcsProvider; identifier: string }
  | { status: "notDetected" };

// Which PR hosts have a saved token — decides whether a repo whose host couldn't be
// auto-detected can still be linked manually, and to which provider(s)/host(s).
interface HostingState {
  /** Connected Azure DevOps organizations (empty if none have a saved PAT). */
  ado: string[];
  /** Configured GitHub connections (github.com and/or Enterprise hosts). */
  github: GithubConnection[];
}

export function PullRequestsSection({ project }: { project: Project }) {
  const t = useT();
  const prs = usePrStore((s) => s.prsByProject[project.id] ?? EMPTY_PRS);
  const loading = usePrStore((s) => s.loadingProjectId === project.id);
  const loadError = usePrStore((s) => s.loadErrorByProject[project.id]);
  const credentialRefused = usePrStore((s) => s.credentialRefusedByProject[project.id]);
  const loadPullRequests = usePrStore((s) => s.loadPullRequests);
  const selectPr = usePrStore((s) => s.selectPr);
  const selectedPr = usePrStore((s) => s.selectedPr);
  const openAiPanel = useUiStore((s) => s.openAiPanel);
  const openSettings = useUiStore((s) => s.openSettings);
  const settingsOpen = useUiStore((s) => s.settingsOpen);
  const [hosting, setHosting] = useState<HostingState | undefined>(undefined);
  const [showConnect, setShowConnect] = useState<false | VcsProvider>(false);
  const [showCreatePr, setShowCreatePr] = useState(false);

  const initiallyLinked = Boolean(
    (project.ado_org && project.ado_project && project.ado_repo_id) ||
      (project.github_owner && project.github_repo),
  );
  const [linkState, setLinkState] = useState<LinkState>(
    initiallyLinked ? { status: "linked" } : { status: "checking" },
  );

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      const ado = await loadAdoConnections().catch(() => []);
      const github = await loadGithubConnections().catch(() => []);
      if (!cancelled) setHosting({ ado: ado.map((c) => c.org), github });
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  // Tries to derive the PR host (Azure DevOps org/project/repo or GitHub owner/repo) straight
  // from this repo's own remote URL — git already knows where the repo lives, so there's no
  // reason to make the user pick it again.
  const runAutoDetect = async (cancelledRef: { current: boolean }) => {
    try {
      const result = await autoLinkProject(project.id);
      if (cancelledRef.current) return;
      if (result.status === "Linked") setLinkState({ status: "linked" });
      else if (result.status === "NeedsToken")
        setLinkState({ status: "needsToken", provider: result.provider, identifier: result.identifier });
      else setLinkState({ status: "notDetected" });
    } catch {
      if (!cancelledRef.current) setLinkState({ status: "notDetected" });
    }
  };

  useEffect(() => {
    if (initiallyLinked) {
      setLinkState({ status: "linked" });
      return;
    }
    const cancelledRef = { current: false };
    void runAutoDetect(cancelledRef);
    return () => {
      cancelledRef.current = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project.id]);

  useEffect(() => {
    if (linkState.status === "linked") void loadPullRequests(project.id);
  }, [linkState.status, project.id]);

  // Re-detect when Settings closes: a token/connection may have just been added there, so the
  // repo should bind to its host on its own — no manual "connect" click and no switching away
  // and back to trigger it.
  const wasSettingsOpen = useRef(settingsOpen);
  useEffect(() => {
    const justClosed = wasSettingsOpen.current && !settingsOpen;
    wasSettingsOpen.current = settingsOpen;
    if (!justClosed || linkState.status === "linked") return;
    const ref = { current: false };
    void (async () => {
      const ado = await loadAdoConnections().catch(() => []);
      const github = await loadGithubConnections().catch(() => []);
      if (ref.current) return;
      setHosting({ ado: ado.map((c) => c.org), github });
      await runAutoDetect(ref);
    })();
    return () => {
      ref.current = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [settingsOpen]);

  const onConnected = () => {
    setLinkState({ status: "linked" });
    void loadPullRequests(project.id);
  };

  // The "planet" shortcut — open this repo's home page on its host (GitHub / Azure DevOps) in
  // the browser. The backend derives the URL from the repo's actual remote.
  const openRepo = async () => {
    try {
      await openRepoInBrowser(project.id);
    } catch (e) {
      pushErrorToast(String(e));
    }
  };

  if (hosting === undefined || linkState.status === "checking") {
    return (
      <CollapsibleSection icon={GitPullRequest} title={t("sidebar.pullRequests")}>
        <SkeletonRows count={2} className="p-0" />
      </CollapsibleSection>
    );
  }

  if (linkState.status === "needsToken") {
    const provider = linkState.provider;
    return (
      <CollapsibleSection icon={GitPullRequest} title={t("sidebar.pullRequests")}>
        <p className="px-1.5 text-ui text-[var(--cf-text-muted)]">
          {provider === "github"
            ? t("sidebar.needsGithubToken")
            : t("sidebar.needsTokenFor", { org: linkState.identifier })}{" "}
          <button onClick={() => openSettings("integrations", provider)} className="text-[var(--cf-accent)] hover:underline">
            {t("statusbar.settings")}
          </button>
        </p>
      </CollapsibleSection>
    );
  }

  if (linkState.status === "notDetected" && hosting.ado.length === 0 && hosting.github.length === 0) {
    return (
      <CollapsibleSection icon={GitPullRequest} title={t("sidebar.pullRequests")}>
        <div className="space-y-0.5">
          {PR_SECTIONS.map((section) => (
            <Tooltip key={section.key} label={t("sidebar.connectRequired")}>
              <div className="flex items-center gap-1.5 rounded-md px-1.5 py-0.5 text-body text-[var(--cf-text-muted)]/60">
                <Lock size={12} aria-hidden />
                <span>{t(section.labelKey)}</span>
              </div>
            </Tooltip>
          ))}
        </div>
      </CollapsibleSection>
    );
  }

  if (linkState.status === "notDetected") {
    return (
      <CollapsibleSection icon={GitPullRequest} title={t("sidebar.pullRequests")}>
        {hosting.github.length > 0 && (
          <button
            onClick={() => setShowConnect("github")}
            className="flex w-full items-center gap-1.5 rounded-md px-1.5 py-1 text-ui text-[var(--cf-accent)] hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
          >
            <GitFork size={14} aria-hidden />
            {t("sidebar.linkGithubRepo")}
          </button>
        )}
        {hosting.ado.length > 0 && (
          <button
            onClick={() => setShowConnect("azure")}
            className="flex w-full items-center gap-1.5 rounded-md px-1.5 py-1 text-ui text-[var(--cf-accent)] hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
          >
            <Cloud size={14} aria-hidden />
            {t("sidebar.linkAdoRepo")}
          </button>
        )}
        {showConnect === "azure" && hosting.ado.length > 0 && (
          <ConnectAdoModal
            projectId={project.id}
            orgs={hosting.ado}
            onConnected={onConnected}
            onClose={() => setShowConnect(false)}
          />
        )}
        {showConnect === "github" && (
          <ConnectGithubModal
            projectId={project.id}
            hosts={hosting.github.map((c) => c.host)}
            onConnected={onConnected}
            onClose={() => setShowConnect(false)}
          />
        )}
      </CollapsibleSection>
    );
  }

  return (
    <CollapsibleSection
      icon={GitPullRequest}
      title={t("sidebar.pullRequests")}
      action={
        <div className="flex items-center">
          {/* Bare 11px and 12px glyphs before this, with the icon itself as the whole hit target. */}
          <IconButton label="createPr.title" icon={Plus} onClick={() => setShowCreatePr(true)} />
          <IconButton label="sidebar.openRepoInBrowser" icon={Globe} onClick={openRepo} />
          <IconButton
            label="sidebar.refreshPrs"
            icon={RefreshCw}
            pending={loading}
            onClick={() => void loadPullRequests(project.id)}
          />
        </div>
      }
    >
      {loadError ? (
        <div className="space-y-1 px-1.5">
          <p className="text-ui text-[var(--cf-danger)]">
            {credentialRefused ? t("sidebar.credentialRefused") : t("sidebar.prLoadError")}
          </p>
          <p className="text-badge text-[var(--cf-text-muted)]">{loadError}</p>
          <div className="flex items-center gap-3">
            {/* A refused credential will be refused again, so retrying is the wrong offer — the
                token has to be replaced first. Both are shown for anything else. */}
            {credentialRefused ? (
              <button
                // "azure" literally: `AzureException.RefusedPrefix` is the only thing that sets this
                // flag today, so pretending to be provider-agnostic here would be a guess dressed as
                // generality. Widening it is one more prefix away.
                onClick={() => openSettings("integrations", "azure")}
                className="text-badge text-[var(--cf-accent)] hover:underline"
              >
                {t("statusbar.settings")}
              </button>
            ) : (
              <button
                onClick={() => void loadPullRequests(project.id)}
                className="text-badge text-[var(--cf-accent)] hover:underline"
              >
                {t("sidebar.retry")}
              </button>
            )}
          </div>
        </div>
      ) : (
        <div className="space-y-2">
          {PR_SECTIONS.map((section) => {
            const items = prs.filter((pr) => pr.status === section.key);
            const Icon = PR_STATUS_ICON[section.key] ?? GitPullRequest;
            return (
              <div key={section.key}>
                <p className="px-1.5 text-badge font-medium text-[var(--cf-text-muted)]">
                  {t(section.labelKey)} ({items.length})
                </p>
                <div className="space-y-0.5">
                  {items.map((pr) => (
                    <button
                      key={pr.id}
                      onClick={() => {
                        selectPr(pr);
                        openAiPanel();
                      }}
                      className={`flex w-full items-center gap-1.5 truncate rounded-md px-1.5 py-0.5 text-left text-ui ${
                        selectedPr?.id === pr.id
                          ? "bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]"
                          : "text-[var(--cf-text-muted)] hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
                      }`}
                    >
                      <Icon size={11} className="shrink-0" />
                      <span className="min-w-0 flex-1 truncate">{pr.title}</span>
                    </button>
                  ))}
                  {items.length === 0 && !loading && (
                    <p className="px-1.5 text-badge text-[var(--cf-text-muted)]">{t("sidebar.noPRsInSection")}</p>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      )}
      {showCreatePr && (
        <CreatePrModal
          project={project}
          onClose={() => setShowCreatePr(false)}
          onCreated={() => {
            openAiPanel();
          }}
        />
      )}
    </CollapsibleSection>
  );
}
