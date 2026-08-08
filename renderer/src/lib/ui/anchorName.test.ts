import { describe, expect, test } from "vitest";
import { anchorName } from "./anchorName";

/** What a CSS `<dashed-ident>` is allowed to be: two hyphens, then ident characters only. */
const DASHED_IDENT = /^--[a-zA-Z0-9_-]+$/;

describe("anchorName", () => {
  test("sanitises the colons React's useId emits", () => {
    // `«r0»` in React 19, `:r0:` in 18 — either way the punctuation has to go.
    expect(anchorName("tooltip", ":r0:")).toMatch(DASHED_IDENT);
    expect(anchorName("tooltip", "«r1»")).toMatch(DASHED_IDENT);
  });

  test("survives whatever a caller puts in the prefix", () => {
    expect(anchorName("row actions", ":r2:")).toMatch(DASHED_IDENT);
    expect(anchorName("tab/strip", ":r3:")).toMatch(DASHED_IDENT);
  });

  test("keeps distinct ids distinct — two anchors sharing a name would collide", () => {
    expect(anchorName("tooltip", ":r0:")).not.toBe(anchorName("tooltip", ":r1:"));
  });

  test("keeps the prefix readable so devtools stays legible", () => {
    expect(anchorName("tooltip", ":r7:")).toContain("tooltip");
  });

  test("is stable for the same input", () => {
    expect(anchorName("tooltip", ":r4:")).toBe(anchorName("tooltip", ":r4:"));
  });
});
