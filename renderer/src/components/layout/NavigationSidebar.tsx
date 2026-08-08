import { PanelLeftClose, PanelLeftOpen, PanelRight, type LucideIcon } from "lucide-react";
import { useUiStore } from "../../state/uiStore";
import { useRepoStore } from "../../state/repoStore";
import { usePrStore } from "../../state/prStore";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { useT } from "../../state/languageStore";
import { reachableInScope, type AppModule, type RegisteredModule } from "../../lib/modules";
import { navBadges } from "../../lib/ui/navBadges";
import { uncommittedCount } from "../../lib/fileStatus";
import { CARD } from "../common/panelChrome";
import { IconButton } from "../common/IconButton";
import { ActivePill } from "../common/ActivePill";
import type { TranslationKey } from "../../lib/i18n/translations";

const RAIL_WIDTH = 48;
const EXPANDED_WIDTH = 208;

/** One id for the whole column: the pill has to slide between the two groups, and framer only
 * tweens between nodes sharing a `layoutId`. */
const PILL_ID = "cf-nav-pill";

/** `app` has no heading: it is one entry, and a group label over a single row names nothing the row
 * does not already say. The gap below it is what separates Home from the scoped modules. */
const SCOPE_LABELS: Record<AppModule["scope"], TranslationKey | null> = {
  app: null,
  repo: "nav.scopeRepo",
  workspace: "nav.scopeWorkspace",
};

function NavItem({
  module,
  active,
  badge,
  collapsed,
  disabled,
  onSelect,
}: {
  module: RegisteredModule;
  active: boolean;
  /** Explicitly `| undefined`: the repo runs with `exactOptionalPropertyTypes`, so an optional prop
   * a parent passes as `badges[id]` has to admit the miss. */
  badge?: number | undefined;
  collapsed: boolean;
  disabled: boolean;
  onSelect: () => void;
}) {
  const t = useT();
  const label = t(module.labelKey);
  const Icon: LucideIcon = module.icon;

  // Collapsed, the label *is* the tooltip, which is the whole reason the rail can drop it: the
  // accessible name comes from the same string either way.
  if (collapsed) {
    return (
      <div className="relative flex justify-center">
        {active && <ActivePill layoutId={PILL_ID} />}
        {/* Positioned, so it paints above the pill. An absolutely-positioned sibling always paints
            over a static one no matter which comes first in the DOM, and without this the pill
            covered the glyph outright: the active rail entry rendered as a solid accent block. */}
        <span className="relative flex">
          <IconButton
            label={module.labelKey}
            icon={Icon}
            active={active}
            disabled={disabled}
            onClick={onSelect}
          />
        </span>
        {badge !== undefined && (
          <span
            aria-hidden
            className="pointer-events-none absolute right-1 top-0.5 h-1.5 w-1.5 rounded-full bg-[var(--cf-accent)]"
          />
        )}
      </div>
    );
  }

  return (
    <button
      type="button"
      disabled={disabled}
      onClick={onSelect}
      aria-current={active ? "page" : undefined}
      // The count goes in the accessible name rather than only in the pill: a badge nobody can
      // hear is decoration.
      aria-label={badge === undefined ? undefined : t("nav.moduleWithBadge", { module: label, count: badge })}
      className="cf-focusable relative flex h-8 w-full items-center gap-2 rounded-md px-2 text-ui font-medium text-[var(--cf-text)] transition-colors hover:bg-black/[0.04] disabled:opacity-40 disabled:hover:bg-transparent dark:hover:bg-white/[0.06]"
    >
      {active && <ActivePill layoutId={PILL_ID} />}
      <span className="relative flex min-w-0 flex-1 items-center gap-2">
        <Icon size={14} className="shrink-0" aria-hidden />
        <span className="min-w-0 flex-1 truncate text-left">{label}</span>
        {badge !== undefined && (
          <span
            aria-hidden
            className="shrink-0 rounded-full bg-[var(--cf-accent-soft)] px-1.5 text-badge font-semibold tabular-nums text-[var(--cf-accent)]"
          >
            {badge}
          </span>
        )}
      </span>
    </button>
  );
}

