import { describe, expect, it } from "vitest";
import { treeIndent, TREE_INDENT, TREE_ROW_PAD } from "./treeIndent";

describe("treeIndent", () => {
  it("pads the top level by the gutter alone", () => {
    expect(treeIndent(0)).toBe(TREE_ROW_PAD);
  });

  it("adds one indent per level", () => {
    expect(treeIndent(1)).toBe(TREE_ROW_PAD + TREE_INDENT);
    expect(treeIndent(3)).toBe(TREE_ROW_PAD + TREE_INDENT * 3);
  });

  // The drop-line overlay indents itself to the row it points between; if the two disagree the
  // line sits a few pixels off the level it claims to describe.
  it("is the same step at every depth", () => {
    expect(treeIndent(5) - treeIndent(4)).toBe(treeIndent(1) - treeIndent(0));
  });
});
