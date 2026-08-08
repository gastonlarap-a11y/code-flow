import { beforeEach, describe, expect, test, vi } from "vitest";
import type { PrTarget } from "../lib/prTarget";
import type * as PrTargetModule from "../lib/prTarget";

/**
 * The renderer half of three sentinel prefixes: `XLANG-012`, `XLANG-013` and `XLANG-014`.
 *
 * The transport carries a string, so an error the UI has to answer differently is marked in the
 * string itself. `docs/business-rules/13-cross-language-contracts.md` records each one VERBATIM,
 * trailing space included, and each is matched here with `includes` against text produced by
 * `AzureException.RefusedPrefix`, `GitHubException.SelfApprovalPrefix` and
 * `GitHubHost.StaleReviewPrefix`. Nothing checks the two sides agree: rename one on the sidecar and
 * this store stops recognising it — no throw, no failing build, just the raw host error where the
 * user used to get an answer they could act on.
 *
 * The three are deliberately *not* handled the same way, which is the other half of what this pins.
 */

const target: PrTarget = { kind: "project", projectId: "p1" };

const pullRequest = (id: number) => ({
  id,
  title: `PR ${id}`,
  url: `https://example.com/pr/${id}`,
  state: "open",
  is_draft: false,
  author: "someone",
  source_branch: "feature",
  target_branch: "main",
  created_at: "2026-01-01T00:00:00Z",
  updated_at: "2026-01-01T00:00:00Z",
});

const toasts: { message: string; kind: string }[] = [];

vi.mock("../lib/ipc/commands", () => ({
  listPullRequests: vi.fn(() => Promise.resolve([])),
  createPullRequest: vi.fn(),
}));

vi.mock("../lib/prTarget", async (importOriginal) => {
  // `targetKey` and friends are pure and the store's bookkeeping depends on them, so only the
  // calls that reach the sidecar are replaced.
  const actual = await importOriginal<typeof PrTargetModule>();
  return {
    ...actual,
    actOnPr: vi.fn(),
    postFindings: vi.fn(),
    reviewDecision: vi.fn(() => Promise.resolve("none")),
    listCommentThreads: vi.fn(() => Promise.resolve([])),
  };
});

vi.mock("./toastStore", () => ({
  pushErrorToast: (message: string) => toasts.push({ message, kind: "error" }),
  useToastStore: {
    getState: () => ({ pushToast: (message: string, kind: string) => toasts.push({ message, kind }) }),
  },
}));

const api = vi.mocked(await import("../lib/ipc/commands"));
const prTarget = vi.mocked(await import("../lib/prTarget"));
const { usePrStore } = await import("./prStore");

const initial = usePrStore.getState();

beforeEach(() => {
  toasts.length = 0;
  vi.clearAllMocks();
  usePrStore.setState(initial, true);
});

describe("CREDENTIAL_REFUSED — the prefix is stripped and a flag is raised", () => {
  test("the message loses the prefix and the project is marked as refused", async () => {
    api.listPullRequests.mockRejectedValueOnce(
      new Error("CREDENTIAL_REFUSED: the saved token was rejected by dev.azure.com"),
    );

    await usePrStore.getState().loadPullRequests("p1");

    // The raw text still says something useful ("the host said no"), so it is kept — only the
    // marker goes. The flag is what turns "unreachable" into "replace your token".
    expect(usePrStore.getState().loadErrorByProject["p1"]).toBe(
      "Error: the saved token was rejected by dev.azure.com",
    );
    expect(usePrStore.getState().credentialRefusedByProject["p1"]).toBe(true);
  });

  test("an ordinary failure keeps its message and raises no flag", async () => {
    api.listPullRequests.mockRejectedValueOnce(new Error("getaddrinfo ENOTFOUND dev.azure.com"));

    await usePrStore.getState().loadPullRequests("p1");

    expect(usePrStore.getState().loadErrorByProject["p1"]).toBe(
      "Error: getaddrinfo ENOTFOUND dev.azure.com",
    );
    expect(usePrStore.getState().credentialRefusedByProject["p1"]).toBe(false);
  });

  // Without the trailing space the marker is a different string, and that is the whole contract.
  test("a prefix missing its trailing space is not recognised", async () => {
    api.listPullRequests.mockRejectedValueOnce(new Error("CREDENTIAL_REFUSED:no space here"));

    await usePrStore.getState().loadPullRequests("p1");

    expect(usePrStore.getState().credentialRefusedByProject["p1"]).toBe(false);
  });

  test("a load that succeeds clears nothing it should not", async () => {
    api.listPullRequests.mockResolvedValueOnce([pullRequest(7)] as never);

    await usePrStore.getState().loadPullRequests("p1");

    expect(usePrStore.getState().prsByProject["p1"]).toHaveLength(1);
    expect(usePrStore.getState().loadingProjectId).toBe(null);
  });
});

