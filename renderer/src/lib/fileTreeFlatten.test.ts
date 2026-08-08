/**
 * The flatten is the virtualizer's contract with FileTree: rows appear exactly as the old
 * recursive render produced them — listing order, children only under an expanded directory,
 * the draft input first in its directory, the empty notice only for a landed-empty non-root
 * listing, and an expanded directory whose lazy listing hasn't landed contributing nothing yet.
 */

import { describe, expect, test } from "vitest";
import { flattenFileTree } from "./fileTreeFlatten";
import type { FileEntry } from "../types/domain";

const dir = (path: string): FileEntry => ({ name: path.split("/").pop() ?? path, path, is_dir: true });
const file = (path: string): FileEntry => ({ name: path.split("/").pop() ?? path, path, is_dir: false });

describe("flattenFileTree", () => {
  test("collapsed directories contribute their row and nothing else", () => {
    const rows = flattenFileTree(new Map([["", [dir("src"), file("README.md")]]]), new Set(), null);
    expect(rows.map((r) => [r.id, r.depth])).toEqual([
      ["src", 0],
      ["README.md", 0],
    ]);
  });

  test("an expanded directory's children follow it, one level deeper, in listing order", () => {
    const rows = flattenFileTree(
      new Map([
        ["", [dir("src"), file("README.md")]],
        ["src", [dir("src/lib"), file("src/main.ts")]],
        ["src/lib", [file("src/lib/a.ts")]],
      ]),
      new Set(["src", "src/lib"]),
      null,
    );
    expect(rows.map((r) => [r.id, r.depth])).toEqual([
      ["src", 0],
      ["src/lib", 1],
      ["src/lib/a.ts", 2],
      ["src/main.ts", 1],
      ["README.md", 0],
    ]);
  });

  test("an expanded directory whose listing hasn't landed yet contributes nothing below itself", () => {
    const rows = flattenFileTree(new Map([["", [dir("src")]]]), new Set(["src"]), null);
    expect(rows).toHaveLength(1);
  });

  test("a landed-empty expanded directory announces itself — but never the root", () => {
    const nested = flattenFileTree(
      new Map([
        ["", [dir("src")]],
        ["src", []],
      ]),
      new Set(["src"]),
      null,
    );
    expect(nested.map((r) => r.kind)).toEqual(["entry", "empty"]);
    expect(nested[1]).toMatchObject({ id: "empty:src", depth: 1 });

    expect(flattenFileTree(new Map([["", []]]), new Set(), null)).toEqual([]);
  });

  test("the draft input renders first in its directory, and replaces the empty notice", () => {
    const rows = flattenFileTree(
      new Map([
        ["", [dir("src")]],
        ["src", []],
      ]),
      new Set(["src"]),
      "src",
    );
    expect(rows.map((r) => r.kind)).toEqual(["entry", "draft"]);
    expect(rows[1]).toMatchObject({ depth: 1 });
  });

  test("a root draft leads the whole list", () => {
    const rows = flattenFileTree(new Map([["", [file("a.ts")]]]), new Set(), "");
    expect(rows.map((r) => r.kind)).toEqual(["draft", "entry"]);
    expect(rows[0]).toMatchObject({ depth: 0 });
  });

  test("expansion state for a directory that is no longer listed is ignored, not an error", () => {
    const rows = flattenFileTree(new Map([["", [file("only.ts")]]]), new Set(["gone"]), null);
    expect(rows.map((r) => r.id)).toEqual(["only.ts"]);
  });
});
