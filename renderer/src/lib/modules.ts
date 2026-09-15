import {
  Code2,
  Database,
  GitBranch,
  History,
  Home,
  Send,
  SquareKanban,
  type LucideIcon,
} from "lucide-react";
import type { TranslationKey } from "./i18n/translations";

/**
 * Every place the app can be, declared once.
 *
 * This replaces a closed union in `uiStore` plus four hand-maintained lists that had to agree with
 * it: `PROJECT_VIEWS`/`WORKSPACE_VIEWS` in `App.tsx`, `REPO_TABS`/`WORKSPACE_TABS` in the tab bar,
 * `VIEW_ORDER` in `shortcuts.ts`, and `VIEW_ITEMS` in the command palette. Adding a view meant
 * finding all five; missing one produced a view you could reach by shortcut but not see, or see but
 * not cycle to. Now the union is *derived* from this array, so a new entry is a new member of
 * `ModuleId` and every consumer either picks it up or stops compiling.
 *
 * Metadata only — no components. `App.tsx` keeps the id → element map because what it holds is a
 * deliberate code-splitting decision (Graph eager because it is the landing view, Monaco and the API
 * client lazy because they are not), and that decision does not belong in a table of labels. It also
 * keeps this file importable from `uiStore` and `shortcuts.ts` without dragging React through them.
 */
export interface AppModule {
  id: string;
  icon: LucideIcon;
  labelKey: TranslationKey;
  /**
   * What the module follows. `repo` modules reload when a different project is selected; `workspace`
   * ones do not, which is why the API client's collections survive a repo switch. `app` follows
   * neither and is always reachable — Home is the landing page, so it has to render before there is
   * a workspace, let alone a repository. The distinction used to be an icon with a tooltip
   * (`ScopeMarker`); the navigation sidebar spells it out.
   *
   * **The headings it spells out do not reuse these names.** `workspace` reads "Tools" on screen,
   * because "workspace" is also the thing called Flow or achsdev — the box holding your repos,
   * collections and environments — and a heading that says "Workspace" over the API client claims
   * the two are related. They are not: this field is about what a module *follows*, and the
   * heading is about what it *is*.
   */
  scope: "repo" | "workspace" | "app";
  /**
   * Registered, listed, and not built yet.
   *
   * §7 of the redesign proposal asks for the work-items module to exist in the registry before the
   * feature does, so that shipping it is one entry here rather than a hunt through five files, and
   * so the `Record<ModuleId, …>` maps in `App.tsx` and `ContextPanel.tsx` already have a slot
   * waiting for it.
   *
   * It is registered and **not offered**: not in the navigation, not in the command bar, not in the
   * cycling order. A row that is permanently dead is not a promise of what is coming, it is a
   * control that looks broken — and in Spanish "Elementos de trabajo" plus a "coming soon" tag does
   * not even fit the sidebar's width. The registry is the foundation; the navigation is for things
   * you can actually open.
   */
  comingSoon?: boolean;
  /**
   * Needs the selected project to be a git repository.
   *
   * A project is any folder the user picked — `create_project` validates nothing — so a plain
   * directory is a supported thing to open, and `graph` and `changes` have nothing to show in one.
   * They are *hidden* there rather than disabled: a disabled control says "this could work",
   * and for a folder with no history it never will. `editor` and `dbml` are deliberately unmarked;
   * they read files, not history.
   */
  requiresGit?: boolean;
}

export const APP_MODULES = [
  { id: "home", icon: Home, labelKey: "home.title", scope: "app" },
  { id: "graph", icon: History, labelKey: "tabbar.graph", scope: "repo", requiresGit: true },
  { id: "changes", icon: GitBranch, labelKey: "tabbar.changes", scope: "repo", requiresGit: true },
  { id: "editor", icon: Code2, labelKey: "tabbar.editor", scope: "repo" },
  // No `requiresGit`: a schema document is a file in the folder, and reading one needs no history.
  { id: "dbml", icon: Database, labelKey: "tabbar.dbml", scope: "repo" },
  { id: "workitems", icon: SquareKanban, labelKey: "tabbar.workitems", scope: "repo" },
  { id: "api", icon: Send, labelKey: "tabbar.api", scope: "workspace" },
] as const satisfies readonly AppModule[];

/**
 * One entry of the registry, with its literal id intact.
 *
 * `AppModule` is the shape an entry must satisfy; this is what an entry actually *is*. Consumers
 * want the second one — indexing a `Record<ModuleId, …>` with an `AppModule["id"]` is indexing it
 * with `string`, which does not compile, and that is the check worth keeping.
 */
export type RegisteredModule = (typeof APP_MODULES)[number];

/** The id of any registered module. Derived, so it cannot drift from the array above. */
export type ModuleId = RegisteredModule["id"];

const BY_ID = new Map<string, RegisteredModule>(APP_MODULES.map((m) => [m.id, m]));

export function moduleById(id: ModuleId): RegisteredModule {
  // Every `ModuleId` comes from `APP_MODULES`, so the lookup cannot miss.
  return BY_ID.get(id)!;
}

/**
 * Whether a string names a registered module.
 *
 * The persisted-navigation path needs this: a stored id from an older version can name a module that
 * no longer exists, and landing on it would leave the app showing nothing.
 */
export function isModuleId(id: string): id is ModuleId {
  return BY_ID.has(id);
}

export function modulesInScope(scope: AppModule["scope"]): readonly RegisteredModule[] {
  return APP_MODULES.filter((m) => m.scope === scope);
}

/** What the navigation lists for a scope: the modules in it that exist. */
export function reachableInScope(scope: AppModule["scope"]): readonly RegisteredModule[] {
  return REACHABLE_MODULES.filter((m) => m.scope === scope);
}

/** The modules you can actually get to. Registered-but-unbuilt ones are listed in the navigation
 * and nowhere that navigates. */
export const REACHABLE_MODULES: readonly RegisteredModule[] = APP_MODULES.filter(
  // `in` rather than a plain read: `as const` gives the entries that omit the flag no such property
  // at all, so the field only exists on the ones that set it.
  (m) => !("comingSoon" in m && m.comingSoon),
);

/** Cycling order for the next/previous-view shortcuts: registry order, minus what is not built. */
export const MODULE_ORDER: readonly ModuleId[] = REACHABLE_MODULES.map((m) => m.id);

/**
 * The modules a project in this state can actually open.
 *
 * `isGitRepo === null` means "not resolved yet" and keeps everything listed: the answer arrives one
 * IPC round-trip after the project is selected, and hiding two entries for that moment reads as the
 * navigation flickering.
 */
export function availableModules(isGitRepo: boolean | null): readonly RegisteredModule[] {
  if (isGitRepo !== false) return REACHABLE_MODULES;
  // `in` rather than a plain read, for the same reason as `REACHABLE_MODULES`: `as const` leaves
  // the property off the entries that omit it.
  return REACHABLE_MODULES.filter((m) => !("requiresGit" in m && m.requiresGit));
}

/** What the navigation lists for one scope, given what the selected project turned out to be. */
export function availableInScope(
  scope: AppModule["scope"],
  isGitRepo: boolean | null,
): readonly RegisteredModule[] {
  return availableModules(isGitRepo).filter((m) => m.scope === scope);
}
