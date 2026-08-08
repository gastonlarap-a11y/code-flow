import { beforeEach, describe, expect, test, vi } from "vitest";
import type {
  BranchInfo,
  CommitFileInfo,
  FileDiffInfo,
  RepoStatusInfo,
  StashApplyOutcome,
} from "../types/domain";

/** A promise the test releases by hand, to hold one command in flight while another runs. */
function deferred<T>(): { promise: Promise<T>; release: (value: T) => void } {
  let release: (value: T) => void = () => {};
  const promise = new Promise<T>((resolve) => {
    release = resolve;
  });
  return { promise, release };
}

const branch = (name: string): BranchInfo => ({
  name,
  is_head: false,
  is_remote: false,
  upstream: null,
  ahead: 0,
  behind: 0,
  target: null,
});

const fileDiff = (path: string): FileDiffInfo => ({
  old_path: path,
  new_path: path,
  status: "modified",
  hunks: [],
});

/** A working tree holding exactly these unstaged paths, and nothing else. */
const statusWith = (paths: string[]): RepoStatusInfo => ({
  staged: [],
  unstaged: paths.map((path) => ({ path, status: "modified" })),
  untracked: [],
  conflicted: [],
  current_branch: "feature",
  is_detached: false,
});

const commitFile = (path: string): CommitFileInfo => ({
  old_path: path,
  new_path: path,
  status: "modified",
});

// The sidecar is the only thing this store talks to, so mocking `lib/ipc/commands` is what makes
// it testable at all under `environment: "node"` — the real module reaches `window.codeflow`
// through `lib/bridge/host`, and there is no `window` here. Every command is a `vi.fn()` so a test
// can decide per case which ones resolve and which reject.
vi.mock("../lib/ipc/commands", () => {
  const ok = <T>(value: T) => vi.fn(() => Promise.resolve(value));

  return {
    getStatus: ok(null),
    getWorkingDiff: ok([]),
    getStagedDiff: ok([]),
    listBranches: ok([]),
    listCommits: ok([]),
    listUnpushedCommits: ok([]),
    listStashes: ok([]),
    listRemotes: ok([]),
    isMerging: ok(false),
    listConflicts: ok([]),
    getCommitDiff: ok([]),
    listCommitFiles: ok([]),
    getCommitFileDiff: ok([]),
    checkoutLocalBranch: ok(undefined),
    stashSave: ok(undefined),
    stashApply: ok<StashApplyOutcome>("applied"),
    stashPop: ok<StashApplyOutcome>("applied"),
    stashDrop: ok(undefined),
    gitFetch: ok(undefined),
    gitPull: ok(undefined),
    gitPush: ok(undefined),
  };
});

// The blocked-checkout offer is a real dialog. The tests drive the answer directly rather than
// rendering one: `confirmAnswer` for the yes/no dialogs, `choiceAnswer` for the three-way one
// (`null` is a cancel).
let confirmAnswer = false;
let choiceAnswer: string | null = null;
const told: string[] = [];
vi.mock("./confirmStore", () => ({
  confirmAction: () => Promise.resolve(confirmAnswer),
  chooseAction: () => Promise.resolve(choiceAnswer),
  // An acknowledgement, not a question — the assertion is that it was raised at all.
  tellUser: (message: string) => {
    told.push(message);
    return Promise.resolve();
  },
}));

// Toasts are a UI side effect; the assertion is that a failure reaches one, not what it renders.
// The informational ones are collected separately: several outcomes here are *not* failures, and
// what they say is the whole point of them.
const toasts: string[] = [];
const infoToasts: string[] = [];
vi.mock("./toastStore", () => ({
  pushErrorToast: (message: string) => toasts.push(message),
  useToastStore: { getState: () => ({ pushToast: (message: string) => infoToasts.push(message) }) },
}));

const api = vi.mocked(await import("../lib/ipc/commands"));
const { useRepoStore } = await import("./repoStore");

const initial = useRepoStore.getState();

