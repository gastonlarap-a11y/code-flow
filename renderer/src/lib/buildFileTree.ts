import type { FileStatusEntry } from "../types/domain";

export interface FileTreeDir {
  type: "dir";
  name: string;
  path: string;
  children: FileTreeNode[];
}

export interface FileTreeFile {
  type: "file";
  name: string;
  entry: FileStatusEntry;
}

export type FileTreeNode = FileTreeDir | FileTreeFile;

function sortDir(dir: FileTreeDir) {
  dir.children.sort((a, b) => {
    if (a.type !== b.type) return a.type === "dir" ? -1 : 1;
    return a.name.localeCompare(b.name);
  });
  for (const child of dir.children) {
    if (child.type === "dir") sortDir(child);
  }
}

/**
 * Groups a flat list of repo-relative paths into a nested directory tree.
 *
 * **One row per path segment, and no folding.** A pass that joined single-child chains into
 * `renderer/src` was tried and taken back out: one row carrying a path reads as a path, not as a
 * folder you can open, which defeats the point of having a tree at all. Every folder gets its own
 * row and opens into the next one.
 *
 * The Changes tab's tree view is the only caller, and the only one it can have: the file explorer
 * builds its tree from a different shape entirely — a `Map` of directory to the listing fetched
 * when that directory was expanded, flattened by `fileTreeFlatten.ts`. It has "not loaded yet" and
 * "loaded and empty" as distinct states; this has neither, because it is handed the whole set at
 * once. The comment that used to sit here claimed both wanted this shape, which sent a reader
 * looking for a second caller that has never existed.
 */
export function buildFileTree(entries: FileStatusEntry[]): FileTreeNode[] {
  const root: FileTreeDir = { type: "dir", name: "", path: "", children: [] };
  for (const entry of entries) {
    const parts = entry.path.split("/").filter(Boolean);
    let current = root;
    for (let i = 0; i < parts.length - 1; i++) {
      const name = parts[i]!;
      const path = parts.slice(0, i + 1).join("/");
      let dir = current.children.find((c) => c.type === "dir" && c.name === name) as FileTreeDir | undefined;
      if (!dir) {
        dir = { type: "dir", name, path, children: [] };
        current.children.push(dir);
      }
      current = dir;
    }
    const name = parts[parts.length - 1] ?? entry.path;
    current.children.push({ type: "file", name, entry });
  }
  sortDir(root);
  return root.children;
}
