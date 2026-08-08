import { create } from "zustand";
import type { VcsProvider } from "../types/domain";
import type { ModuleId } from "../lib/modules";

/**
 * Which module is on screen.
 *
 * Was a closed union written out here; it is now derived from `lib/modules.ts`, so the registry is
 * the only place a view is declared. The alias keeps the name every consumer already imports —
 * "view" is what the store, the history stack and the shortcuts all call it.
 */
export type MainView = ModuleId;

export type SettingsSectionId =
  | "appearance"
  | "general"
  | "keybindings"
  | "projects"
  | "git"
  // Was `azure`, which stopped being true when GitHub joined it and would have been absurd once
  // Jira did. The section is every integration now, so the id says so.
  | "integrations"
  | "claude"
  | "review"
  | "sdd"
  | "skills"
  | "mcps"
  | "api";

/**
 * Which group the command bar lists.
 *
 * Scoped openings come from two places now. The keyboard shortcuts still open a scope directly —
 * "switch repository" wants a list of repositories, not everything the app can do — and the command
 * bar derives one from a prefix typed into the field (`lib/ui/commandScope.ts`), which is what lets
 * one input stand in for the three separate pickers this app used to have.
 */
export type PaletteScope = "all" | "workspaces" | "projects" | "branches" | "files" | "commands";

interface UiState {
  /**
   * Whether the navigation sidebar is a 48px icon rail rather than a labelled list.
   *
   * Collapsed is narrower, never absent: navigation is the one surface that must not be able to
   * disappear, or the only way left to reach a module is a shortcut you have to already know. What
   * v1 called `sidebarCollapsed` unmounted the whole aside, because that aside was content rather
   * than navigation — that content is `contextPanelOpen` now.
   */
  navRailCollapsed: boolean;
  /** Whether the second column — the active module's context — is shown at all. This is what
   * `Mod+B` toggles, and the reason it is a separate flag: hiding the projects/branches/PRs pane to
   * gain width is the thing people actually want, and it used to take the navigation with it. */
  contextPanelOpen: boolean;
  activeView: MainView;
  /** Settings is a modal overlaid on top of the current view, not a view itself — closing
   * it just reveals whatever was already showing underneath. */
  settingsOpen: boolean;
  settingsSection: SettingsSectionId;
  /**
   * Which provider row the Integrations section should open expanded, or `null` for none.
   *
   * Nullable because "no provider in particular" is a real state and used to be spelled `"azure"`:
   * every plain visit to Integrations expanded the Azure form, which pushed the other three
   * providers off screen and turned a list-with-status into a single form. Only a deep-link — "you
   * need a GitHub token" — names a provider, and only then should a row open by itself.
   */
  settingsHostingProvider: VcsProvider | null;
  /** Repo-relative path the Editor tab should jump to open next; consumed once then cleared. */
  pendingEditorPath: string | null;
  /** 1-based line to reveal in that file — set when the jump came from a search hit, so the
   * editor lands on the match instead of at the top of the file. */
  pendingEditorLine: number | null;
  /** The AI panel (PRs / open questions / change analysis) is a persistent left-docked panel,
   * not a tab — it stays mounted and scoped to whatever project is active regardless of which
   * main view or project the user switches to. */
  aiPanelOpen: boolean;
  /** The command palette and the shortcuts cheat sheet live here rather than as local state in
   * the title bar / editor, because keyboard shortcuts have to reach them from anywhere. */
  commandPaletteOpen: boolean;
  commandPaletteScope: PaletteScope;
  /** What the field starts with. The command bar in the header is a real input: whatever was typed
   * there has to survive the hand-off to the overlay, or the first keystroke is eaten. */
  commandPaletteQuery: string;
  shortcutsModalOpen: boolean;
  /** "Review a PR from its link" — reachable from the title bar, the command palette, the
   * sidebar and a shortcut, none of which own the modal, so it lives here and is rendered once
   * at the app root. */
  prLinkModalOpen: boolean;
  toggleNavRail: () => void;
  toggleContextPanel: () => void;
  setActiveView: (view: MainView) => void;
  openSettings: (section: SettingsSectionId, hostingProvider?: VcsProvider) => void;
  toggleSettings: () => void;
  closeSettings: () => void;
  openInEditor: (relPath: string, line?: number) => void;
  clearPendingEditorPath: () => void;
  toggleAiPanel: () => void;
  openAiPanel: () => void;
  openCommandPalette: (scope?: PaletteScope, query?: string) => void;
  /** Re-pressing the same shortcut closes the palette; a *different* scope re-scopes the open
   * one instead, so ⌘O → ⌘⇧O doesn't require closing it in between. */
  toggleCommandPalette: (scope?: PaletteScope) => void;
  closeCommandPalette: () => void;
  toggleShortcutsModal: () => void;
  closeShortcutsModal: () => void;
  openPrLinkModal: () => void;
  togglePrLinkModal: () => void;
  closePrLinkModal: () => void;
}

