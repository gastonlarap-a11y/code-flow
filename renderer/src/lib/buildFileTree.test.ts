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

describe("compaction", () => {
  test("a chain of single-child directories becomes one row", () => {
    // The case this exists for: four rows to reach one name, and none of the three folders in
    // front of it offered a choice.
    const tree = buildFileTree([entry("src/CodeFlow.App/Tickets/TicketStore.cs")]);

    expect(shape(tree)).toEqual([{ "src/CodeFlow.App/Tickets": ["TicketStore.cs"] }]);
  });

  test("the joined row still points at the deepest directory", () => {
    // Expansion state and row keys are held by `path`; folding must not move it, or collapsing a
    // compacted row would refer to a directory that no row represents.
    const tree = buildFileTree([entry("src/CodeFlow.App/Tickets/TicketStore.cs")]);

    expect(dir(tree).path).toBe("src/CodeFlow.App/Tickets");
  });

  test("a fork in the path is where folding stops", () => {
    const tree = buildFileTree([entry("src/a/one.ts"), entry("src/b/two.ts")]);

    expect(shape(tree)).toEqual([{ src: [{ a: ["one.ts"] }, { b: ["two.ts"] }] }]);
  });

  test("a directory holding a file beside a subdirectory is not folded away", () => {
    // The file is a sibling. Folding here would leave it with nowhere to sit.
    const tree = buildFileTree([entry("src/index.ts"), entry("src/deep/one.ts")]);

    expect(shape(tree)).toEqual([{ src: [{ deep: ["one.ts"] }, "index.ts"] }]);
  });

  test("folding continues below a fork", () => {
    const tree = buildFileTree([entry("src/a/deep/one.ts"), entry("src/b/two.ts")]);

    expect(shape(tree)).toEqual([{ src: [{ "a/deep": ["one.ts"] }, { b: ["two.ts"] }] }]);
  });

  test("two files in the same deep directory keep it as one row", () => {
    const tree = buildFileTree([
      entry("src/CodeFlow.App/Tickets/TicketStore.cs"),
      entry("src/CodeFlow.App/Tickets/TicketComment.cs"),
    ]);

    expect(shape(tree)).toEqual([
      { "src/CodeFlow.App/Tickets": ["TicketComment.cs", "TicketStore.cs"] },
    ]);
  });
});
