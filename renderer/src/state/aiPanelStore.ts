import { create } from "zustand";

/** The three things the AI panel can be showing. */
export type AiTab = "chat" | "analyze" | "pr";

/**
 * Which code a review looks at. The wire spelling of the sidecar's `ReviewScope`.
 *
 * One of the two axes the review panel exposes; the other is `withTicket`. They used to be welded
 * together — one tab was always the working tree and never the ticket, the other always the whole
 * branch — so two of the four combinations did not exist. The one that was wanted and missing is
 * `branch` with no ticket: reviewing what you have before opening a pull request.
 */
export type ReviewScope = "working" | "branch";

/** What a caller wants the review controls set to before the panel appears. */
export interface ReviewIntent {
  scope?: ReviewScope;
  withTicket?: boolean;
}

interface AiPanelState {
  tab: AiTab;
  /** The specific analysis run currently shown, pinned by job id when opened from the Activity
   * list. `null` means "show the project's most recent analysis" (a fresh open or a new run).
   * Without this, every analyze activity resolved to the newest run and aliased onto one result. */
  selectedJobId: string | null;
  /**
   * Which diff the review reads, and whether the branch's work item is judged too.
   *
   * <b>Shared state rather than the section's own, because three places set it.</b> The panel's
   * controls, the "analyse" button in `ChangesPanel`, and the pre-commit ticket gate all open this
   * tab meaning different things — and a gate that offers to review against your ticket has to
   * arrive with the box ticked. `prStore.reviewLevel` is shared for exactly this reason.
   */
  scope: ReviewScope;
  withTicket: boolean;

  /** The user picked a tab. Only moves the tab — a pinned analysis stays pinned. */
  setTab: (tab: AiTab) => void;
  setScope: (scope: ReviewScope) => void;
  setWithTicket: (withTicket: boolean) => void;
  /**
   * Show the review panel, optionally saying which combination is meant.
   *
   * An omitted field is left as it was: the tab's own controls call this with nothing, so switching
   * back to a review does not silently undo what the user last chose. A caller that means a specific
   * combination says so, and both `ChangesPanel` entry points do.
   */
  showAnalyze: (intent?: ReviewIntent) => void;
  /** Show a specific past run by its job id (from the Activity list). */
  showAnalyzeJob: (jobId: string) => void;
  showChat: () => void;
  showPr: () => void;
}

/**
 * Which section the AI panel is showing, and what a review is about to look at.
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
  // The cheapest combination, and the only one that starts by itself: the working tree, judged on
  // its own. Anything else is a deliberate click.
  scope: "working",
  withTicket: false,
  setTab: (tab) => set({ tab }),
  setScope: (scope) => set({ scope }),
  setWithTicket: (withTicket) => set({ withTicket }),
  showAnalyze: (intent) =>
    set({
      tab: "analyze",
      selectedJobId: null,
      ...(intent?.scope === undefined ? {} : { scope: intent.scope }),
      ...(intent?.withTicket === undefined ? {} : { withTicket: intent.withTicket }),
    }),
  showAnalyzeJob: (jobId) => set({ tab: "analyze", selectedJobId: jobId }),
  showChat: () => set({ tab: "chat", selectedJobId: null }),
  showPr: () => set({ tab: "pr" }),
}));