beforeEach(() => {
  toasts.length = 0;
  infoToasts.length = 0;
  told.length = 0;
  confirmAnswer = false;
  choiceAnswer = null;
  // `reset`, not `clear`. Clearing wipes recorded calls but leaves implementations in place, so the
  // standing rejection installed by "even when every command fails" outlived its own test and every
  // case after it saw the whole sidecar refusing — which no assertion caught until one of them
  // depended on a command's default. Resetting puts each `vi.fn(impl)` back to the factory's value.
  vi.resetAllMocks();
  useRepoStore.setState(initial, true);
});

describe("a refresh that fails", () => {
  test("reports the failure and clears the loading flag", async () => {
    // The shape of the real bug: one broken remote, one rejected command, and the sidebar
    // skeleton stayed up until the project was closed and reopened. Persistent, not `Once`: a
    // refresh retries once before reporting failure (see the "transient refresh race" tests below),
    // so a one-off rejection alone would recover silently and never reach the toast this asserts.
    api.listRemotes.mockRejectedValue(new Error("no such remote"));

    await useRepoStore.getState().setRepoPath("/repo/a");

    expect(useRepoStore.getState().projectLoading).toBe(false);
    expect(toasts.some((t) => t.includes("no such remote"))).toBe(true);
  });

  test("does not stop the other refreshers from landing", async () => {
    api.listRemotes.mockRejectedValueOnce(new Error("no such remote"));
    api.listBranches.mockResolvedValueOnce([branch("main")]);

    await useRepoStore.getState().setRepoPath("/repo/a");

    expect(useRepoStore.getState().branches).toHaveLength(1);
  });

  test("leaves the loading flag clear even when every command fails", async () => {
    for (const command of Object.values(api)) {
      command.mockRejectedValue(new Error("the core stopped"));
    }

    await useRepoStore.getState().setRepoPath("/repo/a");

    expect(useRepoStore.getState().projectLoading).toBe(false);
  });
});

describe("a refresh that resolves after the repo changed", () => {
  test("does not overwrite the repo the user switched to", async () => {
    // Project A's branches resolve only once B has been selected. Before the guard, A's answer
    // landed on top of B's, silently, until something forced another refresh.
    const inFlight = deferred<BranchInfo[]>();
    api.listBranches.mockImplementationOnce(() => inFlight.promise);

    const switchingToA = useRepoStore.getState().setRepoPath("/repo/a");

    api.listBranches.mockResolvedValue([branch("from-b")]);
    await useRepoStore.getState().setRepoPath("/repo/b");

    inFlight.release([branch("from-a")]);
    await switchingToA;

    expect(useRepoStore.getState().repoPath).toBe("/repo/b");
    expect(useRepoStore.getState().branches).toEqual([branch("from-b")]);
  });
});

describe("pull() and a transient post-pull refresh race", () => {
  // The bug this covers: a user pulled successfully (clean fast-forward, files updated on disk),
  // and still saw a generic error toast — because one of refreshAll()'s seven read-only follow-ups
  // hit a transient failure right after the mutation and reported itself with the same bare,
  // unlabeled message a real pull failure would use.

  test("a sub-refresh that fails once recovers on retry, and nothing is reported as failed", async () => {
    api.listRemotes.mockRejectedValueOnce(new Error("EBUSY"));
    useRepoStore.setState({ repoPath: "/repo/a" });

    await useRepoStore.getState().pull();

    expect(api.gitPull).toHaveBeenCalledTimes(1);
    expect(api.listRemotes).toHaveBeenCalledTimes(2);
    expect(toasts).toHaveLength(0);
    expect(useRepoStore.getState().error).toBe(null);
  });

  test("a sub-refresh that keeps failing names the refresh, not the pull", async () => {
    api.listRemotes.mockRejectedValue(new Error("EBUSY"));
    useRepoStore.setState({ repoPath: "/repo/a" });

    await useRepoStore.getState().pull();

    expect(api.gitPull).toHaveBeenCalledTimes(1);
    expect(api.listRemotes).toHaveBeenCalledTimes(2);
    expect(toasts).toHaveLength(1);
    const [message] = toasts;
    expect(message).toContain("EBUSY");
    expect(message?.toLowerCase()).toContain("remote");
    expect(message?.toLowerCase()).not.toContain("pull failed");
  });

  test("pull() itself failing is still reported as the pull failing, unlabeled", async () => {
    api.gitPull.mockRejectedValueOnce(new Error("could not read from remote"));
    useRepoStore.setState({ repoPath: "/repo/a" });

    await useRepoStore.getState().pull();

    expect(toasts).toHaveLength(1);
    const [message] = toasts;
    expect(message).toContain("could not read from remote");
    expect(message?.toLowerCase()).not.toContain("refresh");
  });
});