export const useUiStore = create<UiState>((set) => ({
  navRailCollapsed: false,
  contextPanelOpen: true,
  // Home rather than the commit graph: the graph answers "what happened in this repository", which
  // is a question you only have once you have chosen one.
  activeView: "home",
  settingsOpen: false,
  settingsSection: "appearance",
  settingsHostingProvider: null,
  pendingEditorPath: null,
  pendingEditorLine: null,
  aiPanelOpen: false,
  commandPaletteOpen: false,
  commandPaletteScope: "all",
  commandPaletteQuery: "",
  shortcutsModalOpen: false,
  prLinkModalOpen: false,
  toggleNavRail: () => set((s) => ({ navRailCollapsed: !s.navRailCollapsed })),
  toggleContextPanel: () => set((s) => ({ contextPanelOpen: !s.contextPanelOpen })),
  setActiveView: (view) => set({ activeView: view, settingsOpen: false }),
  // Cleared rather than carried over when no provider is named: keeping the last one meant that
  // after one "you need a GitHub token" hint, every later visit to Integrations kept expanding
  // GitHub for no reason the user could see.
  openSettings: (section, hostingProvider) =>
    set({
      settingsOpen: true,
      settingsSection: section,
      settingsHostingProvider: hostingProvider ?? null,
    }),
  toggleSettings: () => set((s) => ({ settingsOpen: !s.settingsOpen })),
  closeSettings: () => set({ settingsOpen: false }),
  openInEditor: (relPath, line) =>
    set({
      activeView: "editor",
      pendingEditorPath: relPath,
      pendingEditorLine: line ?? null,
      settingsOpen: false,
    }),
  clearPendingEditorPath: () => set({ pendingEditorPath: null, pendingEditorLine: null }),
  toggleAiPanel: () => set((s) => ({ aiPanelOpen: !s.aiPanelOpen })),
  openAiPanel: () => set({ aiPanelOpen: true }),
  openCommandPalette: (scope = "all", query = "") =>
    set({ commandPaletteOpen: true, commandPaletteScope: scope, commandPaletteQuery: query }),
  toggleCommandPalette: (scope = "all") =>
    set((s) => ({
      commandPaletteOpen: !(s.commandPaletteOpen && s.commandPaletteScope === scope),
      commandPaletteScope: scope,
      commandPaletteQuery: "",
    })),
  closeCommandPalette: () => set({ commandPaletteOpen: false, commandPaletteQuery: "" }),
  toggleShortcutsModal: () => set((s) => ({ shortcutsModalOpen: !s.shortcutsModalOpen })),
  closeShortcutsModal: () => set({ shortcutsModalOpen: false }),
  openPrLinkModal: () => set({ prLinkModalOpen: true }),
  togglePrLinkModal: () => set((s) => ({ prLinkModalOpen: !s.prLinkModalOpen })),
  closePrLinkModal: () => set({ prLinkModalOpen: false }),
}));
