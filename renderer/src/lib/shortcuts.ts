import { useUiStore } from "../state/uiStore";
import { useWorkspaceStore } from "../state/workspaceStore";
import { useTerminalStore } from "../state/terminalStore";
import { useNavigationStore } from "../state/navigationStore";
import { MODULE_ORDER } from "./modules";
import { fetchNow, pullNow, pushNow } from "./gitActions";
import type { Chord } from "./keys";
import type { TranslationKey } from "./i18n/translations";

export type ShortcutGroup = "general" | "panels" | "views" | "navigation" | "workspace" | "git";

export type ShortcutId =
  | "app.commandPalette"
  | "app.settings"
  | "app.shortcuts"
  | "panel.sidebar"
  | "panel.ai"
  | "panel.terminal"
  | "view.home"
  | "view.graph"
  | "view.changes"
  | "view.editor"
  | "view.api"
  | "view.next"
  | "view.prev"
  | "nav.back"
  | "nav.forward"
  | "project.switcher"
  | "project.next"
  | "project.prev"
  | "workspace.switcher"
  | "workspace.next"
  | "workspace.prev"
  | "branch.switcher"
  | "git.fetch"
  | "git.pull"
  | "git.push"
  | "pr.fromLink";

export interface ShortcutCommand {
  id: ShortcutId;
  group: ShortcutGroup;
  labelKey: TranslationKey;
  /** Written with `Mod`, so one default serves both platforms (⌘ on macOS, Ctrl elsewhere). */
  defaultChord: Chord;
  run: () => void;
}

export const SHORTCUT_GROUP_LABELS: Record<ShortcutGroup, TranslationKey> = {
  general: "shortcuts.groupGeneral",
  panels: "shortcuts.groupPanels",
  views: "shortcuts.groupViews",
  navigation: "shortcuts.navigation",
  workspace: "shortcuts.groupWorkspace",
  git: "shortcuts.groupGit",
};

// Registry order, every module in it — including the workspace-scoped ones, because cycling is about
// reaching every view from the keyboard and leaving one out makes the shortcut a trap for it. It
// used to be a literal here that had to be kept in step with the tab bar and `App.tsx` by hand.
function cycleView(delta: number): void {
  const { activeView, setActiveView } = useUiStore.getState();
  const index = MODULE_ORDER.indexOf(activeView);
  // MODULE_ORDER is non-empty, so the modulo index always lands in range.
  setActiveView(MODULE_ORDER[(index + delta + MODULE_ORDER.length) % MODULE_ORDER.length]!);
}

/** Replays a history entry — shared with the title bar's back/forward chevrons so both routes
 * apply an entry the same way. */
export function goHistory(direction: "back" | "forward"): void {
  const entry = useNavigationStore.getState()[direction]();
  if (!entry) return;
  useUiStore.getState().setActiveView(entry.view);
  if (entry.projectId) useWorkspaceStore.getState().setActiveProject(entry.projectId);
}

function cycleProject(delta: number): void {
  const { activeWorkspaceId, projectsByWorkspace, activeProjectId, setActiveProject } =
    useWorkspaceStore.getState();
  const projects = activeWorkspaceId ? projectsByWorkspace[activeWorkspaceId] ?? [] : [];
  if (projects.length < 2) return;
  const index = projects.findIndex((p) => p.id === activeProjectId);
  const next = index < 0 ? 0 : (index + delta + projects.length) % projects.length;
  // `next` is always a valid index: `projects.length >= 2` above and `next` is either 0 or a
  // value already reduced modulo `projects.length`.
  setActiveProject(projects[next]!.id);
}

function cycleWorkspace(delta: number): void {
  const { workspaces, activeWorkspaceId, setActiveWorkspace } = useWorkspaceStore.getState();
  if (workspaces.length < 2) return;
  const index = workspaces.findIndex((w) => w.id === activeWorkspaceId);
  const next = index < 0 ? 0 : (index + delta + workspaces.length) % workspaces.length;
  // `next` is always a valid index: `workspaces.length >= 2` above and `next` is either 0 or a
  // value already reduced modulo `workspaces.length`.
  setActiveWorkspace(workspaces[next]!.id);
}

/**
 * Every keyboard-reachable app action, with the default binding it ships with.
 *
 * The defaults deliberately avoid everything the embedded editor already claims — Monaco's own
 * bindings (⌘F, ⌘G, ⌘D, ⌘/, ⌘Z, ⇧⌘K, ⇧⌘L, ⌥↑/↓, ⌃⌥↑/↓, ⇧⌘←/→) and the app's editor-scoped ones
 * (⌘S, ⌘W, ⌘P, ⇧⌘F, ⌘I, ⌘PgUp/PgDn), listed in `EDITOR_RESERVED` below. Nothing here is a bare
 * letter with only ⌘/Ctrl either, so copy/paste/select-all stay untouched.
 */
