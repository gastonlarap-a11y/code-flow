import { Suspense, type ReactElement } from "react";
import { lazyRetry } from "../../lib/lazyRetry";
import { useUiStore, type MainView } from "../../state/uiStore";
import { APP_MODULES, type ModuleId } from "../../lib/modules";
import { RepoNavigator } from "./sidebar/RepoNavigator";

const ApiSidebar = lazyRetry(() => import("../api/ApiSidebar").then((m) => ({ default: m.ApiSidebar })));

/** The distinct context columns that exist. Fewer than there are modules, on purpose. */
type PanelId = "repo" | "api";

/**
 * Which column each module reads.
 *
 * The three repo modules share one panel because they share one context — which project, which
 * branch, which pull requests. That is not three panels that happen to look alike; it is one panel
 * three modules read, and saying so here is what stops it from being copied into three.
 *
 * `Record<ModuleId, …>` is the coupling worth having, same as `MODULE_VIEWS` in `App.tsx`: register
 * a module in `lib/modules.ts` without deciding what its context column shows and this stops
 * compiling. `null` is a decision too — Home is a full-width hub, and a column of repository
 * context beside it would be the sidebar it exists to make unnecessary.
 */
const MODULE_PANEL: Record<ModuleId, PanelId | null> = {
  home: null,
  graph: "repo",
  changes: "repo",
  editor: "repo",
  // Nothing to show beside a module that does not exist yet. The real one will want a list of work
  // items here, which is the whole reason the panel dispatches per module.
  workitems: null,
  api: "api",
};

/**
 * What each column renders.
 *
 * Kept apart from `lib/modules.ts` for the reason that file states: the registry holds identity,
 * label, icon and scope, and imports no React, so `uiStore` and `shortcuts.ts` can depend on it.
 * Elements live in components. `ApiSidebar` is `lazy` because it is the door to the whole API
 * client — the collection tree, four of its stores — and none of that belongs in the first paint.
 */
const PANEL_VIEWS: Record<PanelId, () => ReactElement> = {
  repo: () => <RepoNavigator />,
  api: () => (
    <Suspense fallback={null}>
      <ApiSidebar />
    </Suspense>
  ),
};

const PANEL_IDS = Object.keys(PANEL_VIEWS) as PanelId[];

/** Which modules a column serves — derived, so a module added to the registry cannot be left out. */
const MODULES_BY_PANEL: Record<PanelId, ModuleId[]> = PANEL_IDS.reduce(
  (acc, panel) => {
    acc[panel] = APP_MODULES.filter((m) => MODULE_PANEL[m.id] === panel).map((m) => m.id);
    return acc;
  },
  {} as Record<PanelId, ModuleId[]>,
);

/**
 * The second column: the active module's context.
 *
 * Mounted-hidden, like the views in `App.tsx` and for the same reason. Rendering only the active
 * module's column would tear down the API sidebar's tab and its search box every time you glanced
 * at the commit graph — the requests themselves live in stores and would survive, but the panel's
 * own state does not, and losing it on a module switch is the class of regression that keeps the
 * views mounted in the first place. A column appears the first time a module it serves is opened,
 * and then stays.
 *
 * `contextPanelOpen` hides the whole thing. Its control is in `NavigationSidebar`, which is where a
 * control that brings a hidden panel back has to be.
 */
export function ContextPanel({ visited }: { visited: ReadonlySet<MainView> }) {
  const activeView = useUiStore((s) => s.activeView);
  const contextPanelOpen = useUiStore((s) => s.contextPanelOpen);

  if (!contextPanelOpen) return null;

  return (
    <>
      {PANEL_IDS.filter((panel) => MODULES_BY_PANEL[panel].some((id) => visited.has(id))).map(
        (panel) => (
          // `contents` rather than a wrapper box: each panel still renders its own aside *and* its
          // own resize handle as siblings, and they have to stay direct children of the app row for
          // the drag to size the aside instead of an invisible div around it.
          <div
            key={panel}
            className={MODULE_PANEL[activeView] === panel ? "contents" : "hidden"}
          >
            {PANEL_VIEWS[panel]()}
          </div>
        ),
      )}
    </>
  );
}