describe("selecting a commit", () => {
  test("reports a failure rather than rejecting into nowhere", async () => {
    // Its callers invoke it as a bare onClick, so a rejection here had nothing to catch it.
    api.listCommitFiles.mockRejectedValueOnce(new Error("bad object"));

    useRepoStore.setState({ repoPath: "/repo/a" });
    await useRepoStore.getState().selectCommit("deadbeef");

    expect(toasts.some((t) => t.includes("bad object"))).toBe(true);
    // The row stays expanded on a failure, so its spinner has to come down with it.
    expect(useRepoStore.getState().commitFilesLoading).toBe(false);
  });

  test("expands without fetching any diff", async () => {
    api.listCommitFiles.mockResolvedValueOnce([commitFile("first.ts")]);

    useRepoStore.setState({ repoPath: "/repo/a" });
    await useRepoStore.getState().selectCommit("commit-1");

    expect(useRepoStore.getState().commitFiles).toEqual([commitFile("first.ts")]);
    // The whole point of GIT-035: expanding a commit costs a file list, not its content.
    expect(api.getCommitFileDiff).not.toHaveBeenCalled();
  });

  test("drops a file list whose commit is no longer selected", async () => {
    const inFlight = deferred<CommitFileInfo[]>();
    api.listCommitFiles.mockImplementationOnce(() => inFlight.promise);

    useRepoStore.setState({ repoPath: "/repo/a" });
    const first = useRepoStore.getState().selectCommit("commit-1");

    api.listCommitFiles.mockResolvedValueOnce([commitFile("second.ts")]);
    await useRepoStore.getState().selectCommit("commit-2");

    inFlight.release([commitFile("first.ts")]);
    await first;

    expect(useRepoStore.getState().commitFiles).toEqual([commitFile("second.ts")]);
  });
});

describe("selecting a file inside a commit", () => {
  test("asks for that file's diff, old path included", async () => {
    useRepoStore.setState({ repoPath: "/repo/a", selectedCommitId: "commit-1" });
    await useRepoStore.getState().selectCommitFile({
      old_path: "was.ts",
      new_path: "now.ts",
      status: "renamed",
    });

    // `oldPath` is what stops a rename from arriving as a wholly-new file — see GIT-035.
    expect(api.getCommitFileDiff).toHaveBeenCalledWith("/repo/a", "commit-1", "now.ts", "was.ts");
    expect(useRepoStore.getState().selectedCommitFile).toBe("now.ts");
  });

  test("drops a diff whose file is no longer selected", async () => {
    const inFlight = deferred<FileDiffInfo[]>();
    api.getCommitFileDiff.mockImplementationOnce(() => inFlight.promise);

    useRepoStore.setState({ repoPath: "/repo/a", selectedCommitId: "commit-1" });
    const first = useRepoStore.getState().selectCommitFile(commitFile("first.ts"));

    api.getCommitFileDiff.mockResolvedValueOnce([fileDiff("second.ts")]);
    await useRepoStore.getState().selectCommitFile(commitFile("second.ts"));

    inFlight.release([fileDiff("first.ts")]);
    await first;

    expect(useRepoStore.getState().commitFileDiff).toEqual([fileDiff("second.ts")]);
  });

  test("clears the open file when the commit collapses", async () => {
    useRepoStore.setState({ repoPath: "/repo/a", selectedCommitId: "commit-1" });
    await useRepoStore.getState().selectCommitFile(commitFile("first.ts"));

    await useRepoStore.getState().selectCommit(null);

    expect(useRepoStore.getState().selectedCommitFile).toBeNull();
    expect(useRepoStore.getState().commitFileDiff).toEqual([]);
  });
});

