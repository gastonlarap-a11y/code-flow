/** One level of nesting, in pixels. */
export const TREE_INDENT = 14;
/** The gutter before a depth-0 row, so the first level is not flush against the panel edge. */
export const TREE_ROW_PAD = 6;

/**
 * Left padding for a row at `depth`.
 *
 * Both trees computed `depth * 14 + 6` — one with the literals inlined at three call sites, the
 * other with them named. Same pixels, two sources, and the drop-line overlay has to agree with the
 * rows it points between or the line lands a few pixels off the indent it is describing.
 */
export function treeIndent(depth: number): number {
  return depth * TREE_INDENT + TREE_ROW_PAD;
}
