import { Suspense, useEffect, useState, type ReactElement } from "react";
import { lazyRetry } from "./lib/lazyRetry";
import { AnimatePresence } from "framer-motion";
import { FolderGit2, SquareKanban } from "lucide-react";
import { useT } from "./state/languageStore";
import { CommandHeader } from "./components/layout/CommandHeader";
import { SidecarBanner } from "./components/layout/SidecarBanner";
import { NavigationSidebar } from "./components/layout/NavigationSidebar";
import { ContextPanel } from "./components/layout/ContextPanel";
import { HomeView } from "./components/home/HomeView";
import { GraphView } from "./components/git/GraphView";
import { ChangesPanel } from "./components/git/ChangesPanel";
import { AiPanel } from "./components/ai/AiPanel";
import { TerminalDock } from "./components/terminal/TerminalDock";
import { CommandPalette } from "./components/layout/CommandPalette";
import { ShortcutsModal } from "./components/layout/ShortcutsModal";
import { OpenPrLinkModal } from "./components/layout/OpenPrLinkModal";
import { UpdateNotesModal } from "./components/layout/UpdateNotesModal";
import { UpdateAlert } from "./components/layout/UpdateAlert";
import { EmptyState } from "./components/common/EmptyState";
import { ErrorBoundary } from "./components/common/ErrorBoundary";
import { SkeletonRows } from "./components/common/Skeleton";
import { ToastContainer } from "./components/common/Toast";
import { ConfirmModal } from "./components/common/ConfirmModal";
import { useThemeStore } from "./state/themeStore";
import { useUiStore, type MainView } from "./state/uiStore";
import { moduleById, modulesInScope, type ModuleId } from "./lib/modules";
import { useWorkspaceStore } from "./state/workspaceStore";
import { useLayoutStore } from "./state/layoutStore";
import { useRepoStore } from "./state/repoStore";
import { useApiStore } from "./state/apiStore";
import { usePreferencesStore } from "./state/preferencesStore";
import { useAiProviderStore } from "./state/aiProviderStore";
import { useLanguageStore } from "./state/languageStore";
import { useAccentStore } from "./state/accentStore";
import { useFetchTimerStore } from "./state/fetchTimerStore";
import { useUpdateStore, CHECK_INTERVAL_MS } from "./state/updateStore";
import { useNavigationStore } from "./state/navigationStore";
import { useTerminalStore } from "./state/terminalStore";
import { useShortcutsStore } from "./state/shortcutsStore";
import { useDensityStore } from "./state/densityStore";
import { useSidecarStore } from "./state/sidecarStore";
import { useGlobalShortcuts } from "./lib/useGlobalShortcuts";
import { startWatching, stopWatching } from "./lib/ipc/commands";
import { onRepoFsChanged } from "./lib/ipc/events";

// Split out of the entry chunk. Each of the three is the root of a subtree that pulls its own
// weight in — Monaco for the editor, the whole API client for `ApiView`, twelve settings panels
// for `SettingsView` — and none of them is on the path to the first paint.
//
// `.then` rather than a default export from each: `lazy` wants one, and the codebase exports by
// name everywhere else.
const EditorView = lazyRetry(() =>
  import("./components/editor/EditorView").then((m) => ({ default: m.EditorView })),
);
const ApiView = lazyRetry(() => import("./components/api/ApiView").then((m) => ({ default: m.ApiView })));
const SettingsView = lazyRetry(() =>
  import("./components/settings/SettingsView").then((m) => ({ default: m.SettingsView })),
);

/**
 * What each module renders.
 *
 * The registry (`lib/modules.ts`) owns identity, label, icon and scope; this owns the element, and
 * only this, because what lives here is a code-splitting decision rather than a fact about the
 * module. `HomeView`, `GraphView` and `ChangesPanel` stay eager on purpose: Home is the landing
 * view, so its chunk is requested during the first paint anyway and splitting it would only add a
 * round trip, and Graph is one click behind it. The other two are the roots of subtrees that pull
 * their own weight in — Monaco, the whole API client — and neither is on the path to that paint.
 *
 * `Record<ModuleId, …>` is the coupling that matters: registering a module without giving it
 * something to render stops compiling here.
 */
const MODULE_VIEWS: Record<ModuleId, () => ReactElement> = {
  home: () => <HomeView />,
  graph: () => <GraphView />,
  changes: () => <ChangesPanel />,
  editor: () => <EditorView />,
  // Registered ahead of the feature (§7 of the proposal) and unreachable by design — the navigation
  // shows it disabled and nothing else routes to it. This entry exists because `Record<ModuleId, …>`
  // demands one, and it is the placeholder the real module replaces.
  workitems: () => <ComingSoonView />,
  api: () => <ApiView />,
};

