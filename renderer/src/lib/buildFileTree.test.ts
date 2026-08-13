import { describe, expect, test } from "vitest";
import { buildFileTree, type FileTreeDir, type FileTreeNode } from "./buildFileTree";
import type { FileStatusEntry } from "../types/domain";

const entry = (path: string): FileStatusEntry => ({ path, status: "modified" });

/** The shape a row actually shows: its label, and what hangs off it. */
function shape(nodes: FileTreeNode[]): unknown {
  return nodes.map((node) =>
    node.type === "file" ? node.name : { [node.name]: shape(node.children) },
  );
}

const dir = (nodes: FileTreeNode[], index = 0) => nodes[index] as FileTreeDir;

describe("grouping", () => {
  test("files land under the directory that holds them", () => {
    const tree = buildFileTree([entry("src/a.ts"), entry("src/b.ts"), entry("README.md")]);

    // Directories first, then files, each alphabetically.
    expect(shape(tree)).toEqual([{ src: ["a.ts", "b.ts"] }, "README.md"]);
  });

  test("a file at the repository root stays at the top level", () => {
    expect(shape(buildFileTree([entry("README.md")]))).toEqual(["README.md"]);
  });

  test("the entry travels with its file, so a row keeps its status", () => {
    const tree = buildFileTree([entry("src/a.ts")]);
    const file = dir(tree).children[0]!;

    expect(file.type === "file" && file.entry.status).toBe("modified");
  });
});

describe("nesting", () => {
  test("every path segment is its own row, however deep", () => {
    // What a folding pass would have collapsed into one `src/CodeFlow.App/Tickets` row. A row
    // carrying a path reads as a path rather than as a folder you can open, which is the whole
    // reason that pass was taken back out.
    const tree = buildFileTree([entry("src/CodeFlow.App/Tickets/TicketStore.cs")]);

    expect(shape(tree)).toEqual([{ src: [{ "CodeFlow.App": [{ Tickets: ["TicketStore.cs"] }] }] }]);
  });

  test("a directory row is keyed by its own path, not the deepest one", () => {
    // Expansion state and row keys are held by `path`, so each level has to name itself: collapsing
    // `src` must not be indistinguishable from collapsing what is under it.
    const tree = buildFileTree([entry("src/deep/one.ts")]);

    expect(dir(tree).path).toBe("src");
    expect(dir(dir(tree).children).path).toBe("src/deep");
  });

  test("siblings stay apart", () => {
    const tree = buildFileTree([entry("src/a/one.ts"), entry("src/b/two.ts")]);

    expect(shape(tree)).toEqual([{ src: [{ a: ["one.ts"] }, { b: ["two.ts"] }] }]);
  });

  test("a file sits beside the subdirectory it shares a parent with", () => {
    const tree = buildFileTree([entry("src/index.ts"), entry("src/deep/one.ts")]);

    expect(shape(tree)).toEqual([{ src: [{ deep: ["one.ts"] }, "index.ts"] }]);
  });

  test("two files in the same directory share the one row that holds them", () => {
    const tree = buildFileTree([
      entry("src/CodeFlow.App/Tickets/TicketStore.cs"),
      entry("src/CodeFlow.App/Tickets/TicketComment.cs"),
    ]);

    expect(shape(tree)).toEqual([
      { src: [{ "CodeFlow.App": [{ Tickets: ["TicketComment.cs", "TicketStore.cs"] }] }] },
    ]);
  });
});
