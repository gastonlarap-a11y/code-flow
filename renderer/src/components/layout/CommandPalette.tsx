import { useEffect, useMemo, useState } from "react";
import { PickerModal, PickerGroupLabel } from "../common/PickerModal";
import {
  Briefcase,
  Cloud,
  Cog,
  Download,
  FolderGit2,
  FolderPlus,
  GitBranch,
  Link2,
  MessageCircle,
  Plus,
  TerminalSquare,
} from "lucide-react";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { useRepoStore } from "../../state/repoStore";
import { useUiStore, type PaletteScope, type SettingsSectionId } from "../../state/uiStore";
import { useTerminalStore } from "../../state/terminalStore";
import { ensureApiStoreLoaded } from "../../state/apiStore";
import { useApiTabsStore } from "../../state/apiTabsStore";
import { useApiTreeStore } from "../../state/apiTreeStore";
import { useApiModalStore } from "../../state/apiModalStore";
import { useT } from "../../state/languageStore";
import { listRepoFiles } from "../../lib/ipc/commands";
import { fileIconFor } from "../../lib/fileIcon";
import { REACHABLE_MODULES } from "../../lib/modules";
import { parseQuery, queryForScope } from "../../lib/ui/commandScope";
import { rankByFuzzy } from "../../lib/ui/fuzzyScore";
import type { TranslationKey } from "../../lib/i18n/translations";

type PaletteGroup =
  | "workspaces"
  | "projects"
  | "branches"
  | "files"
  | "views"
  | "actions"
  | "api"
  | "settings";

interface PaletteItem {
  key: string;
  icon: typeof GitBranch;
  label: string;
  /** Shown after the label, dimmed — the directory of a file, and nothing else so far. */
  detail?: string;
  /**
   * Tints the icon. Set for a workspace and a repository, whose colour the user chose; left unset
   * for everything else, which keeps the muted token this list is built on.
   */
  tint?: string;
  group: PaletteGroup;
  onSelect: () => void;
}

/**
 * What each scope lists.
 *
 * A scope arrives two ways: from a shortcut ("switch repository" wants repositories, not everything
 * the app can do) or from a prefix typed into the command bar. `commands` is the `>` scope — the
 * things the app can *do*, as opposed to the places it can go — and `files`/`branches` are what used
 * to be two separate modals.
 */
const SCOPE_GROUPS: Record<PaletteScope, PaletteGroup[]> = {
  all: ["workspaces", "projects", "branches", "views", "actions", "api", "settings"],
  workspaces: ["workspaces"],
  projects: ["projects"],
  branches: ["branches"],
  files: ["files"],
  commands: ["views", "actions", "api", "settings"],
};

/**
 * Straight off the registry, rather than a fifth hand-kept copy of it.
 *
 * This list was the one `lib/modules.ts` was supposed to absorb and did not, and it had already
 * drifted: it named the API client with `api.title` and a `Zap` where the registry says
 * `tabbar.api` and `Send`, so the same module answered to two labels and two icons depending on
 * how you reached it. Deriving it means a new module is listed here the moment it is registered —
 * except the ones registered precisely because they do not exist yet, which would be a command
 * that takes you to an empty screen.
 */
const VIEW_ITEMS = REACHABLE_MODULES;

const SETTINGS_ITEMS: { id: SettingsSectionId; labelKey: TranslationKey }[] = [
  { id: "appearance", labelKey: "settings.appearance" },
  { id: "general", labelKey: "settings.general" },
  { id: "keybindings", labelKey: "shortcuts.title" },
  { id: "projects", labelKey: "settings.projects" },
  { id: "git", labelKey: "settings.git" },
  { id: "integrations", labelKey: "settings.integrationsSection" },
  { id: "claude", labelKey: "settings.aiSection" },
  { id: "review", labelKey: "settings.review" },
  { id: "sdd", labelKey: "settings.sdd" },
  { id: "skills", labelKey: "settings.skills" },
  { id: "mcps", labelKey: "settings.mcps" },
  { id: "api", labelKey: "api.settings.title" },
];

const GROUP_LABEL_KEY: Record<PaletteGroup, TranslationKey> = {
  workspaces: "sidebar.workspaces",
  projects: "sidebar.projects",
  branches: "sidebar.localBranches",
  files: "commandbar.files",
  views: "titlebar.goTo",
  actions: "titlebar.aiActions",
  api: "api.title",
  settings: "statusbar.settings",
};

/** How many file rows are drawn. Matching runs over the whole repo; nobody scrolls a thousand
 * results, they type two more letters. */
const MAX_FILE_ROWS = 40;