export const SHORTCUT_COMMANDS: ShortcutCommand[] = [
  {
    id: "app.commandPalette",
    group: "general",
    labelKey: "shortcuts.cmdCommandPalette",
    defaultChord: "Mod+Shift+P",
    run: () => useUiStore.getState().toggleCommandPalette("all"),
  },
  {
    id: "app.settings",
    group: "general",
    labelKey: "shortcuts.cmdSettings",
    defaultChord: "Mod+,",
    run: () => useUiStore.getState().toggleSettings(),
  },
  {
    id: "app.shortcuts",
    group: "general",
    labelKey: "shortcuts.cmdShortcuts",
    defaultChord: "Mod+Alt+K",
    run: () => useUiStore.getState().toggleShortcutsModal(),
  },

  {
    id: "panel.sidebar",
    group: "panels",
    labelKey: "shortcuts.cmdSidebar",
    defaultChord: "Mod+B",
    // Same chord, same intent — hide the content-heavy column and get the width back — but it no
    // longer takes the module switcher with it, because that moved into its own column. The id
    // stays `panel.sidebar` so a user's rebind survives the split.
    run: () => useUiStore.getState().toggleContextPanel(),
  },
  {
    id: "panel.ai",
    group: "panels",
    labelKey: "shortcuts.cmdAiPanel",
    defaultChord: "Mod+Shift+A",
    run: () => useUiStore.getState().toggleAiPanel(),
  },
  {
    id: "panel.terminal",
    group: "panels",
    labelKey: "shortcuts.cmdTerminal",
    defaultChord: "Mod+J",
    run: () => useTerminalStore.getState().togglePanel(),
  },

  {
    id: "view.home",
    group: "views",
    labelKey: "shortcuts.cmdViewHome",
    // `Mod+0` rather than taking `Mod+1` and pushing the other four along. Home arrived after the
    // numbering did, and a redesign that silently renumbers the chords people already use is a
    // worse trade than one unusual key. Zero also reads as "before one", which is where Home sits.
    defaultChord: "Mod+0",
    run: () => useUiStore.getState().setActiveView("home"),
  },
  {
    id: "view.graph",
    group: "views",
    labelKey: "shortcuts.cmdViewGraph",
    defaultChord: "Mod+1",
    run: () => useUiStore.getState().setActiveView("graph"),
  },
  {
    id: "view.changes",
    group: "views",
    labelKey: "shortcuts.cmdViewChanges",
    defaultChord: "Mod+2",
    run: () => useUiStore.getState().setActiveView("changes"),
  },
  {
    id: "view.editor",
    group: "views",
    labelKey: "shortcuts.cmdViewEditor",
    defaultChord: "Mod+3",
    run: () => useUiStore.getState().setActiveView("editor"),
  },
  {
    id: "view.api",
    group: "views",
    labelKey: "shortcuts.cmdViewApi",
    defaultChord: "Mod+4",
    run: () => useUiStore.getState().setActiveView("api"),
  },
  {
    id: "view.next",
    group: "views",
    labelKey: "shortcuts.cmdViewNext",
    defaultChord: "Mod+Alt+ArrowRight",
    run: () => cycleView(1),
  },
  {
    id: "view.prev",
    group: "views",
    labelKey: "shortcuts.cmdViewPrev",
    defaultChord: "Mod+Alt+ArrowLeft",
    run: () => cycleView(-1),
  },

  {
    id: "nav.back",
    group: "navigation",
    labelKey: "titlebar.goBack",
    defaultChord: "Alt+ArrowLeft",
    run: () => goHistory("back"),
  },
  {
    id: "nav.forward",
    group: "navigation",
    labelKey: "titlebar.goForward",
    defaultChord: "Alt+ArrowRight",
    run: () => goHistory("forward"),
  },

  {
    id: "project.switcher",
    group: "workspace",
    labelKey: "shortcuts.cmdProjectSwitcher",
    defaultChord: "Mod+O",
    run: () => useUiStore.getState().toggleCommandPalette("projects"),
  },
  {
    id: "project.next",
    group: "workspace",
    labelKey: "shortcuts.cmdProjectNext",
    defaultChord: "Mod+Shift+PageDown",
    run: () => cycleProject(1),
  },
  {
    id: "project.prev",
    group: "workspace",
    labelKey: "shortcuts.cmdProjectPrev",
    defaultChord: "Mod+Shift+PageUp",
    run: () => cycleProject(-1),
  },
  {
    id: "workspace.switcher",
    group: "workspace",
    labelKey: "shortcuts.cmdWorkspaceSwitcher",
    defaultChord: "Mod+Shift+O",
    run: () => useUiStore.getState().toggleCommandPalette("workspaces"),
  },
  {
    id: "workspace.next",
    group: "workspace",
    labelKey: "shortcuts.cmdWorkspaceNext",
    defaultChord: "Mod+Alt+PageDown",
    run: () => cycleWorkspace(1),
  },
  {
    id: "workspace.prev",
    group: "workspace",
    labelKey: "shortcuts.cmdWorkspacePrev",
    defaultChord: "Mod+Alt+PageUp",
    run: () => cycleWorkspace(-1),
  },
  {
    id: "branch.switcher",
    group: "workspace",
    labelKey: "shortcuts.cmdBranchSwitcher",
    defaultChord: "Mod+Shift+B",
    // The binding is unchanged; what it opens is not. Branch picking was its own modal because it
    // checks out rather than merely navigating — but that is a property of the *action*, not a
    // reason for a second search field, and the palette already listed branches. It is the `#`
    // scope now, and this shortcut is the way in that does not need the prefix typed.
    run: () => useUiStore.getState().toggleCommandPalette("branches"),
  },

  {
    id: "git.fetch",
    group: "git",
    labelKey: "statusbar.fetch",
    defaultChord: "Mod+Shift+R",
    run: fetchNow,
  },
  {
    id: "git.pull",
    group: "git",
    labelKey: "statusbar.pull",
    defaultChord: "Mod+Shift+D",
    run: pullNow,
  },
  {
    id: "git.push",
    group: "git",
    labelKey: "statusbar.push",
    defaultChord: "Mod+Shift+U",
    run: pushNow,
  },
  {
    id: "pr.fromLink",
    group: "git",
    labelKey: "prLink.menuItem",
    defaultChord: "Mod+Shift+L",
    run: () => useUiStore.getState().togglePrLinkModal(),
  },
];

