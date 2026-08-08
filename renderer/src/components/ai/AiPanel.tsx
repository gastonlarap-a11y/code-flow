import { useMemo, useState } from "react";
import { motion } from "framer-motion";
import { GitPullRequest, MessageSquare, Plus, RotateCcw, ShieldCheck, Sparkles, X } from "lucide-react";
import { targetKey, type PrTarget } from "../../lib/prTarget";
import { useUiStore } from "../../state/uiStore";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { useLayoutStore } from "../../state/layoutStore";
import { usePrStore } from "../../state/prStore";
import { useChatStore } from "../../state/chatStore";
import { useAiPanelStore, type AiTab } from "../../state/aiPanelStore";
import { useT } from "../../state/languageStore";
import { ResizeHandle } from "../common/ResizeHandle";
import { CARD } from "../common/panelChrome";
import { EmptyState } from "../common/EmptyState";
import { Tabs, tabPanelProps, type TabOption } from "../common/Tabs";
import { PanelHeader } from "../common/PanelHeader";
import { IconButton } from "../common/IconButton";
import { Button } from "../common/Button";
import { CheckpointsModal } from "./CheckpointsModal";
import { AnalyzeSection } from "./AnalyzeSection";
import { LinkSessionsSection } from "./LinkSessionsSection";
import { ActivitySection } from "./ActivityPanel";
import { PrReviewSection } from "./PrReviewPanel";
import { ChatSection } from "./ChatPanel";

/** Unique per rendered strip, and the prefix for the tab/panel ids that pair them up. */
const TABS_ID = "cf-ai-panel";

const PANEL_MIN = 280;
const PANEL_MAX = 520;

/** Rendered by App.tsx inside an `AnimatePresence` so mount/unmount slides the panel in/out
 * instead of popping — width is what's animated, so the resize handle's own drag updates
 * (which set inline width directly) aren't fighting a CSS transition mid-drag. */
export function AiPanel() {
  const t = useT();
  const project = useWorkspaceStore((s) => s.activeProject());
  const selectedPr = usePrStore((s) => s.selectedPr);
  const linkPr = usePrStore((s) => s.linkPr);
  const linkTarget = useMemo<PrTarget | null>(
    () => (linkPr ? { kind: "link", url: linkPr.url, workspaceId: linkPr.workspaceId } : null),
    [linkPr],
  );
  const tab = useAiPanelStore((s) => s.tab);
  const setTab = useAiPanelStore((s) => s.setTab);
  const toggle = useUiStore((s) => s.toggleAiPanel);

  // The PR tab is disabled rather than hidden when there is nothing to review: a strip that changes
  // length as you work moves the other tabs under the pointer.
  const tabs = useMemo<TabOption<AiTab>[]>(
    () => [
      { id: "chat", labelKey: "chat.title", icon: MessageSquare },
      { id: "analyze", labelKey: "analyze.title", icon: ShieldCheck },
      { id: "pr", labelKey: "ai.prReview", icon: GitPullRequest, disabled: !selectedPr },
    ],
    [selectedPr],
  );
  const width = useLayoutStore((s) => s.sizes.aiPanelWidth);
  const setSize = useLayoutStore((s) => s.setSize);
  const commitSize = useLayoutStore((s) => s.commitSize);

  // "New chat" from the panel header works from *any* view: reviewing a PR or looking at an
  // analysis, one click drops those and lands on a fresh, empty free-form conversation — so the
  // user isn't stuck hunting for the little × on the PR card (which only closed the PR, it didn't
  // start a new chat) to get back to open-ended chat.
  //
  // None of it is destructive: a selected PR is still in the sidebar, and a link session is
  // parked in `linkPrHistory` for the list above Activity to bring back.
  const startNewChat = () => {
    if (!project) return;
    usePrStore.getState().selectPr(null);
    usePrStore.getState().closeLinkPr();
    useChatStore.getState().clear(project.id);
  };

  const [checkpointsOpen, setCheckpointsOpen] = useState(false);

  return (
    <motion.div
      initial={{ width: 0, opacity: 0 }}
      animate={{ width, opacity: 1 }}
      exit={{ width: 0, opacity: 0 }}
      transition={{ duration: 0.18, ease: "easeOut" }}
      className="flex shrink-0 overflow-hidden"
    >
      <ResizeHandle
        axis="x"
        value={width}
        min={PANEL_MIN}
        max={PANEL_MAX}
        invert
        onChange={(w) => setSize("aiPanelWidth", w)}
        onCommit={(w) => commitSize("aiPanelWidth", w)}
      />
      <aside
        style={{ width }}
        className={`flex shrink-0 flex-col overflow-hidden ${CARD}`}
      >
        <PanelHeader
          title="ai.panelTitle"
          icon={Sparkles}
          actions={
            <>
              {project && (
                <IconButton
                  label="checkpoints.title"
                  icon={RotateCcw}
                  onClick={() => setCheckpointsOpen(true)}
                />
              )}
              {project && (
                <Button variant="ghost" size="sm" icon={Plus} onClick={startNewChat}>
                  {t("chatHistory.newChat")}
                </Button>
              )}
              <IconButton label="ai.closePanel" icon={X} shortcut="panel.ai" onClick={toggle} />
            </>
          }
        />
        {/* A PR opened from a link outranks everything and works without a project — that's the
            whole point: it belongs to a repository this machine may not have. There is nothing to
            switch between in that state, so it keeps the panel to itself. */}
        {linkPr ? (
          <>
            <ActivitySection projectId={targetKey(linkTarget!)} />
            <div className="min-h-0 flex-1">
              <PrReviewSection target={linkTarget!} pr={linkPr.pr} />
            </div>
          </>
        ) : !project ? (
          // Still offer the way back into a parked link review — with no project open this used
          // to be a dead end, which is exactly where "New chat" landed the user.
          <>
            <LinkSessionsSection />
            <EmptyState icon={Sparkles} title={t("ai.noProject")} />
          </>
        ) : (
          <>
            <LinkSessionsSection />
            <ActivitySection projectId={project.id} />
            <Tabs
              options={tabs}
              activeId={tab}
              onSelect={setTab}
              layoutId={TABS_ID}
              label={t("ai.sections")}
              // Manual: the Analyze tab starts a Claude run when it mounts, so an arrow key must
              // move focus without committing to it. See `Tabs`.
              activation="manual"
              className="shrink-0 border-b border-[var(--cf-border)] px-2 py-1"
            />
            <div {...tabPanelProps(TABS_ID, tab)} className="min-h-0 flex-1">
              {tab === "pr" && selectedPr ? (
                <PrReviewSection target={{ kind: "project", projectId: project.id }} pr={selectedPr} />
              ) : tab === "analyze" ? (
                <AnalyzeSection projectId={project.id} />
              ) : (
                <ChatSection projectId={project.id} />
              )}
            </div>
          </>
        )}
      </aside>
      {checkpointsOpen && project && (
        <CheckpointsModal repoPath={project.local_path} onClose={() => setCheckpointsOpen(false)} />
      )}
    </motion.div>
  );
}