export function CommandPalette({
  scope: initialScope = "all",
  initialQuery = "",
  onClose,
}: {
  scope?: PaletteScope;
  initialQuery?: string;
  onClose: () => void;
}) {
  const t = useT();
  // The field starts pre-scoped when a shortcut named one, so the prefix is visible and the user can
  // widen the search by deleting it — a scope you cannot see is a scope you cannot leave.
  const [query, setQuery] = useState(() => queryForScope(initialScope, initialQuery));

  const workspaces = useWorkspaceStore((s) => s.workspaces);
  const setActiveWorkspace = useWorkspaceStore((s) => s.setActiveWorkspace);
  const activeWorkspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);
  // Selecting the raw (stably-referenced) map and only applying the `?? []` fallback in the
  // render body — not inside the selector — avoids handing useSyncExternalStore a brand-new
  // array on every store update, which previously caused a real infinite-render loop elsewhere
  // in this app (see prStore's EMPTY_PRS fix).
  const projectsByWorkspace = useWorkspaceStore((s) => s.projectsByWorkspace);
  const projects = activeWorkspaceId ? projectsByWorkspace[activeWorkspaceId] ?? [] : [];
  const setActiveProject = useWorkspaceStore((s) => s.setActiveProject);
  const branches = useRepoStore((s) => s.branches);
  const checkoutBranch = useRepoStore((s) => s.checkoutBranch);
  const checkoutRemoteBranch = useRepoStore((s) => s.checkoutRemoteBranch);
  const setActiveView = useUiStore((s) => s.setActiveView);
  const openSettings = useUiStore((s) => s.openSettings);
  const openApiModal = useApiModalStore((s) => s.openApiModal);
  const openPrLinkModal = useUiStore((s) => s.openPrLinkModal);
  const openInEditor = useUiStore((s) => s.openInEditor);
  const toggleAiPanel = useUiStore((s) => s.toggleAiPanel);
  const toggleTerminalPanel = useTerminalStore((s) => s.togglePanel);
  const activeProject = useWorkspaceStore((s) => s.activeProject());

  // The scope the field is asking for, which is either what a shortcut opened with or what the
  // prefix says. Deriving it from the text rather than tracking it separately is what lets deleting
  // the prefix widen the search back out.
  const { scope, term } = parseQuery(query);

  // Repo files, fetched only once the `@` scope is actually entered — a walk of the whole working
  // tree is not worth paying for on every palette opening, and most openings are not about files.
  // Re-read per entry rather than cached: files appear and vanish between openings, and one walk is
  // fast enough that a stale list is the worse trade.
  const [files, setFiles] = useState<string[] | null>(null);
  const repoPath = activeProject?.local_path ?? null;
  const wantsFiles = scope === "files";

  useEffect(() => {
    if (!wantsFiles || !repoPath) return;
    let cancelled = false;
    void listRepoFiles(repoPath)
      .then((result) => {
        if (!cancelled) setFiles(result);
      })
      .catch(() => {
        if (!cancelled) setFiles([]);
      });
    return () => {
      cancelled = true;
    };
  }, [wantsFiles, repoPath]);

  const items = useMemo<PaletteItem[]>(() => {
    const workspaceItems: PaletteItem[] = workspaces.map((w) => ({
      key: `workspace:${w.id}`,
      icon: Briefcase,
      label: w.name,
      tint: w.color,
      group: "workspaces",
      onSelect: () => setActiveWorkspace(w.id),
    }));

    const projectItems: PaletteItem[] = projects.map((p) => ({
      key: `project:${p.id}`,
      icon: FolderGit2,
      label: p.name,
      tint: p.color,
      group: "projects",
      onSelect: () => setActiveProject(p.id),
    }));

    const branchItems: PaletteItem[] = branches.map((b) => ({
      key: `branch:${b.name}`,
      icon: b.is_remote ? Cloud : GitBranch,
      label: b.name,
      group: "branches",
      onSelect: () => (b.is_remote ? checkoutRemoteBranch(b.name) : checkoutBranch(b.name)),
    }));

    const viewItems: PaletteItem[] = [
      ...VIEW_ITEMS.map(({ id, labelKey, icon }) => ({
        key: `view:${id}`,
        icon,
        label: t(labelKey),
        group: "views" as const,
        onSelect: () => setActiveView(id),
      })),
      {
        key: "view:ai-panel",
        icon: MessageCircle,
        label: t("chat.title"),
        group: "views" as const,
        onSelect: () => toggleAiPanel(),
      },
      {
        key: "view:terminal",
        icon: TerminalSquare,
        label: t("tabbar.terminal"),
        group: "views" as const,
        onSelect: () => toggleTerminalPanel(),
      },
    ];

    // Reviewing a PR from its link needs nothing but the link — no project open, no repo picked.
    const actionItems: PaletteItem[] = [
      {
        key: "action:pr-from-link",
        icon: Link2,
        label: t("prLink.menuItem"),
        group: "actions",
        onSelect: () => openPrLinkModal(),
      },
    ];

    // The API client is app-global, so these work with no project open — but each one has to
    // switch to the view as well, because `ApiView` is what mounts the tab strip and the modals.
    const openApi = (then?: () => void) => {
      setActiveView("api");
      void ensureApiStoreLoaded().then(() => then?.());
    };

    const apiItems: PaletteItem[] = [
      {
        key: "api:new-request",
        icon: Plus,
        label: t("api.newRequest"),
        group: "api",
        onSelect: () => openApi(() => useApiTabsStore.getState().openScratchTab()),
      },
      {
        key: "api:new-collection",
        icon: FolderPlus,
        label: t("api.newCollection"),
        group: "api",
        onSelect: () =>
          openApi(() => void useApiTreeStore.getState().createCollection(t("api.untitledCollection"))),
      },
      {
        key: "api:import",
        icon: Download,
        label: t("api.import.title"),
        group: "api",
        onSelect: () => openApi(() => openApiModal({ kind: "import" })),
      },
    ];

    const settingsItems: PaletteItem[] = SETTINGS_ITEMS.map(({ id, labelKey }) => ({
      key: `settings:${id}`,
      icon: Cog,
      label: t(labelKey),
      group: "settings",
      onSelect: () => openSettings(id),
    }));

    return [
      ...workspaceItems,
      ...projectItems,
      ...branchItems,
      ...viewItems,
      ...actionItems,
      ...apiItems,
      ...settingsItems,
    ];
  }, [
    workspaces,
    projects,
    branches,
    t,
    setActiveWorkspace,
    setActiveProject,
    checkoutBranch,
    checkoutRemoteBranch,
    setActiveView,
    openSettings,
    openApiModal,
    openPrLinkModal,
    toggleAiPanel,
    toggleTerminalPanel,
  ]);

  const groups = SCOPE_GROUPS[scope];

  /**
   * File rows, ranked the way quick-open ranks them.
   *
   * Substring matching is right for the other groups — a workspace called "Work" should not surface
   * for "wrk" — but paths are long and typed in fragments, so files get `fuzzyScore` instead. That
   * asymmetry is deliberate and is why this is a separate list rather than more `items`.
   */
  const fileItems = useMemo<PaletteItem[]>(() => {
    if (!wantsFiles || !files) return [];
    return rankByFuzzy(files, term, (path) => path, MAX_FILE_ROWS).map((path) => {
      const name = path.slice(path.lastIndexOf("/") + 1);
      const { Icon } = fileIconFor(path);
      return {
        key: `file:${path}`,
        icon: Icon,
        label: name,
        detail: path.slice(0, Math.max(0, path.length - name.length - 1)),
        group: "files" as const,
        onSelect: () => openInEditor(path),
      };
    });
  }, [wantsFiles, files, term, openInEditor]);

  const filtered = useMemo(() => {
    const q = term.trim().toLowerCase();
    const inScope = items.filter((item) => groups.includes(item.group));
    const matched = q ? inScope.filter((item) => item.label.toLowerCase().includes(q)) : inScope;
    return [...matched, ...fileItems];
  }, [items, groups, term, fileItems]);

  const choose = (item: PaletteItem) => {
    item.onSelect();
    onClose();
  };

  return (
    <PickerModal
      placeholder={scope === "all" ? t("commandbar.placeholder") : t(GROUP_LABEL_KEY[groups[0]!])}
      value={query}
      onValueChange={setQuery}
      size="lg"
      onKeyDown={(e) => {
        if (e.key === "Enter" && filtered[0]) choose(filtered[0]);
      }}
      onClose={onClose}
    >
      {groups.map((group) => {
        const groupItems = filtered.filter((item) => item.group === group);
        if (groupItems.length === 0) return null;
        return (
          <div key={group} className="mb-1">
            <PickerGroupLabel>{t(GROUP_LABEL_KEY[group])}</PickerGroupLabel>
            {groupItems.map((item) => {
              const Icon = item.icon;
              return (
                <button
                  key={item.key}
                  onClick={() => choose(item)}
                  className="cf-focusable cf-interactive flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-body hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
                >
                  <Icon
                    size={14}
                    className={item.tint ? "shrink-0" : "shrink-0 text-[var(--cf-text-muted)]"}
                    style={item.tint ? { color: item.tint } : undefined}
                    aria-hidden
                  />
                  <span className="shrink-0 truncate">{item.label}</span>
                  {item.detail && (
                    <span className="truncate text-badge text-[var(--cf-text-muted)]">{item.detail}</span>
                  )}
                </button>
              );
            })}
          </div>
        );
      })}
      {/* Three different empty states, because they mean three different things: still walking the
          tree, no repository to walk, and nothing matched. */}
      {wantsFiles && !repoPath ? (
        <p className="px-2 py-3 text-center text-ui text-[var(--cf-text-muted)]">
          {t("common.noProjectOpen")}
        </p>
      ) : wantsFiles && files === null ? (
        <p className="px-2 py-3 text-center text-ui text-[var(--cf-text-muted)]">{t("editor.loading")}</p>
      ) : (
        filtered.length === 0 && (
          <p className="px-2 py-3 text-center text-ui text-[var(--cf-text-muted)]">
            {t("titlebar.noResults")}
          </p>
        )
      )}
    </PickerModal>
  );
}
