import { describe, expect, it } from "vitest";
import { parseRecent, pushRecent, resolveRecent } from "./recentProjects";

describe("pushRecent", () => {
  it("puts the newest first", () => {
    expect(pushRecent(["a", "b"], "c")).toEqual(["c", "a", "b"]);
  });

  // Without this a project opened twice would occupy two rows of a four-row card.
  it("moves an already-known project to the front instead of duplicating it", () => {
    expect(pushRecent(["a", "b", "c"], "c")).toEqual(["c", "a", "b"]);
  });

  it("caps the list, dropping the oldest", () => {
    const full = ["1", "2", "3", "4", "5", "6", "7", "8"];
    expect(pushRecent(full, "9")).toEqual(["9", "1", "2", "3", "4", "5", "6", "7"]);
  });
});

describe("resolveRecent", () => {
  const projects = [{ id: "a" }, { id: "b" }];

  it("returns projects in recency order, not store order", () => {
    expect(resolveRecent(["b", "a"], projects)).toEqual([{ id: "b" }, { id: "a" }]);
  });

  // A deleted repository leaves its id in the setting; the card must not offer a row to nowhere.
  it("drops ids that no longer name a project", () => {
    expect(resolveRecent(["gone", "a"], projects)).toEqual([{ id: "a" }]);
  });
});

describe("parseRecent", () => {
  it("reads a stored list", () => {
    expect(parseRecent('["a","b"]')).toEqual(["a", "b"]);
  });

  it.each([
    ["nothing stored yet", null],
    ["not JSON at all", "{oops"],
    ["JSON that is not a list", '{"a":1}'],
  ])("reads %s as no history", (_case, raw) => {
    expect(parseRecent(raw)).toEqual([]);
  });

  it("keeps only the strings out of a mixed list", () => {
    expect(parseRecent('["a",7,null,"b"]')).toEqual(["a", "b"]);
  });
});
