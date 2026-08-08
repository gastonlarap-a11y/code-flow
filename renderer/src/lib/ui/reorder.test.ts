import { describe, expect, test } from "vitest";
import { moveBy, moveTo } from "./reorder";

const LIST = ["a", "b", "c", "d"];

describe("moveTo", () => {
  test("inserts before the entry currently at the slot", () => {
    expect(moveTo(LIST, "d", 1)).toEqual(["a", "d", "b", "c"]);
    expect(moveTo(LIST, "a", 2)).toEqual(["b", "a", "c", "d"]);
  });

  test("a slot at the length means the end", () => {
    expect(moveTo(LIST, "a", LIST.length)).toEqual(["b", "c", "d", "a"]);
  });

  test("moving an entry onto its own slot changes nothing", () => {
    expect(moveTo(LIST, "b", 1)).toEqual(LIST);
    expect(moveTo(LIST, "b", 2)).toEqual(LIST);
  });

  test("slots outside the list are clamped rather than dropping the entry", () => {
    expect(moveTo(LIST, "b", -5)).toEqual(["b", "a", "c", "d"]);
    expect(moveTo(LIST, "b", 99)).toEqual(["a", "c", "d", "b"]);
  });

  test("an unknown id leaves the list alone", () => {
    expect(moveTo(LIST, "zzz", 0)).toEqual(LIST);
  });

  test("never loses or duplicates an entry", () => {
    for (let slot = -1; slot <= LIST.length + 1; slot++) {
      for (const id of LIST) {
        const moved = moveTo(LIST, id, slot);
        expect(moved).toHaveLength(LIST.length);
        expect([...moved].sort()).toEqual([...LIST].sort());
      }
    }
  });
});

describe("moveBy", () => {
  test("one step in each direction", () => {
    expect(moveBy(LIST, "b", 1)).toEqual(["a", "c", "b", "d"]);
    expect(moveBy(LIST, "c", -1)).toEqual(["a", "c", "b", "d"]);
  });

  test("stops at the ends instead of wrapping", () => {
    expect(moveBy(LIST, "a", -1)).toEqual(LIST);
    expect(moveBy(LIST, "d", 1)).toEqual(LIST);
  });

  test("stepping down then up returns the original order", () => {
    expect(moveBy(moveBy(LIST, "b", 1), "b", -1)).toEqual(LIST);
  });

  /** The two inputs must produce the same list, or the drag and the arrow keys disagree. */
  test("agrees with the slot the equivalent drag would produce", () => {
    expect(moveBy(LIST, "b", 1)).toEqual(moveTo(LIST, "b", 3));
    expect(moveBy(LIST, "c", -1)).toEqual(moveTo(LIST, "c", 1));
  });

  test("an unknown id leaves the list alone", () => {
    expect(moveBy(LIST, "zzz", 1)).toEqual(LIST);
  });
});
