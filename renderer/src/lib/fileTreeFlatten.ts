/**
 * Turns FileTree's recursive state — lazily-loaded listings per directory plus an expansion set —
 * into the flat list of visible rows the virtualizer renders.
 *
 * The walk mirrors the old recursive `TreeNode` render exactly: the in-progress "new file" input
 * renders first inside its target directory, a directory's children appear only while it is
 * expanded in listing order, an expanded directory whose listing landed empty announces itself
 * (the root never does), and one whose listing hasn't landed yet contributes nothing until it
 * does.
 */

import type { FileEntry } from "../types/domain";

export type FileTreeRow =
  | { kind: "entry"; id: string; entry: FileEntry; depth: number }
  /** The inline new-file/new-folder input, keyed "draft" — at most one exists. */
  | { kind: "draft"; id: "draft"; depth: number }
  /** The "empty directory" notice under an expanded directory with a landed, empty listing. */
  | { kind: "empty"; id: string; depth: number };

export function flattenFileTree(
  childrenByDir: ReadonlyMap<string, FileEntry[]>,
  expanded: ReadonlySet<string>,
  /** The directory holding the in-progress draft input ("" = repo root), or null. */
  draftParent: string | null,
): FileTreeRow[] {
  const rows: FileTreeRow[] = [];
  const walk = (dir: string, depth: number) => {
    if (draftParent === dir) rows.push({ kind: "draft", id: "draft", depth });
    const children = childrenByDir.get(dir);
    for (const entry of children ?? []) {
      rows.push({ kind: "entry", id: entry.path, entry, depth });
      if (entry.is_dir && expanded.has(entry.path)) walk(entry.path, depth + 1);
    }
    if (dir !== "" && children !== undefined && children.length === 0 && draftParent !== dir) {
      rows.push({ kind: "empty", id: `empty:${dir}`, depth });
    }
  };
  walk("", 0);
  return rows;
}
