import { describe, expect, it } from "vitest";
import { currentBranch, preferredBaseBranch, PREFERRED_TARGETS } from "./branches";

const b = (name: string, extra: { remote?: boolean; head?: boolean } = {}) => ({
  name,
  is_remote: extra.remote ?? false,
  is_head: extra.head ?? false,
});

describe("preferredBaseBranch", () => {
  it("picks the first branch whose name is conventional", () => {
    expect(preferredBaseBranch([b("feature/x"), b("main"), b("release/2026")], "feature/x")).toBe("main");
    expect(preferredBaseBranch([b("feature/x"), b("develop")], "feature/x")).toBe("develop");
  });

  it("goes by the repository's order, not by the list's — the behaviour it was extracted from", () => {
    // `PREFERRED_TARGETS` is a set of names, not a ranking. Pinned because reading it as a ranking
    // is the plausible mistake, and it would change which target a PR dialog pre-selects.
    expect(preferredBaseBranch([b("develop"), b("main")], "feature/x")).toBe("develop");
  });

  it("never returns the branch it was told to exclude", () => {
    // The source branch of a PR is not a target for it, and `main` checked out is the normal case.
    expect(preferredBaseBranch([b("main"), b("release/2026")], "main")).toBe("release/2026");
  });

  it("falls back to the first local branch when nothing is conventional", () => {
    expect(preferredBaseBranch([b("release/2026"), b("hotfix")], "hotfix")).toBe("release/2026");
  });

  it("ignores remote branches", () => {
    expect(preferredBaseBranch([b("main", { remote: true }), b("trunk")], "x")).toBe("trunk");
  });

  it("answers the empty string rather than throwing on an empty repository", () => {
    expect(preferredBaseBranch([], "x")).toBe("");
  });
});

describe("currentBranch", () => {
  it("is the checked-out one", () => {
    expect(currentBranch([b("main"), b("feature/x", { head: true })])).toBe("feature/x");
  });

  it("falls back to the first local branch on a detached HEAD", () => {
    expect(currentBranch([b("origin/main", { remote: true }), b("main")])).toBe("main");
  });
});

describe("PREFERRED_TARGETS", () => {
  it("is the list the PR modal used to hold on its own", () => {
    expect(PREFERRED_TARGETS).toEqual(["main", "master", "develop", "development"]);
  });
});
