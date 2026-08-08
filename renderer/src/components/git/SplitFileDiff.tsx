import { useMemo } from "react";
import { DiffEditor } from "../../lib/monacoEditor";
import type { FileDiffInfo } from "../../types/domain";
import { useThemeStore } from "../../state/themeStore";
import { languageForPath } from "../../lib/monacoLanguage";

// Split out of `DiffView` so it can be reached through `lazy()`: this is the only half of the diff
// that needs Monaco, and it is not the half anyone sees first. `DiffView` opens in unified mode,
// which is plain DOM — keeping the two in one file is what put ~19 MB of editor in the chunk that
// paints the commit list.

/** Rebuilds the two full-text sides of a file's diff from its hunks — the diff commands
 * already run with (near-)unlimited context lines, so for anything but a huge commit-view
 * diff this reproduces the whole original/modified file, which is what the side-by-side
 * Monaco DiffEditor needs (it diffs two full texts itself, not a hunk list). */
function reconstructSides(file: FileDiffInfo): { original: string; modified: string } {
  const original: string[] = [];
  const modified: string[] = [];
  for (const hunk of file.hunks) {
    for (const line of hunk.lines) {
      if (line.origin === "-") original.push(line.content);
      else if (line.origin === "+") modified.push(line.content);
      else {
        original.push(line.content);
        modified.push(line.content);
      }
    }
  }
  return { original: original.join("\n"), modified: modified.join("\n") };
}

const MIN_SPLIT_HEIGHT = 120;
const MAX_SPLIT_HEIGHT = 640;
const SPLIT_LINE_HEIGHT = 19;

export function SplitFileDiff({ file }: { file: FileDiffInfo }) {
  const monacoTheme = useThemeStore((s) => s.monacoTheme);
  const { original, modified } = useMemo(() => reconstructSides(file), [file]);
  const path = file.new_path ?? file.old_path ?? "";
  const lineCount = Math.max(original.split("\n").length, modified.split("\n").length);
  const height = Math.min(MAX_SPLIT_HEIGHT, Math.max(MIN_SPLIT_HEIGHT, lineCount * SPLIT_LINE_HEIGHT + 24));

  return (
    <DiffEditor
      height={height}
      language={languageForPath(path)}
      original={original}
      modified={modified}
      theme={monacoTheme}
      options={{
        readOnly: true,
        fontSize: 13,
        renderSideBySide: true,
        // Monaco silently collapses side-by-side into a unified-looking layout below ~900px
        // wide (e.g. inside a modal) unless told not to — the whole point of this toggle is
        // an actual two-pane view, so never let it fall back on its own.
        useInlineViewWhenSpaceIsLimited: false,
        automaticLayout: true,
      }}
    />
  );
}
