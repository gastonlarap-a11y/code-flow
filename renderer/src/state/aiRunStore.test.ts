import { beforeEach, describe, expect, test, vi } from "vitest";

/**
 * The renderer half of `RUN_CANCELLED::` (`XLANG-003`), plus the buffer the run log reads from.
 *
 * The marker is produced by the sidecar and is the only thing separating "the user pressed stop"
 * from "the agent crashed". Miss it and a deliberate cancellation surfaces as a red error toast for
 * something the user did on purpose; the two look identical on the wire.
 *
 * The buffer matters for a different reason: `linesByRun` is read through a zustand selector, so an
 * unstable empty value re-renders the log panel on every unrelated store write.
 */

vi.mock("../lib/ipc/commands", () => ({ cancelAiRun: vi.fn(() => Promise.resolve(true)) }));
vi.mock("../lib/ipc/events", () => ({ onAiOutput: vi.fn(() => Promise.resolve(() => {})) }));

const api = vi.mocked(await import("../lib/ipc/commands"));
const { useAiRunStore, isCancellation, newRunId, CANCELLED_MARKER } = await import("./aiRunStore");

const initial = useAiRunStore.getState();

beforeEach(() => {
  vi.clearAllMocks();
  useAiRunStore.setState(initial, true);
});

describe("telling a cancellation from a failure", () => {
  test("recognises the marker the sidecar produces", () => {
    expect(isCancellation(`${CANCELLED_MARKER} run-7`)).toBe(true);
  });

  // `includes`, so whatever the transport wraps the error in does not matter.
  test("finds it wherever the transport put it", () => {
    expect(isCancellation(new Error(`command failed: ${CANCELLED_MARKER}`))).toBe(true);
  });

  test("a genuine failure is not a cancellation", () => {
    expect(isCancellation(new Error("claude: command not found"))).toBe(false);
  });

  // The wording alone is not the contract; the marker is.
  test("a message that merely says cancelled is not one", () => {
    expect(isCancellation("the run was cancelled")).toBe(false);
  });

  test("survives a non-string error", () => {
    expect(isCancellation(null)).toBe(false);
    expect(isCancellation({ code: 130 })).toBe(false);
  });
});

describe("a run's lifecycle", () => {
  test("starting clears whatever the previous run of that id printed", () => {
    useAiRunStore.setState({ linesByRun: { "run-1": [{ stream: "stdout", text: "old" }] } });

    useAiRunStore.getState().start("run-1");

    expect(useAiRunStore.getState().linesFor("run-1")).toEqual([]);
    expect(useAiRunStore.getState().active["run-1"]).toBe(true);
  });

  test("finishing leaves the output in place so a done job still shows what it printed", () => {
    useAiRunStore.getState().start("run-1");
    useAiRunStore.setState({ linesByRun: { "run-1": [{ stream: "stdout", text: "done" }] } });

    useAiRunStore.getState().finish("run-1");

    expect(useAiRunStore.getState().active["run-1"]).toBe(false);
    expect(useAiRunStore.getState().linesFor("run-1")).toHaveLength(1);
  });

  test("cancelling marks the run as stopping before the backend answers", async () => {
    await useAiRunStore.getState().cancel("run-1");

    expect(useAiRunStore.getState().cancelling["run-1"]).toBe(true);
    expect(api.cancelAiRun).toHaveBeenCalledWith("run-1");
  });

  // The backend refusing means the run had already ended; the caller's own promise is about to
  // resolve with the real answer, so this must not throw over it.
  test("a backend that refuses the cancel does not throw", async () => {
    api.cancelAiRun.mockRejectedValueOnce(new Error("no such run"));

    await expect(useAiRunStore.getState().cancel("run-1")).resolves.toBeUndefined();
  });

  test("clearing drops only that run's output", () => {
    useAiRunStore.setState({
      linesByRun: { "run-1": [{ stream: "stdout", text: "a" }], "run-2": [{ stream: "stdout", text: "b" }] },
    });

    useAiRunStore.getState().clear("run-1");

    expect(useAiRunStore.getState().linesByRun["run-1"]).toBe(undefined);
    expect(useAiRunStore.getState().linesFor("run-2")).toHaveLength(1);
  });
});

describe("reading a run that printed nothing", () => {
  // Referentially stable, or every unrelated store write re-renders the log panel through its
  // selector.
  test("returns the same empty array every time", () => {
    const first = useAiRunStore.getState().linesFor("never-started");

    expect(first).toEqual([]);
    expect(useAiRunStore.getState().linesFor("never-started")).toBe(first);
  });
});

describe("run ids", () => {
  test("carry their prefix so a stray id is recognisable in a log", () => {
    expect(newRunId("review")).toMatch(/^review-/);
  });

  test("are unique", () => {
    expect(newRunId("review")).not.toBe(newRunId("review"));
  });
});
