import { beforeEach, describe, expect, test } from "vitest";
import { useAiPanelStore } from "./aiPanelStore";

const initial = useAiPanelStore.getState();

beforeEach(() => {
  useAiPanelStore.setState(initial, true);
});

describe("the review controls", () => {
  test("start on the cheapest combination, which is the only one that runs by itself", () => {
    const state = useAiPanelStore.getState();

    expect(state.scope).toBe("working");
    expect(state.withTicket).toBe(false);
  });

  test("a caller that means a combination says so, and gets it", () => {
    // The pre-commit gate. It offered to review against the ticket, so arriving with the box
    // unticked would be the dialog contradicting itself — and judging only the uncommitted half
    // against acceptance criteria reports met criteria as unmet.
    useAiPanelStore.getState().showAnalyze({ scope: "branch", withTicket: true });

    const state = useAiPanelStore.getState();
    expect(state.tab).toBe("analyze");
    expect(state.scope).toBe("branch");
    expect(state.withTicket).toBe(true);
  });

  test("the changes-panel button puts them back on the uncommitted diff", () => {
    useAiPanelStore.setState({ scope: "branch", withTicket: true });

    useAiPanelStore.getState().showAnalyze({ scope: "working", withTicket: false });

    expect(useAiPanelStore.getState().scope).toBe("working");
    expect(useAiPanelStore.getState().withTicket).toBe(false);
  });

  test("switching to the tab with no intent leaves the user's own choice alone", () => {
    // The tab strip calls this with nothing. Resetting here would silently undo what was last
    // picked every time the panel was revisited.
    useAiPanelStore.setState({ scope: "branch", withTicket: true });

    useAiPanelStore.getState().showAnalyze();

    expect(useAiPanelStore.getState().scope).toBe("branch");
    expect(useAiPanelStore.getState().withTicket).toBe(true);
  });

  test("an intent that names only one axis leaves the other alone", () => {
    useAiPanelStore.setState({ scope: "branch", withTicket: true });

    useAiPanelStore.getState().showAnalyze({ withTicket: false });

    expect(useAiPanelStore.getState().scope).toBe("branch");
    expect(useAiPanelStore.getState().withTicket).toBe(false);
  });

  test("showing a past run pins it without disturbing the controls", () => {
    // A stored answer is read, not re-run: the controls describe the *next* review, and moving them
    // to match an old one would misreport what is about to happen.
    useAiPanelStore.setState({ scope: "branch", withTicket: true });

    useAiPanelStore.getState().showAnalyzeJob("job-7");

    const state = useAiPanelStore.getState();
    expect(state.selectedJobId).toBe("job-7");
    expect(state.scope).toBe("branch");
    expect(state.withTicket).toBe(true);
  });

  test("a fresh review clears any pinned run", () => {
    useAiPanelStore.setState({ selectedJobId: "job-7" });

    useAiPanelStore.getState().showAnalyze({ scope: "working", withTicket: false });

    expect(useAiPanelStore.getState().selectedJobId).toBeNull();
  });
});