/**
 * `CHECKOUT_CONFLICT: ` is `XLANG-002`, mirroring `CHECKOUT_CONFLICT_PREFIX` in `Branches.cs`. It
 * marks the one checkout failure that has a way out, and it is matched here by text: rename it on
 * the sidecar and this store stops offering the stash-and-retry, silently degrading to "checkout
 * failed" for the case it was built to rescue.
 */
describe("a checkout blocked by uncommitted work", () => {
  test("parks the work in a stash and retries when that is the answer", async () => {
    choiceAnswer = "stash";
    api.checkoutLocalBranch.mockRejectedValueOnce(
      new Error("CHECKOUT_CONFLICT: your local changes would be overwritten"),
    );

    useRepoStore.setState({ repoPath: "/repo/a" });
    await useRepoStore.getState().checkoutBranch("feature");

    expect(api.stashSave).toHaveBeenCalledOnce();
    // Twice: the attempt that hit the conflict, and the retry after stashing.
    expect(api.checkoutLocalBranch).toHaveBeenCalledTimes(2);
    // Parking means parking: the stash stays put.
    expect(api.stashPop).not.toHaveBeenCalled();
    expect(useRepoStore.getState().error).toBe(null);
  });

  test("carrying the work stashes, switches and applies it, in that order", async () => {
    choiceAnswer = "carry";
    const order: string[] = [];
    api.checkoutLocalBranch
      .mockRejectedValueOnce(new Error("CHECKOUT_CONFLICT: your local changes would be overwritten"))
      .mockImplementationOnce(() => {
        order.push("checkout");
        return Promise.resolve();
      });
    api.stashSave.mockImplementationOnce(() => {
      order.push("stash");
      return Promise.resolve();
    });
    api.stashApply.mockImplementationOnce(() => {
      order.push("apply");
      return Promise.resolve("applied");
    });

    useRepoStore.setState({ repoPath: "/repo/a" });
    await useRepoStore.getState().checkoutBranch("feature");

    expect(order).toEqual(["stash", "checkout", "apply"]);
    expect(useRepoStore.getState().error).toBe(null);
  });

  test("the backup stash survives a clean carry", async () => {
    // The bug that cost a day's work: pop deletes the entry the moment it applies, and when the
    // destination already has that content it deletes it having brought nothing across.
    choiceAnswer = "carry";
    api.checkoutLocalBranch.mockRejectedValueOnce(
      new Error("CHECKOUT_CONFLICT: your local changes would be overwritten"),
    );
    api.stashApply.mockResolvedValueOnce("applied");

    useRepoStore.setState({ repoPath: "/repo/a" });
    await useRepoStore.getState().checkoutBranch("feature");

    expect(api.stashPop).not.toHaveBeenCalled();
    expect(api.stashDrop).not.toHaveBeenCalled();
  });

  test("an apply that brings nothing across is explained, not toasted away", async () => {
    // The destination already had that content: the Changes panel ends up empty, which reads as
    // lost work unless something says otherwise — and a toast that fades in five seconds did not.
    choiceAnswer = "carry";
    api.checkoutLocalBranch.mockRejectedValueOnce(
      new Error("CHECKOUT_CONFLICT: your local changes would be overwritten"),
    );
    api.stashApply.mockResolvedValueOnce("applied");
    api.getStatus.mockResolvedValue(statusWith([]));

    useRepoStore.setState({ repoPath: "/repo/a" });
    await useRepoStore.getState().checkoutBranch("feature");

    expect(told.some((message) => message.includes("nothing to bring over"))).toBe(true);
    expect(useRepoStore.getState().error).toBe(null);
  });

  test("an apply that does bring changes across is reported as the win it is", async () => {
    choiceAnswer = "carry";
    api.checkoutLocalBranch.mockRejectedValueOnce(
      new Error("CHECKOUT_CONFLICT: your local changes would be overwritten"),
    );
    api.stashApply.mockResolvedValueOnce("applied");
    api.getStatus.mockResolvedValue(statusWith(["deploy/deploy.ts"]));

    useRepoStore.setState({ repoPath: "/repo/a" });
    await useRepoStore.getState().checkoutBranch("feature");

    expect(infoToasts.some((t) => t.includes("came along"))).toBe(true);
  });

  test("an apply that conflicts is an outcome, not a failure", async () => {
    // GIT-015: the stash survives and the conflict UI takes over, so reporting an error here
    // would be telling the user something broke when the recovery path is working as designed.
    choiceAnswer = "carry";
    api.checkoutLocalBranch.mockRejectedValueOnce(
      new Error("CHECKOUT_CONFLICT: your local changes would be overwritten"),
    );
    api.stashApply.mockResolvedValueOnce("conflicts");

    useRepoStore.setState({ repoPath: "/repo/a" });
    await useRepoStore.getState().checkoutBranch("feature");

    expect(useRepoStore.getState().error).toBe(null);
  });

  test("cancelling is not a failure", async () => {
    // It used to re-throw the original error, so declining an offer looked like a broken checkout.
    choiceAnswer = null;
    api.checkoutLocalBranch.mockRejectedValueOnce(
      new Error("CHECKOUT_CONFLICT: your local changes would be overwritten"),
    );

    useRepoStore.setState({ repoPath: "/repo/a" });
    await useRepoStore.getState().checkoutBranch("feature");

    expect(api.stashSave).not.toHaveBeenCalled();
    expect(useRepoStore.getState().error).toBe(null);
    expect(toasts).toEqual([]);
  });

  test("any other checkout failure is never offered a stash", async () => {
    choiceAnswer = "carry";
    api.checkoutLocalBranch.mockRejectedValueOnce(new Error("pathspec 'feature' did not match"));

    useRepoStore.setState({ repoPath: "/repo/a" });
    await useRepoStore.getState().checkoutBranch("feature");

    expect(api.stashSave).not.toHaveBeenCalled();
    expect(api.checkoutLocalBranch).toHaveBeenCalledOnce();
    expect(useRepoStore.getState().error).toContain("pathspec");
  });

  test("the busy flags are released whichever way it ends", async () => {
    api.checkoutLocalBranch.mockRejectedValueOnce(new Error("CHECKOUT_CONFLICT: blocked"));

    useRepoStore.setState({ repoPath: "/repo/a" });
    await useRepoStore.getState().checkoutBranch("feature");

    expect(useRepoStore.getState().busy).toBe(false);
    expect(useRepoStore.getState().checkingOutBranch).toBe(null);
  });
});

