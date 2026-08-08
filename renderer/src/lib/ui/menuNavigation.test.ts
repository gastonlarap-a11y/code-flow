import { describe, expect, test } from "vitest";
import { edgeEnabledIndex, menuKeyAction, nextEnabledIndex } from "./menuNavigation";

const enabled = [{}, {}, {}];
const withGap = [{}, { disabled: true }, {}];
const allDisabled = [{ disabled: true }, { disabled: true }];

describe("nextEnabledIndex", () => {
  test("steps to the neighbour when it is available", () => {
    expect(nextEnabledIndex(enabled, 0, 1)).toBe(1);
    expect(nextEnabledIndex(enabled, 1, -1)).toBe(0);
  });

  test("wraps at both ends", () => {
    expect(nextEnabledIndex(enabled, 2, 1)).toBe(0);
    expect(nextEnabledIndex(enabled, 0, -1)).toBe(2);
  });

  test("skips disabled entries instead of stopping on them", () => {
    expect(nextEnabledIndex(withGap, 0, 1)).toBe(2);
    expect(nextEnabledIndex(withGap, 2, -1)).toBe(0);
  });

  test("returns -1 when nothing is selectable, rather than a disabled index", () => {
    expect(nextEnabledIndex(allDisabled, 0, 1)).toBe(-1);
    expect(nextEnabledIndex([], 0, 1)).toBe(-1);
  });

  test("starts from nothing selected", () => {
    expect(nextEnabledIndex(enabled, -1, 1)).toBe(0);
    expect(nextEnabledIndex(withGap, -1, 1)).toBe(0);
  });

  test("a single enabled item is its own neighbour in both directions", () => {
    expect(nextEnabledIndex([{}], 0, 1)).toBe(0);
    expect(nextEnabledIndex([{}], 0, -1)).toBe(0);
  });
});

describe("edgeEnabledIndex", () => {
  test("finds the first and last selectable item", () => {
    expect(edgeEnabledIndex(enabled, 1)).toBe(0);
    expect(edgeEnabledIndex(enabled, -1)).toBe(2);
  });

  test("skips past disabled entries at the edges", () => {
    const edges = [{ disabled: true }, {}, { disabled: true }];
    expect(edgeEnabledIndex(edges, 1)).toBe(1);
    expect(edgeEnabledIndex(edges, -1)).toBe(1);
  });

  test("reports -1 for a menu with nothing available", () => {
    expect(edgeEnabledIndex(allDisabled, 1)).toBe(-1);
  });
});

describe("menuKeyAction", () => {
  test("uses the arrow pair that matches the orientation", () => {
    expect(menuKeyAction("ArrowDown", enabled, 0)).toEqual({ kind: "move", index: 1 });
    expect(menuKeyAction("ArrowRight", enabled, 0)).toEqual({ kind: "none" });

    expect(menuKeyAction("ArrowRight", enabled, 0, "horizontal")).toEqual({ kind: "move", index: 1 });
    expect(menuKeyAction("ArrowDown", enabled, 0, "horizontal")).toEqual({ kind: "none" });
  });

  test("Home and End jump to the edges in either orientation", () => {
    expect(menuKeyAction("Home", enabled, 2)).toEqual({ kind: "move", index: 0 });
    expect(menuKeyAction("End", enabled, 0, "horizontal")).toEqual({ kind: "move", index: 2 });
  });

  test("Escape and Tab close, so focus is never stranded inside", () => {
    expect(menuKeyAction("Escape", enabled, 1)).toEqual({ kind: "close" });
    expect(menuKeyAction("Tab", enabled, 1)).toEqual({ kind: "close" });
  });

  test("Enter and Space activate what is focused", () => {
    expect(menuKeyAction("Enter", enabled, 1)).toEqual({ kind: "activate", index: 1 });
    expect(menuKeyAction(" ", enabled, 2)).toEqual({ kind: "activate", index: 2 });
  });

  test("Enter on nothing, or on a disabled item, fires nothing", () => {
    expect(menuKeyAction("Enter", enabled, -1)).toEqual({ kind: "none" });
    expect(menuKeyAction("Enter", withGap, 1)).toEqual({ kind: "none" });
    expect(menuKeyAction("Enter", enabled, 99)).toEqual({ kind: "none" });
  });

  test("movement in a menu with nothing available is inert, not a jump to a disabled row", () => {
    for (const key of ["ArrowDown", "ArrowUp", "Home", "End"]) {
      expect(menuKeyAction(key, allDisabled, 0)).toEqual({ kind: "none" });
    }
  });

  test("keys the menu does not own are left for the caller", () => {
    expect(menuKeyAction("a", enabled, 0)).toEqual({ kind: "none" });
    expect(menuKeyAction("PageDown", enabled, 0)).toEqual({ kind: "none" });
  });
});
