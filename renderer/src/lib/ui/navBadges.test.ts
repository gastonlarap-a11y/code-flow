import { describe, expect, it } from "vitest";
import { navBadges } from "./navBadges";

const QUIET = { uncommittedChanges: 0, openPrs: 0 };

describe("navBadges", () => {
  it("badges the changes module with the uncommitted count", () => {
    expect(navBadges({ ...QUIET, uncommittedChanges: 3 })).toEqual({ changes: 3 });
  });

  it("badges Home with the open pull requests, since that is where the list is", () => {
    expect(navBadges({ ...QUIET, openPrs: 2 })).toEqual({ home: 2 });
  });

  it("badges both at once", () => {
    expect(navBadges({ uncommittedChanges: 1, openPrs: 4 })).toEqual({ changes: 1, home: 4 });
  });

  // The distinction the component depends on: a quiet module has no key at all, so nothing renders.
  // Returning `0` here would put a "0" pill on the row for the entire time a repo is clean.
  it("leaves the key out entirely when there is nothing to count", () => {
    const badges = navBadges(QUIET);
    expect(badges).toEqual({});
    expect("changes" in badges).toBe(false);
    expect("home" in badges).toBe(false);
  });

  it("badges no other module", () => {
    expect(Object.keys(navBadges({ uncommittedChanges: 7, openPrs: 7 })).sort()).toEqual(["changes", "home"]);
  });
});
