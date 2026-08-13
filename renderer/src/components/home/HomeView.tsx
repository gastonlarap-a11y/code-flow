import { useEffect, useMemo, useState } from "react";
import {
  Clock,
  Download,
  FolderGit2,
  GitBranchPlus,
  GitPullRequest,
  MessageSquare,
  Plus,
  Send,
  Sparkles,
  Zap,
} from "lucide-react";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { useUiStore } from "../../state/uiStore";
import { usePrStore } from "../../state/prStore";
import { useChatHistoryStore } from "../../state/activityStore";
import { useApiTabsStore } from "../../state/apiTabsStore";
import { ensureApiStoreLoaded } from "../../state/apiStore";
import { useT } from "../../state/languageStore";
import { pushErrorToast } from "../../state/toastStore";
import { pickFolder } from "../../lib/ipc/commands";
import { resolveRecent } from "../../lib/ui/recentProjects";
import { HubCard } from "../common/HubCard";
import { Chip } from "../common/Chip";
import { Button } from "../common/Button";
import { CloneRepoModal } from "../layout/CloneRepoModal";
import type { Project, PullRequestSummary } from "../../types/domain";

/** A row that is the whole click target. Three cards list things you open; this is what they open
 * with, so hit area and hover live in one place instead of three. */
function HubRow({
  onClick,
  children,
}: {
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="cf-focusable flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left transition-colors hover:bg-black/[0.04] dark:hover:bg-white/[0.06]"
    >
      {children}
    </button>
  );
}

function RecentProjectsCard({ onAddProject, onClone }: { onAddProject: () => void; onClone: () => void }) {
  const activeWorkspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);
  const projectsByWorkspace = useWorkspaceStore((s) => s.projectsByWorkspace);
  const recentProjectIds = useWorkspaceStore((s) => s.recentProjectIds);
  const setActiveProject = useWorkspaceStore((s) => s.setActiveProject);
  const setActiveView = useUiStore((s) => s.setActiveView);
  const t = useT();

  const projects = useMemo(
    () => (activeWorkspaceId ? projectsByWorkspace[activeWorkspaceId] ?? [] : []),
    [activeWorkspaceId, projectsByWorkspace],
  );

  // Recency first; anything never opened falls in behind it in the workspace's own order, so a
  // fresh install still shows its repositories instead of an empty card.
  const ordered = useMemo(() => {
    const recent = resolveRecent(recentProjectIds, projects);
    const seen = new Set(recent.map((p) => p.id));
    return [...recent, ...projects.filter((p) => !seen.has(p.id))].slice(0, 6);
  }, [recentProjectIds, projects]);

  const open = (project: Project) => {
    setActiveProject(project.id);
    setActiveView("graph");
  };

  return (
    <HubCard
      title="home.recentProjects"
      icon={Clock}
      action={
        <div className="flex items-center gap-1">
          <Button variant="ghost" size="sm" icon={GitBranchPlus} onClick={onClone} disabled={!activeWorkspaceId}>
            {t("sidebar.cloneRepo")}
          </Button>
          <Button variant="ghost" size="sm" icon={Plus} onClick={onAddProject} disabled={!activeWorkspaceId}>
            {t("home.addRepo")}
          </Button>
        </div>
      }
    >
      {ordered.length === 0 ? (
        <p className="px-2 py-1 text-ui text-[var(--cf-text-muted)]">{t("sidebar.noProjects")}</p>
      ) : (
        ordered.map((project) => (
          <HubRow key={project.id} onClick={() => open(project)}>
            {/* The repository's own icon in its own colour, rather than a dot beside a nameless
                row: the colour is what tells two repositories apart at a glance, and an 8px dot was
                the smallest place it could have been put. */}
            <FolderGit2 size={14} aria-hidden className="shrink-0" style={{ color: project.color }} />
            <span className="min-w-0 flex-1 truncate text-ui font-medium text-[var(--cf-text)]">
              {project.name}
            </span>
            <span className="min-w-0 max-w-[45%] truncate text-badge text-[var(--cf-text-muted)]">
              {project.local_path}
            </span>
          </HubRow>
        ))
      )}
    </HubCard>
  );
}

function OpenPullRequestsCard() {
  const activeWorkspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);
  const projectsByWorkspace = useWorkspaceStore((s) => s.projectsByWorkspace);
  const prsByProject = usePrStore((s) => s.prsByProject);
  const selectPr = usePrStore((s) => s.selectPr);
  const setActiveProject = useWorkspaceStore((s) => s.setActiveProject);
  const openAiPanel = useUiStore((s) => s.openAiPanel);
  const t = useT();

  const projects = activeWorkspaceId ? projectsByWorkspace[activeWorkspaceId] ?? [] : [];

  // Only what is already loaded. Home does not fetch: firing one `loadPullRequests` per project on
  // a landing page would hit every configured provider on every app start, and a landing page that
  // costs a round trip per repository is not a landing page.
  const open = projects.flatMap((project) =>
    (prsByProject[project.id] ?? [])
      .filter((pr) => pr.status === "open" || pr.status === "draft")
      .map((pr) => ({ project, pr })),
  );

  const review = (project: Project, pr: PullRequestSummary) => {
    setActiveProject(project.id);
    selectPr(pr);
    openAiPanel();
  };

  return (
    <HubCard title="home.openPrs" icon={GitPullRequest}>
      {open.length === 0 ? (
        <p className="px-2 py-1 text-ui text-[var(--cf-text-muted)]">{t("home.noOpenPrs")}</p>
      ) : (
        open.slice(0, 6).map(({ project, pr }) => (
          <HubRow key={`${project.id}:${pr.id}`} onClick={() => review(project, pr)}>
            <span className="min-w-0 flex-1 truncate text-ui text-[var(--cf-text)]">{pr.title}</span>
            {pr.status === "draft" && <Chip>{t("home.draft")}</Chip>}
            <Chip tone="accent">{project.name}</Chip>
          </HubRow>
        ))
      )}
    </HubCard>
  );
}

