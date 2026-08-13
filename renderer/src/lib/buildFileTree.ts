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
 * Folds a directory that holds nothing but one other directory into its child.
 *
 * `src` → `CodeFlow.App` → `Tickets` → one file is four rows to reach one name, and the three
 * folders in front of it carry no choice: there is nothing else in them to pick. Joining them into
 * `src/CodeFlow.App/Tickets` is what VS Code's explorer does, and in a repository whose paths run
 * this deep it is the difference between a usable tree and an indented list.
 *
 * A directory with a file in it is never folded, even alongside one subdirectory — the file is a
 * sibling that would lose its place. Folding also stops at a directory with two children, since
 * that one is a real fork in the path.
 */
function compact(dir: FileTreeDir): FileTreeDir {
  let folded = dir;

  while (folded.children.length === 1 && folded.children[0]!.type === "dir") {
    const only = folded.children[0] as FileTreeDir;
    // The joined name is what the row shows; the path stays the deepest one, so expansion state,
    // keys and anything that later resolves a row back to a directory keep working unchanged.
    folded = { type: "dir", name: `${folded.name}/${only.name}`, path: only.path, children: only.children };
  }

  return { ...folded, children: folded.children.map((c) => (c.type === "dir" ? compact(c) : c)) };
}

/**
 * Groups a flat list of repo-relative paths into a nested directory tree.
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

  // Compaction runs after sorting, not before: it changes names (`src` becomes
  // `src/CodeFlow.App/Tickets`) and sorting on the joined name would order the tree by a string
  // that only exists once the folding has happened.
  return root.children.map((node) => (node.type === "dir" ? compact(node) : node));
}
