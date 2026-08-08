import { describe, expect, test } from "vitest";
import { parseQuery, queryForScope, SCOPE_PREFIXES } from "./commandScope";

describe("parseQuery", () => {
  test("no prefix searches everything", () => {
    expect(parseQuery("button")).toEqual({ scope: "all", term: "button", prefix: null });
  });

  test("each prefix selects its list and is stripped from the term", () => {
    expect(parseQuery(">settings")).toEqual({ scope: "commands", term: "settings", prefix: ">" });
    expect(parseQuery("@App.tsx")).toEqual({ scope: "files", term: "App.tsx", prefix: "@" });
    expect(parseQuery("#main")).toEqual({ scope: "branches", term: "main", prefix: "#" });
  });

  test("a bare prefix scopes with an empty term, so the whole list shows", () => {
    expect(parseQuery("#")).toEqual({ scope: "branches", term: "", prefix: "#" });
  });

  test("space after the prefix is not part of what is searched for", () => {
    expect(parseQuery(">  settings")).toEqual({ scope: "commands", term: "settings", prefix: ">" });
  });

  test("only a leading prefix counts — this is the branch-name case", () => {
    // `#` here is part of the branch being looked for, not a scope change.
    expect(parseQuery("fix/#123")).toEqual({ scope: "all", term: "fix/#123", prefix: null });
  });

  test("trailing space is kept: the user is still typing", () => {
    expect(parseQuery("main ").term).toBe("main ");
  });

  test("an empty field searches everything", () => {
    expect(parseQuery("")).toEqual({ scope: "all", term: "", prefix: null });
  });
});

describe("queryForScope", () => {
  test("round-trips with parseQuery for every prefixed scope", () => {
    for (const { scope } of SCOPE_PREFIXES) {
      const parsed = parseQuery(queryForScope(scope, "x"));
      expect(parsed.scope).toBe(scope);
      expect(parsed.term).toBe("x");
    }
  });

  test("a scope with no prefix produces a bare field", () => {
    expect(queryForScope("projects")).toBe("");
    expect(queryForScope("all", "hello")).toBe("hello");
  });

  test("defaults to an empty term", () => {
    expect(queryForScope("branches")).toBe("#");
  });
});
