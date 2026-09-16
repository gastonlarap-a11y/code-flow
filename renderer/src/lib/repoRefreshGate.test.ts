import { describe, expect, test } from "vitest";
import { watcherMayRefresh } from "./repoRefreshGate";

const idle = { checkingOutBranch: null, remoteOp: null, isGitRepo: true } as const;

describe("watcherMayRefresh", () => {
  test("an external change refreshes when the app is not writing", () => {
    // The case this must not break: someone edits a file in another editor, or a script touches the
    // tree. That is what the watcher is for.
    expect(watcherMayRefresh(idle)).toBe(true);
  });

  test("a checkout in flight silences it", () => {
    // The flicker: the watcher fires on the leading edge of the burst a checkout creates, so the
    // refresh answering it reads a tree halfway between two branches — the outgoing branch's
    // changes showed for a beat and then vanished.
    expect(watcherMayRefresh({ ...idle, checkingOutBranch: "feature" })).toBe(false);
  });

  test("a remote operation in flight silences it too", () => {
    // A pull rewrites the tree the same way, and ends with its own refresh over the settled result.
    for (const remoteOp of ["fetch", "pull", "push"] as const) {
      expect(watcherMayRefresh({ ...idle, remoteOp })).toBe(false);
    }
  });

  test("a project that is not a repository never refreshes", () => {
    // GIT-039. The watcher is a plain FileSystemWatcher and fires on any folder, so without this
    // every save in a non-git project starts seven reads that each throw and each raise a toast.
    expect(watcherMayRefresh({ ...idle, isGitRepo: false })).toBe(false);
  });

  test("an unresolved project is treated as a repository", () => {
    // `null` is "the answer has not arrived yet", one IPC round-trip wide. Refusing there would
    // drop the first external change after opening a repo, which is worse than one failed read.
    expect(watcherMayRefresh({ ...idle, isGitRepo: null })).toBe(true);
  });

  test("a checkout onto a branch named the empty string still counts as in flight", () => {
    // Guarded on `null`, not on falsiness. A branch cannot really be named "" — but a truthiness
    // check here would be a silent hole, and the flag's contract is that null means "not running".
    expect(watcherMayRefresh({ ...idle, checkingOutBranch: "" })).toBe(false);
  });
});