export const SHORTCUT_BY_ID = new Map(SHORTCUT_COMMANDS.map((c) => [c.id, c]));

/**
 * Chords the editor owns. They aren't configurable here — some belong to Monaco itself — but the
 * settings screen warns before a user assigns one of them to an app action, since the app action
 * would only ever fire outside the editor and feel broken inside it.
 */
export const EDITOR_RESERVED: { chord: Chord; labelKey: TranslationKey }[] = [
  { chord: "Mod+P", labelKey: "editor.goToFile" },
  { chord: "Mod+Shift+F", labelKey: "editor.searchInProject" },
  { chord: "Mod+F", labelKey: "shortcuts.findInFile" },
  { chord: "Mod+G", labelKey: "shortcuts.goToLine" },
  { chord: "Mod+S", labelKey: "editor.save" },
  { chord: "Mod+W", labelKey: "editor.closeTab" },
  { chord: "Mod+PageDown", labelKey: "shortcuts.nextTab" },
  { chord: "Mod+PageUp", labelKey: "shortcuts.prevTab" },
  { chord: "Mod+I", labelKey: "shortcuts.inlineEdit" },
  { chord: "Mod+/", labelKey: "shortcuts.toggleComment" },
  { chord: "Mod+D", labelKey: "shortcuts.selectNextOccurrence" },
  { chord: "Alt+ArrowUp", labelKey: "shortcuts.moveLine" },
  { chord: "Alt+ArrowDown", labelKey: "shortcuts.moveLine" },
  { chord: "Mod+Shift+K", labelKey: "shortcuts.deleteLine" },
  { chord: "Mod+Shift+M", labelKey: "anchors.title" },
  { chord: "Mod+Shift+C", labelKey: "codesnap.action" },
  { chord: "Mod+\\", labelKey: "editor.splitRight" },
  { chord: "Mod+Z", labelKey: "shortcuts.undo" },
];

export function reservedBy(chord: Chord): TranslationKey | null {
  return EDITOR_RESERVED.find((r) => r.chord === chord)?.labelKey ?? null;
}

/**
 * The reverse lookup: which chord opens the thing this label names.
 *
 * The editor's activity rail needs it. Its tooltips used to carry the chord as a hardcoded suffix
 * (`" (Ctrl+Shift+F)"`), which a rebind — or a change to this very list — would leave lying without
 * anything failing. Returns `null` for an action the editor does not reserve.
 */
export function reservedChordFor(labelKey: TranslationKey): Chord | null {
  return EDITOR_RESERVED.find((r) => r.labelKey === labelKey)?.chord ?? null;
}