describe("SELF_APPROVAL — the message is replaced, not stripped", () => {
  test("the user is told the rule instead of GitHub's error envelope", async () => {
    prTarget.actOnPr.mockRejectedValueOnce(
      new Error('SELF_APPROVAL: {"message":"Unprocessable Entity","status":"422"}'),
    );

    await usePrStore.getState().actOnPr(target, 7, "approve");

    // Unlike CREDENTIAL_REFUSED, keeping the text would tell the user nothing they can act on: it
    // is a JSON error envelope for a rule no retry can satisfy.
    const [toast] = toasts;
    if (!toast) throw new Error("expected a toast");
    expect(toast.kind).toBe("error");
    expect(toast.message).not.toContain("Unprocessable Entity");
    expect(toast.message).not.toContain("SELF_APPROVAL");
  });

  test("any other failure is shown as it came", async () => {
    prTarget.actOnPr.mockRejectedValueOnce(new Error("403 Forbidden"));

    await usePrStore.getState().actOnPr(target, 7, "approve");

    const [toast] = toasts;
    if (!toast) throw new Error("expected a toast");
    expect(toast.message).toContain("403 Forbidden");
  });

  test("the busy flag is released whichever way it ends", async () => {
    prTarget.actOnPr.mockRejectedValueOnce(new Error("SELF_APPROVAL: nope"));

    await usePrStore.getState().actOnPr(target, 7, "approve");

    expect(usePrStore.getState().prActionBusy).toBe(null);
  });
});

describe("STALE_REVIEW — the prefix is stripped and the message kept", () => {
  test("the text naming both commits survives", async () => {
    prTarget.postFindings.mockRejectedValueOnce(
      new Error("STALE_REVIEW: reviewed abc1234, head is now def5678 — run the review again"),
    );

    await expect(usePrStore.getState().postReview(target, 7, "run-1", [], false, null)).rejects.toThrow();

    // This text says what moved and what to do about it, which is exactly what the user needs —
    // so unlike SELF_APPROVAL it is kept, and unlike a plain failure the marker is removed.
    const [toast] = toasts;
    if (!toast) throw new Error("expected a toast");
    expect(toast.message).toBe(
      "Error: reviewed abc1234, head is now def5678 — run the review again",
    );
  });

  test("the failure still propagates so the caller does not record a posted review", async () => {
    prTarget.postFindings.mockRejectedValueOnce(new Error("STALE_REVIEW: moved"));

    await expect(usePrStore.getState().postReview(target, 7, "run-1", [], false, null)).rejects.toThrow();

    expect(usePrStore.getState().posted).toBe(false);
    expect(usePrStore.getState().posting).toBe(false);
  });

  test("a successful post marks the review as posted", async () => {
    prTarget.postFindings.mockResolvedValueOnce(undefined as never);

    await usePrStore.getState().postReview(target, 7, "run-1", [], false, null);

    expect(usePrStore.getState().posted).toBe(true);
  });
});
