import { useState } from "react";
import { ChevronLeft, ChevronRight, Link2, MessageCircle, Settings, Sparkles, TerminalSquare, Zap } from "lucide-react";
import { usePlatform } from "../../lib/platform";
import { IconButton } from "../common/IconButton";
import { Tooltip } from "../common/Tooltip";
import { CommandBar } from "../common/CommandBar";
import { MacControlsSpacer, WindowsControls } from "./WindowControls";
import { HeaderContext } from "./HeaderContext";
import { HeaderGitActions } from "./HeaderGitActions";
import { useUiStore } from "../../state/uiStore";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { useNavigationStore } from "../../state/navigationStore";
import { useTerminalStore } from "../../state/terminalStore";
import { usePrStore } from "../../state/prStore";
import { useT } from "../../state/languageStore";
import { goHistory } from "../../lib/shortcuts";

/**
 * The one bar across the top, replacing three.
 *
 * v1 stacked a title bar (44px), a tab bar (40px) and a status bar (32px): 116px of chrome telling
 * you the same three things — which workspace, which project, which branch — in three places, with
 * the view switcher in the middle one and the panel toggles in the bottom one. This is the single
 * band that carries all of it, and the view switcher moves out entirely: the navigation sidebar owns
 * that now, which is what lets a module list grow past four entries.
 *
 * Three zones, in the order you reach for them: where you are, what you want, what you can do.
 *
 * Window controls stay at the edges the OS puts them: a spacer on the left under macOS's traffic
 * lights, real caption buttons on the far right under Windows.
 */
function AiActionsMenu({ onClose }: { onClose: () => void }) {
  const t = useT();
  const openAiPanel = useUiStore((s) => s.openAiPanel);
  const openPrLinkModal = useUiStore((s) => s.openPrLinkModal);
  const project = useWorkspaceStore((s) => s.activeProject());
  const selectedPr = usePrStore((s) => s.selectedPr);
  const reviewPr = usePrStore((s) => s.reviewPr);

  const openChat = () => {
    openAiPanel();
    onClose();
  };

  const reviewFromLink = () => {
    openPrLinkModal();
    onClose();
  };

  // Same rule as the PR panel: a merged or closed pull request is settled and takes no more
  // actions. Without this the menu would be a way around the panel's own lock.
  const prSettled = selectedPr?.status === "merged" || selectedPr?.status === "closed";

  const reviewCurrentPr = () => {
    if (!project || !selectedPr || prSettled) return;
    openAiPanel();
    reviewPr({ kind: "project", projectId: project.id }, selectedPr.id);
    onClose();
  };

  return (
    <>
      <div className="fixed inset-0 z-10" onClick={onClose} />
      <div className="absolute right-0 top-full z-20 mt-1 w-60 rounded-lg border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-1 shadow-[var(--cf-shadow)]">
        <button
          onClick={openChat}
          className="flex w-full items-center gap-2 rounded-md px-2.5 py-1.5 text-left text-body text-[var(--cf-text)] hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
        >
          <MessageCircle size={14} aria-hidden />
          {t("titlebar.openChat")}
        </button>
        {/* The reason this entry is dead has to reach the user, and a disabled button fires no
            pointer events — so the tooltip anchors on the wrapper, not on the button. */}
        <Tooltip label={prSettled ? t("pr.stateLockedHint") : ""}>
          <span className="block">
            <button
              onClick={reviewCurrentPr}
              disabled={!selectedPr || prSettled}
              className="flex w-full items-center gap-2 rounded-md px-2.5 py-1.5 text-left text-body text-[var(--cf-text)] hover:bg-black/[0.03] disabled:opacity-40 disabled:hover:bg-transparent dark:hover:bg-white/[0.04]"
            >
              <Sparkles size={14} aria-hidden />
              <span className="min-w-0 flex-1 truncate">
                {selectedPr
                  ? t("titlebar.reviewCurrentPr", { title: selectedPr.title })
                  : t("titlebar.noPrSelected")}
              </span>
            </button>
          </span>
        </Tooltip>
        {/* Works with no project open and no PR selected — that's the point: the link is the
            only input needed. */}
        <button
          onClick={reviewFromLink}
          className="flex w-full items-center gap-2 rounded-md px-2.5 py-1.5 text-left text-body text-[var(--cf-text)] hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
        >
          <Link2 size={14} aria-hidden />
          {t("prLink.menuItem")}
        </button>
      </div>
    </>
  );
}