/** What a registered-but-unbuilt module renders if something ever does reach it. */
function ComingSoonView() {
  const t = useT();
  return <EmptyState icon={SquareKanban} title={t("tabbar.workitems")} subtitle={t("settings.comingSoon")} />;
}

const REPO_MODULES = modulesInScope("repo");
/** Modules that aren't about a repository, so the "no project open" empty state must not swallow
 * them — but that do belong to a workspace. The API client owns the workspace's
 * collections/environments and is expected to be usable before any repo has been added to it. */
const WORKSPACE_MODULES = modulesInScope("workspace");
/** Modules that follow nothing: Home renders before there is a workspace to belong to, which is
 * the state it is most useful in — it is where the "add a repository" buttons live. */
const APP_SCOPE_MODULES = modulesInScope("app");

/**
 * Which modules have been opened at least once this session.
 *
 * Owned by `App` rather than by `MainContent` because the context panel needs the same answer: both
 * columns keep what they have mounted, and two independently-grown sets would be two answers to one
 * question, drifting the first time one of them is updated somewhere the other is not.
 */
function useVisitedModules(activeView: MainView): ReadonlySet<MainView> {
  const [visited, setVisited] = useState<Set<MainView>>(new Set());

  useEffect(() => {
    setVisited((prev) => (prev.has(activeView) ? prev : new Set(prev).add(activeView)));
  }, [activeView]);

  return visited;
}

function MainContent({ visited }: { visited: ReadonlySet<MainView> }) {
  const activeView = useUiStore((s) => s.activeView);
  const project = useWorkspaceStore((s) => s.activeProject());
  const workspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);
  const t = useT();

  const workspaceViewOpen =
    workspaceId !== null && WORKSPACE_MODULES.some((m) => m.id === activeView);
  const appViewOpen = APP_SCOPE_MODULES.some((m) => m.id === activeView);

  // The empty state answers "you have no repository open" — true, and useless on Home, which is the
  // screen that exists to fix it.
  if (!project && !workspaceViewOpen && !appViewOpen) {
    return (
      <EmptyState icon={FolderGit2} title={t("common.noProjectOpen")} subtitle={t("common.openProjectHint")} />
    );
  }

  // Once a view has been opened it stays mounted (just hidden) so switching tabs doesn't kill
  // in-progress state — the Terminal's shell session, and now also the API client's live
  // WebSocket/MQTT connections and unsaved request drafts, all of which would otherwise be
  // torn down every time you tabbed away. Views never opened yet aren't mounted at all.
  return (
    <>
      {APP_SCOPE_MODULES.filter(({ id }) => visited.has(id)).map(({ id }) => (
        <div key={id} className={activeView === id ? "h-full" : "hidden"}>
          <Suspense fallback={<SkeletonRows count={12} />}>{MODULE_VIEWS[id]()}</Suspense>
        </div>
      ))}
      {project &&
        REPO_MODULES.filter(({ id }) => visited.has(id)).map(({ id }) => (
          <div key={id} className={activeView === id ? "h-full" : "hidden"}>
            <Suspense fallback={<SkeletonRows count={12} />}>{MODULE_VIEWS[id]()}</Suspense>
          </div>
        ))}
      {workspaceId !== null &&
        WORKSPACE_MODULES.filter(({ id }) => visited.has(id)).map(({ id }) => (
          <div key={id} className={activeView === id ? "h-full" : "hidden"}>
            <Suspense fallback={<SkeletonRows count={12} />}>{MODULE_VIEWS[id]()}</Suspense>
          </div>
        ))}
    </>
  );
}

