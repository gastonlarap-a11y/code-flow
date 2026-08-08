import { describe, expect, test } from "vitest";
import { manualCopyChord } from "./copyHint";

describe("manualCopyChord", () => {
  test("names the Command key only where it exists", () => {
    expect(manualCopyChord("macos")).toBe("⌘C");
  });

  test("every other platform gets the key it actually has", () => {
    // The bug this replaces: `⌘C` was hardcoded in both locales, so a Windows user whose clipboard
    // failed was told to press a key that is not on their keyboard.
    expect(manualCopyChord("windows")).toBe("Ctrl+C");
    expect(manualCopyChord("linux")).toBe("Ctrl+C");
  });

  test("an unresolved platform falls to the majority answer rather than the Mac one", () => {
    // `unknown` is what a plain `vite dev` in a browser resolves to, and what a failed platform
    // read gives on any non-Mac machine — guessing Command there would reintroduce the bug.
    expect(manualCopyChord("unknown")).toBe("Ctrl+C");
  });
});