export function CommandHeader() {
  const platform = usePlatform();
  const isMac = platform === "macos";
  const canGoBack = useNavigationStore((s) => s.canGoBack);
  const canGoForward = useNavigationStore((s) => s.canGoForward);
  const settingsOpen = useUiStore((s) => s.settingsOpen);
  const toggleSettings = useUiStore((s) => s.toggleSettings);
  const aiPanelOpen = useUiStore((s) => s.aiPanelOpen);
  const toggleAiPanel = useUiStore((s) => s.toggleAiPanel);
  const terminalPanelOpen = useTerminalStore((s) => s.panelOpen);
  const toggleTerminalPanel = useTerminalStore((s) => s.togglePanel);
  const t = useT();
  const [showAiMenu, setShowAiMenu] = useState(false);

  return (
    <header
      data-drag-region
      className="relative flex h-12 shrink-0 items-center gap-2 px-2"
      style={{ background: "var(--cf-titlebar-gradient)" }}
    >
      {isMac ? <MacControlsSpacer /> : <div className="w-1" />}

      {/* Back/forward walk the view-and-project history, which nothing else in the app exposes —
          they earn their place. What they had not earned is *that* place: sitting flush against
          macOS's traffic lights they read as two more window buttons. The rule holds on Windows
          too, where the caption buttons are on the other side: these belong with the context they
          navigate, so the divider marks where the window's chrome ends and the app's begins. */}
      {isMac && <span className="h-4 w-px shrink-0 bg-[var(--cf-border)]" aria-hidden />}
      <div className="flex shrink-0 items-center gap-0.5">
        <IconButton
          label="titlebar.goBack"
          icon={ChevronLeft}
          shortcut="nav.back"
          disabled={!canGoBack}
          onClick={() => goHistory("back")}
        />
        <IconButton
          label="titlebar.goForward"
          icon={ChevronRight}
          shortcut="nav.forward"
          disabled={!canGoForward}
          onClick={() => goHistory("forward")}
        />
      </div>

      <HeaderContext />

      <CommandBar />

      <div className="flex shrink-0 items-center gap-1">
        <HeaderGitActions />
        <span className="h-4 w-px shrink-0 bg-[var(--cf-border)]" aria-hidden />
        <div className="relative">
          <button
            onClick={() => setShowAiMenu((v) => !v)}
            className="cf-focusable flex h-7 items-center gap-1.5 rounded-control px-2 text-ui font-medium text-[var(--cf-text)] transition-colors hover:bg-black/[0.04] dark:hover:bg-white/[0.06]"
          >
            <Zap size={14} aria-hidden />
            {t("titlebar.aiActions")}
          </button>
          {showAiMenu && <AiActionsMenu onClose={() => setShowAiMenu(false)} />}
        </div>
        <IconButton
          label="terminal.toggle"
          icon={TerminalSquare}
          shortcut="panel.terminal"
          active={terminalPanelOpen}
          onClick={toggleTerminalPanel}
        />
        <IconButton
          label="statusbar.aiPanel"
          icon={Sparkles}
          shortcut="panel.ai"
          active={aiPanelOpen}
          onClick={toggleAiPanel}
        />
        {/* The single door to settings. There were seven scattered `openSettings` call sites and
            they are still there — a "needs a token" hint deep-linking into the right form is worth
            keeping — but this is the one that is always visible, so there is one place to look. */}
        <IconButton
          label="statusbar.settings"
          icon={Settings}
          shortcut="app.settings"
          active={settingsOpen}
          onClick={toggleSettings}
        />
        {!isMac && <WindowsControls />}
      </div>
    </header>
  );
}