describe("a read that belongs to the branch being left", () => {
  test("never lands after the checkout that replaced it", async () => {
    // The flicker, reproduced without a DOM: `RepoWatcher` fires while a checkout is still writing
    // files, `App.tsx` answers with a refresh, and that read of the outgoing branch used to resolve
    // last and win — showing the old branch's files for a beat on the new one.
    const inFlight = deferred<RepoStatusInfo>();
    api.getStatus.mockImplementationOnce(() => inFlight.promise);

    useRepoStore.setState({ repoPath: "/repo/a" });
    const stale = useRepoStore.getState().refreshStatus();

    // The checkout starts and finishes while that read is still out.
    await useRepoStore.getState().checkoutBranch("feature");

    inFlight.release(statusWith(["left-behind.ts"]));
    await stale;

    expect(useRepoStore.getState().status?.unstaged ?? []).toEqual([]);
  });
});

describe("conflicts outside a merge", () => {
  test("are read even when nothing is merging", async () => {
    // `is_merging` is MERGE_HEAD and nothing else, while `list_conflicts` reads the index — and a
    // stash that would not apply marks the index without ever writing MERGE_HEAD. Gating the read
    // on `merging` is what left a working tree full of conflict markers with no UI to resolve them.
    api.isMerging.mockResolvedValueOnce(false);
    api.listConflicts.mockResolvedValueOnce([{ path: "src/api/manga.ts" }]);

    useRepoStore.setState({ repoPath: "/repo/a" });
    await useRepoStore.getState().refreshMergeState();

    expect(useRepoStore.getState().merging).toBe(false);
    expect(useRepoStore.getState().conflicts).toEqual([{ path: "src/api/manga.ts" }]);
  });
});