export default function App() {
  const initTheme = useThemeStore((s) => s.init);
  const initLayout = useLayoutStore((s) => s.init);
  const initPreferences = usePreferencesStore((s) => s.init);
  const initLanguage = useLanguageStore((s) => s.init);
  const initAccent = useAccentStore((s) => s.init);
  const initTerminal = useTerminalStore((s) => s.init);
  const initAiProvider = useAiProviderStore((s) => s.init);
  const initShortcuts = useShortcutsStore((s) => s.init);
  const initDensity = useDensityStore((s) => s.init);
  const loadWorkspaces = useWorkspaceStore((s) => s.loadWorkspaces);
  const project = useWorkspaceStore((s) => s.activeProject());
  const workspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);
  const setRepoPath = useRepoStore((s) => s.setRepoPath);
  const autoFetchSeconds = usePreferencesStore((s) => s.autoFetchSeconds);
  const resolvedTheme = useThemeStore((s) => s.resolved);
  const accentId = useAccentStore((s) => s.accentId);
  const activeView = useUiStore((s) => s.activeView);
  const aiPanelOpen = useUiStore((s) => s.aiPanelOpen);
  const terminalPanelOpen = useTerminalStore((s) => s.panelOpen);
  const commandPaletteOpen = useUiStore((s) => s.commandPaletteOpen);
  const commandPaletteScope = useUiStore((s) => s.commandPaletteScope);
  const commandPaletteQuery = useUiStore((s) => s.commandPaletteQuery);
  const closeCommandPalette = useUiStore((s) => s.closeCommandPalette);
  const settingsOpen = useUiStore((s) => s.settingsOpen);
  const settingsSection = useUiStore((s) => s.settingsSection);
  const t = useT();
  const shortcutsModalOpen = useUiStore((s) => s.shortcutsModalOpen);
  const closeShortcutsModal = useUiStore((s) => s.closeShortcutsModal);
  const prLinkModalOpen = useUiStore((s) => s.prLinkModalOpen);
  const closePrLinkModal = useUiStore((s) => s.closePrLinkModal);
  const visited = useVisitedModules(activeView);

  useGlobalShortcuts();

  useEffect(() => {
    void (async () => {
      await Promise.all([
        initTheme(),
        initLayout(),
        initPreferences(),
        initLanguage(),
        initAccent(),
        initTerminal(),
        initAiProvider(),
        initShortcuts(),
        initDensity(),
        // Startup, not a panel's business. This used to hang off the sidebar's mount, which worked
        // only because the sidebar was always mounted; once the repo navigator moved behind the
        // context panel and Home became the landing view, nothing loaded the workspaces at all —
        // and since the repo modules disable themselves without a project, the one place that
        // loaded them had become unreachable. The app opened on an empty Home and stayed there.
        loadWorkspaces(),
      ]);
      useAccentStore.getState().apply(useThemeStore.getState().resolved);
    })();
  }, [
    initTheme,
    initLayout,
    initPreferences,
    initLanguage,
    initAccent,
    initTerminal,
    initAiProvider,
    initShortcuts,
    initDensity,
    loadWorkspaces,
  ]);

  // Kept out of the `init` batch above: every one of those calls the sidecar, so if the core is
  // down they all reject, and the thing that explains why must not be waiting behind them.
  useEffect(() => {
    const unlisten = useSidecarStore.getState().init();
    return () => {
      void unlisten.then((f) => f());
    };
  }, []);

  // Re-apply the chosen accent whenever the resolved theme or the accent selection changes,
  // since the actual hex differs per theme (a lighter shade is used on dark backgrounds).
  useEffect(() => {
    useAccentStore.getState().apply(resolvedTheme);
  }, [resolvedTheme, accentId]);

  // Single source of truth for which repo the git engine points at — covers manual
  // sidebar clicks *and* the auto-selected first project on load/reload, which
  // previously left branches/commits empty until the user re-clicked it.
  useEffect(() => {
    void setRepoPath(project?.local_path ?? null);
  }, [project?.local_path, setRepoPath]);

  // The API client's collections, environments, history and cookies belong to the workspace, so
  // a switch has to swap them the way the repo above swaps. Only the id is passed: the store
  // owns the teardown of what the previous workspace left running (live WebSocket/MQTT
  // connections, open request tabs), so there is nothing here to keep in step with it.
  useEffect(() => {
    if (!workspaceId) return;
    void useApiStore.getState().setWorkspace(workspaceId);
  }, [workspaceId]);

  // Looks for a newer release: once on launch, then every hour for as long as the app is open.
  // The focus listener is the catch-up for a machine that slept through several ticks — a
  // laptop reopened on Monday would otherwise keep Friday's answer until the next hour was up.
  // Every one of these is silent unless it finds something; see `checkNow`.
  useEffect(() => {
    void useUpdateStore.getState().loadCurrentVersion();
    void useUpdateStore.getState().checkNow();
    const id = setInterval(() => void useUpdateStore.getState().checkNow(), CHECK_INTERVAL_MS);
    const onFocus = () => {
      const { lastCheckedAt } = useUpdateStore.getState();
      if (lastCheckedAt === null || Date.now() - lastCheckedAt >= CHECK_INTERVAL_MS) {
        void useUpdateStore.getState().checkNow();
      }
    };
    window.addEventListener("focus", onFocus);
    return () => {
      clearInterval(id);
      window.removeEventListener("focus", onFocus);
    };
  }, []);

  // Records every view/project change onto the back/forward history — TitleBar's
  // chevrons just replay entries from this stack.
  useEffect(() => {
    useNavigationStore.getState().push({ view: activeView, projectId: project?.id ?? null });
  }, [activeView, project?.id]);

  // Watch the active project's working tree so external changes — an edit made in the
  // embedded Editor, in VS Code, from a terminal `git` command, anything — show up in
  // Changes/Graph automatically instead of only after the app's own git actions.
  useEffect(() => {
    const path = project?.local_path;
    if (!path) return;
    void startWatching(path);
    return () => {
      void stopWatching(path);
    };
  }, [project?.local_path]);

  useEffect(() => {
    const unlisten = onRepoFsChanged((e) => {
      const activePath = useWorkspaceStore.getState().activeProject()?.local_path;
      if (e.repo_path !== activePath) return;
      // Full refresh, not just status/commits — an external change can just as easily be a
      // branch switch, a stash, or a merge (all of which used to go stale until something
      // else happened to trigger a refresh).
      void useRepoStore.getState().refreshAll();
    });
    return () => {
      void unlisten.then((f) => f());
    };
  }, []);

  // Background auto-fetch with a live countdown, gated on a user-configured interval
  // (min 10s, 0 = off). Ticks every second so the status bar can show "next fetch in Ns".
  useEffect(() => {
    if (!autoFetchSeconds || !project?.local_path) {
      useFetchTimerStore.getState().setRemaining(null);
      return;
    }
    useFetchTimerStore.getState().setRemaining(autoFetchSeconds);
    const id = setInterval(() => {
      const remaining = useFetchTimerStore.getState().remainingSeconds;
      if (remaining === null) return;
      if (remaining <= 1) {
        void useRepoStore.getState().fetch();
        useFetchTimerStore.getState().setRemaining(autoFetchSeconds);
      } else {
        useFetchTimerStore.getState().setRemaining(remaining - 1);
      }
    }, 1000);
    return () => clearInterval(id);
  }, [autoFetchSeconds, project?.local_path]);

  return (
    <div className="flex h-screen flex-col overflow-hidden">
      <CommandHeader />
      {/* Above everything, below the header: when the core is down every panel underneath is
          inert, so the explanation cannot be tucked into one of them. */}
      <SidecarBanner />
      {/* The update notice used to hang off the top edge of the status bar. With the bar gone it
          anchors under the header, which is where the rest of the app's global state now lives. */}
      <div className="relative">
        <UpdateAlert />
      </div>
      {/* The ambient gradients used to sit behind the active view alone, because everything else
          was a flush panel covering them. Now every panel is a card with space around it, so this
          is the canvas the whole app floats on — one layer, shared, rather than a wash per column
          that would have to be kept in step. `index.css` says the same thing from the token side. */}
      <div className="cf-ambient-bg flex min-h-0 flex-1 gap-1.5 overflow-hidden p-2">
        <NavigationSidebar />
        {/* One boundary per column, keyed on the active module: a view that throws takes its own
            island down and leaves the rest of the app usable, and walking away from it clears the
            fallback. Without these, one bad render blanked the entire window. */}
        <ErrorBoundary area={t("nav.contextPanel")} resetKey={activeView}>
          <ContextPanel visited={visited} />
        </ErrorBoundary>
        <div className="flex min-w-0 flex-1 flex-col gap-1.5 overflow-hidden">
          <div className="min-h-0 flex-1 overflow-hidden">
            <ErrorBoundary area={t(moduleById(activeView).labelKey)} resetKey={activeView}>
              <MainContent visited={visited} />
            </ErrorBoundary>
          </div>
          <AnimatePresence initial={false}>
            {terminalPanelOpen && <TerminalDock key="terminal-dock" />}
          </AnimatePresence>
        </div>
        <AnimatePresence initial={false}>{aiPanelOpen && <AiPanel key="ai-panel" />}</AnimatePresence>
      </div>
      {/* Mounted only while open, which is what makes the `lazy` above worth anything: rendered
          unconditionally, its chunk would be requested on the first frame no matter that the
          component returns null. `SettingsView` keeps its own `open` guard — that file is where
          v1.7.6's hook-order bug lived, and it is not worth touching to remove a branch. */}
      {/* The boundary wraps the `Suspense`, not the other way round: a `lazy` whose chunk fails to
          load throws into the nearest boundary, and a settings panel that cannot be fetched should
          say so rather than blank the window behind it. */}
      {settingsOpen && (
        <ErrorBoundary area={t("statusbar.settings")} resetKey={settingsSection}>
          <Suspense fallback={null}>
            <SettingsView />
          </Suspense>
        </ErrorBoundary>
      )}
      {/* All three are reachable from the keyboard anywhere in the app, so they're mounted at the
          root rather than inside whichever panel happens to have a button for them. The branch
          switcher used to be a fourth; it is a scope of the palette now. */}
      {commandPaletteOpen && (
        <CommandPalette
          scope={commandPaletteScope}
          initialQuery={commandPaletteQuery}
          onClose={closeCommandPalette}
        />
      )}
      {shortcutsModalOpen && <ShortcutsModal onClose={closeShortcutsModal} />}
      {prLinkModalOpen && <OpenPrLinkModal onClose={closePrLinkModal} />}
      {/* Owns its own open flag rather than one in uiStore: nothing but the update badge and the
          Settings panel ever opens it, and both go through the update store already. */}
      <UpdateNotesModal />
      <ToastContainer />
      <ConfirmModal />
    </div>
  );
}
