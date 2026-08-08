import { create } from "zustand";

/** The three things the AI panel can be showing. */
export type AiTab = "chat" | "analyze" | "pr";

interface AiPanelState {
  tab: AiTab;
  /** The specific analysis run currently shown, pinned by job id when opened from the Activity
   * list. `null` means "show the project's most recent analysis" (a fresh open or a new run).
   * Without this, every analyze activity resolved to the newest run and aliased onto one result. */
  selectedJobId: string | null;
  /** The user picked a tab. Only moves the tab — a pinned analysis stays pinned. */
  setTab: (tab: AiTab) => void;
  /** Show the latest analysis / a brand-new run (no specific past run pinned). */
  showAnalyze: () => void;
  /** Show a specific past analysis by its job id (from the Activity list). */
  showAnalyzeJob: (jobId: string) => void;
  showChat: () => void;
  showPr: () => void;
}

/**
 * Which section the AI panel is showing.
 *
 * This replaces `analyzeUiStore`, and the change is not only cosmetic. What the panel displayed used
 * to be *derived* from three independent pieces of state — `prStore.linkPr`, `prStore.selectedPr`
 * and an `analyzeOpen` boolean — resolved by a chain of ternaries. Nothing named the result, so
 * every action that wanted to show one thing had to remember to switch the others off:
 * `useAnalyzeUiStore.getState().hide()` appeared in eleven places across eight files, and the code
 * said why out loud — *"Clear whatever else the panel might currently be showing — otherwise the
 * chat switches underneath a still-visible PR review"*. Forgetting one was a silent bug.
 *
 * One field replaces that. Opening a PR or starting an analysis sets the tab, the user can move it
 * with the tabs, and nothing has to be switched off.
 *
 * The analysis jobs themselves live in `jobsStore` and keep running regardless of what is shown.
 */
export const useAiPanelStore = create<AiPanelState>((set) => ({
  tab: "chat",
  selectedJobId: null,
  setTab: (tab) => set({ tab }),
  showAnalyze: () => set({ tab: "analyze", selectedJobId: null }),
  showAnalyzeJob: (jobId) => set({ tab: "analyze", selectedJobId: jobId }),
  showChat: () => set({ tab: "chat", selectedJobId: null }),
  showPr: () => set({ tab: "pr" }),
}));
