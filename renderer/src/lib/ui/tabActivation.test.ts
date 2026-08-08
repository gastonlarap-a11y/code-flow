import { describe, expect, test } from "vitest";
import { tabKeyResult } from "./tabActivation";

const tabs = [{}, {}, {}];
const withDisabled = [{}, {}, { disabled: true }];

describe("tab activation", () => {
  test("automatic selects the tab an arrow lands on", () => {
    expect(tabKeyResult("ArrowRight", tabs, 0, "automatic")).toEqual({
      kind: "focus-and-select",
      index: 1,
    });
  });

  /**
   * The reason this mode exists. The AI panel's Analyze tab starts a Claude run when it mounts, so
   * arrowing past it must not commit — otherwise one key press spends money on a run nobody asked
   * for. Selecting stays an explicit Enter or Space.
   */
  test("manual moves focus without selecting", () => {
    expect(tabKeyResult("ArrowRight", tabs, 0, "manual")).toEqual({ kind: "focus", index: 1 });
    expect(tabKeyResult("ArrowLeft", tabs, 1, "manual")).toEqual({ kind: "focus", index: 0 });
    expect(tabKeyResult("Home", tabs, 2, "manual")).toEqual({ kind: "focus", index: 0 });
    expect(tabKeyResult("End", tabs, 0, "manual")).toEqual({ kind: "focus", index: 2 });
  });

  test("manual commits on Enter and Space", () => {
    expect(tabKeyResult("Enter", tabs, 2, "manual")).toEqual({ kind: "select", index: 2 });
    expect(tabKeyResult(" ", tabs, 1, "manual")).toEqual({ kind: "select", index: 1 });
  });

  test("automatic has nothing left to commit, so Enter is inert", () => {
    expect(tabKeyResult("Enter", tabs, 1, "automatic")).toEqual({ kind: "none" });
    expect(tabKeyResult(" ", tabs, 1, "automatic")).toEqual({ kind: "none" });
  });

  // A disabled tab is a real case here: the PR tab is disabled until a PR is selected.
  test("both modes skip a disabled tab rather than landing on it", () => {
    expect(tabKeyResult("ArrowRight", withDisabled, 1, "manual")).toEqual({ kind: "focus", index: 0 });
    expect(tabKeyResult("ArrowRight", withDisabled, 1, "automatic")).toEqual({
      kind: "focus-and-select",
      index: 0,
    });
  });

  test("a disabled tab cannot be committed even with focus parked on it", () => {
    expect(tabKeyResult("Enter", withDisabled, 2, "manual")).toEqual({ kind: "none" });
  });

  test("keys the strip does not own are left alone in both modes", () => {
    for (const activation of ["automatic", "manual"] as const) {
      expect(tabKeyResult("a", tabs, 0, activation)).toEqual({ kind: "none" });
      expect(tabKeyResult("ArrowDown", tabs, 0, activation)).toEqual({ kind: "none" });
    }
  });

  // Escape/Tab close a menu; a tab strip has nothing to close, so they must pass through to the
  // page rather than be swallowed.
  test("Escape and Tab are not the strip's to handle", () => {
    expect(tabKeyResult("Escape", tabs, 0, "manual")).toEqual({ kind: "none" });
    expect(tabKeyResult("Tab", tabs, 0, "manual")).toEqual({ kind: "none" });
  });
});