function RecentAiCard() {
  const project = useWorkspaceStore((s) => s.activeProject());
  const byProject = useChatHistoryStore((s) => s.byProject);
  const load = useChatHistoryStore((s) => s.load);
  const openAiPanel = useUiStore((s) => s.openAiPanel);
  const t = useT();

  // One project's history, the active one — the only fetch Home makes, and it is the same call the
  // AI panel would make the moment it opens.
  useEffect(() => {
    if (project) void load(project.id);
  }, [project, load]);

  const conversations = project ? byProject[project.id] ?? [] : [];

  return (
    <HubCard title="home.recentAi" icon={Sparkles}>
      {conversations.length === 0 ? (
        <p className="px-2 py-1 text-ui text-[var(--cf-text-muted)]">{t("home.noAiActivity")}</p>
      ) : (
        conversations.slice(0, 6).map((conversation) => (
          <HubRow key={conversation.session_id} onClick={openAiPanel}>
            <MessageSquare size={13} className="shrink-0 text-[var(--cf-text-muted)]" aria-hidden />
            <span className="min-w-0 flex-1 truncate text-ui text-[var(--cf-text)]">
              {conversation.title}
            </span>
            <Chip>{t("home.turns", { n: conversation.turn_count })}</Chip>
          </HubRow>
        ))
      )}
    </HubCard>
  );
}

function QuickActionsCard({ onAddProject, onClone }: { onAddProject: () => void; onClone: () => void }) {
  const activeWorkspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);
  const setActiveView = useUiStore((s) => s.setActiveView);
  const openPrLinkModal = useUiStore((s) => s.openPrLinkModal);
  const t = useT();

  const newRequest = () => {
    setActiveView("api");
    // The API stores hydrate on first use rather than at boot, so the tab has to wait for them —
    // same order the command palette uses.
    void ensureApiStoreLoaded().then(() => {
      useApiTabsStore.getState().openScratchTab();
    });
  };

  return (
    <HubCard title="home.quickActions" icon={Zap}>
      <div className="grid grid-cols-2 gap-1.5 p-0.5">
        <Button variant="secondary" icon={Plus} onClick={onAddProject} disabled={!activeWorkspaceId}>
          {t("home.addRepo")}
        </Button>
        <Button variant="secondary" icon={Download} onClick={onClone} disabled={!activeWorkspaceId}>
          {t("sidebar.cloneRepo")}
        </Button>
        <Button variant="secondary" icon={Send} onClick={newRequest}>
          {t("api.newRequest")}
        </Button>
        <Button variant="secondary" icon={GitPullRequest} onClick={openPrLinkModal}>
          {t("prLink.menuItem")}
        </Button>
      </div>
    </HubCard>
  );
}

/**
 * Where the app opens.
 *
 * v1 landed on the commit graph, which answers "what happened in this repository" — a question you
 * only have once you have already chosen the repository. This asks the earlier one: what were you
 * doing, and what is waiting for you. Four cards, each a list you can click straight into, and no
 * state of its own beyond the clone dialog.
 *
 * It is the app-scoped module, so it renders with no workspace and no repository open, which is
 * exactly the moment its two "add a repository" buttons matter most.
 */
export function HomeView() {
  const activeWorkspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);
  const addProject = useWorkspaceStore((s) => s.addProject);
  const t = useT();
  const [showCloneModal, setShowCloneModal] = useState(false);

  // The same handler the repo navigator has. It stays duplicated rather than shared: pulling it out
  // means a module that owns "pick a folder and register it", and the two call sites differ only in
  // which button they hang off.
  const handleAddProject = async () => {
    if (!activeWorkspaceId) return;
    try {
      const folder = await pickFolder();
      if (folder === null) return;
      const name = folder.split(/[\\/]/).filter(Boolean).pop() ?? folder;
      await addProject({
        workspace_id: activeWorkspaceId,
        name,
        local_path: folder,
        remote_url: null,
        // No colour: `addProject` picks the least-used one.
        icon: "git-branch",
        ado_org: null,
        ado_project: null,
        ado_repo_id: null,
        github_owner: null,
        github_repo: null,
        github_host: null,
      });
    } catch (e) {
      pushErrorToast(t("toast.addProjectFailed", { error: String(e) }));
    }
  };

  const onAddProject = () => void handleAddProject();
  const onClone = () => setShowCloneModal(true);

  return (
    <div className="h-full overflow-y-auto">
      <div className="mx-auto flex max-w-[1100px] flex-col gap-3 p-1">
        <header className="flex items-center gap-2 px-1 pt-1">
          <FolderGit2 size={16} className="text-[var(--cf-accent)]" aria-hidden />
          <h1 className="text-title font-semibold text-[var(--cf-text)]">{t("home.heading")}</h1>
        </header>

        <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
          <RecentProjectsCard onAddProject={onAddProject} onClone={onClone} />
          <OpenPullRequestsCard />
          <RecentAiCard />
          <QuickActionsCard onAddProject={onAddProject} onClone={onClone} />
        </div>
      </div>

      {showCloneModal && activeWorkspaceId && (
        <CloneRepoModal workspaceId={activeWorkspaceId} onClose={() => setShowCloneModal(false)} />
      )}
    </div>
  );
}