/**
 * Where you can go, and nowhere else.
 *
 * v1's sidebar was a drawer holding workspaces, projects, branches, stashes and pull requests, and
 * the module switcher was a separate strip of tabs above the view. Phase 2 removed that strip on
 * the promise this would replace it, so until this file existed the app had no visible way to
 * change module at all — only `Mod+1..4` and the command bar. This is the promise being kept:
 * modules only, grouped under the scope they follow, with the drawer's old contents moved one
 * column right into `ContextPanel`.
 *
 * The scope headers spell out what a cryptic `ScopeMarker` icon used to hint at: a `repo` module
 * reloads when you pick a different project, a `workspace` one does not — which is why the API
 * client keeps its collections across a repo switch.
 */
export function NavigationSidebar() {
  const t = useT();
  const activeView = useUiStore((s) => s.activeView);
  const setActiveView = useUiStore((s) => s.setActiveView);
  const collapsed = useUiStore((s) => s.navRailCollapsed);
  const toggleNavRail = useUiStore((s) => s.toggleNavRail);
  const contextPanelOpen = useUiStore((s) => s.contextPanelOpen);
  const toggleContextPanel = useUiStore((s) => s.toggleContextPanel);
  const project = useWorkspaceStore((s) => s.activeProject());

  // Every hook the badges need runs here, unconditionally and in a fixed order, so the `.map()`
  // below stays hook-free and rules-of-hooks holds no matter how the registry grows.
  const uncommitted = useRepoStore((s) => uncommittedCount(s.status));
  // Only what is already loaded, and counted the same way Home's card counts it — open *and* draft.
  // Nothing is fetched for the badge: the PR lists arrive when the repo context panel mounts, and a
  // sidebar that triggered a round trip per project would be paying for a number.
  const openPrs = usePrStore((s) =>
    Object.values(s.prsByProject)
      .flat()
      .filter((pr) => pr.status === "open" || pr.status === "draft").length,
  );
  const badges = navBadges({ uncommittedChanges: uncommitted, openPrs });

  const groups = [
    { scope: "app" as const, modules: reachableInScope("app") },
    { scope: "repo" as const, modules: reachableInScope("repo") },
    { scope: "workspace" as const, modules: reachableInScope("workspace") },
  ];

  return (
    <nav
      aria-label={t("nav.title")}
      style={{ width: collapsed ? RAIL_WIDTH : EXPANDED_WIDTH }}
      className={`flex shrink-0 flex-col overflow-hidden ${CARD}`}
    >
      <div className="min-h-0 flex-1 overflow-y-auto p-2">
        {groups.map(({ scope, modules }) => (
          <div key={scope} className="mb-2 last:mb-0">
            {/* The rail drops the heading rather than truncating it: three letters of "WORKSPACE"
                names nothing, and the grouping still reads as a gap between two runs of icons. */}
            {!collapsed && SCOPE_LABELS[scope] && (
              <span className="mb-1 block px-2 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
                {t(SCOPE_LABELS[scope])}
              </span>
            )}
            <div className="space-y-0.5">
              {modules.map((module) => (
                <NavItem
                  key={module.id}
                  module={module}
                  active={activeView === module.id}
                  badge={badges[module.id]}
                  collapsed={collapsed}
                  // Disabled rather than hidden, for the same reason the clone and add buttons are:
                  // a list that changes length as you open a repo moves the other entries under the
                  // pointer, and `ActivePill` needs the group's buttons to stay mounted to animate.
                  disabled={scope === "repo" && !project}
                  onSelect={() => setActiveView(module.id)}
                />
              ))}
            </div>
          </div>
        ))}
      </div>

      {/* Both layout toggles live here, at the foot of the one column that never disappears. The
          context panel's own toggle *has* to be outside it — a button that hides a panel cannot sit
          in the panel it hides, or there is no way back. */}
      <div
        className={`flex shrink-0 items-center gap-0.5 border-t border-[var(--cf-border)] p-1.5 ${
          collapsed ? "flex-col" : "justify-end"
        }`}
      >
        <IconButton
          label="nav.contextPanel"
          icon={PanelRight}
          shortcut="panel.sidebar"
          active={contextPanelOpen}
          onClick={toggleContextPanel}
        />
        <IconButton
          label={collapsed ? "nav.expandRail" : "nav.collapseRail"}
          icon={collapsed ? PanelLeftOpen : PanelLeftClose}
          onClick={toggleNavRail}
        />
      </div>
    </nav>
  );
}
