import { useEffect, useMemo, useRef, useState } from "react";
import { PickerModal } from "../common/PickerModal";
import { listRepoFiles } from "../../lib/ipc/commands";
import { fileIconFor } from "../../lib/fileIcon";
import { useT } from "../../state/languageStore";

/** How many rows the list renders. Filtering happens over the whole repo; only the top slice is
 * drawn, because nobody scrolls a thousand results — they type two more letters. */
const MAX_ROWS = 40;

/** Subsequence match, the way editors' quick-open works: "edvw" finds `EditorView.tsx`. Returns
 * a score (lower is better) or `null` when the query doesn't fit at all. */
function fuzzyScore(path: string, query: string): number | null {
  if (!query) return 0;
  const haystack = path.toLowerCase();
  const name = haystack.slice(haystack.lastIndexOf("/") + 1);

  // A plain substring of the *filename* is what the user almost always means, so it outranks any
  // subsequence spread across the directories.
  const inName = name.indexOf(query);
  if (inName >= 0) return inName;
  const inPath = haystack.indexOf(query);
  if (inPath >= 0) return 100 + inPath;

  let cursor = 0;
  let gaps = 0;
  for (const char of query) {
    const found = haystack.indexOf(char, cursor);
    if (found < 0) return null;
    gaps += found - cursor;
    cursor = found + 1;
  }
  return 1000 + gaps;
}

/** Quick-open: type part of a path, hit Enter, the file opens in a pinned tab. */
export function FilePalette({
  repoPath,
  onPick,
  onClose,
}: {
  repoPath: string;
  onPick: (path: string) => void;
  onClose: () => void;
}) {
  const t = useT();
  const [files, setFiles] = useState<string[] | null>(null);
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const listRef = useRef<HTMLDivElement>(null);

  // Re-read on every open rather than caching: files appear and vanish between opens, and one
  // walk of a repo is fast enough that a stale list is the worse trade.
  useEffect(() => {
    let cancelled = false;
    void listRepoFiles(repoPath)
      .then((result) => {
        if (!cancelled) setFiles(result);
      })
      .catch(() => {
        if (!cancelled) setFiles([]);
      });
    return () => {
      cancelled = true;
    };
  }, [repoPath]);

  const matches = useMemo(() => {
    if (!files) return [];
    const needle = query.trim().toLowerCase();
    const scored: { path: string; score: number }[] = [];
    for (const path of files) {
      const score = fuzzyScore(path, needle);
      if (score !== null) scored.push({ path, score });
    }
    scored.sort((a, b) => a.score - b.score || a.path.length - b.path.length);
    return scored.slice(0, MAX_ROWS).map((s) => s.path);
  }, [files, query]);

  useEffect(() => {
    setActive(0);
  }, [query]);

  useEffect(() => {
    listRef.current?.querySelector('[data-active="true"]')?.scrollIntoView({ block: "nearest" });
  }, [active]);

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Escape") {
      e.preventDefault();
      onClose();
    } else if (e.key === "ArrowDown") {
      e.preventDefault();
      setActive((i) => Math.min(i + 1, matches.length - 1));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setActive((i) => Math.max(i - 1, 0));
    } else if (e.key === "Enter") {
      e.preventDefault();
      const picked = matches[active];
      if (picked) {
        onPick(picked);
        onClose();
      }
    }
  };

  return (
    <PickerModal
      placeholder={t("editor.goToFilePlaceholder")}
      value={query}
      onValueChange={setQuery}
      onKeyDown={onKeyDown}
      size="lg"
      listRef={listRef}
      onClose={onClose}
    >
      {files === null ? (
        <p className="px-2 py-2 text-ui text-[var(--cf-text-muted)]">{t("editor.loading")}</p>
      ) : matches.length === 0 ? (
        <p className="px-2 py-2 text-ui text-[var(--cf-text-muted)]">{t("titlebar.noResults")}</p>
      ) : (
        matches.map((path, index) => {
          const { Icon, color } = fileIconFor(path);
          const name = path.slice(path.lastIndexOf("/") + 1);
          const dir = path.slice(0, path.length - name.length - 1);
          return (
            <button
              key={path}
              data-active={index === active}
              onMouseEnter={() => setActive(index)}
              onClick={() => {
                onPick(path);
                onClose();
              }}
              className={`cf-focusable cf-interactive flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left ${
                index === active ? "bg-[var(--cf-accent-soft)]" : ""
              }`}
            >
              <Icon size={14} className="shrink-0" style={{ color }} aria-hidden />
              <span className="shrink-0 text-body text-[var(--cf-text)]">{name}</span>
              <span className="truncate text-badge text-[var(--cf-text-muted)]">{dir}</span>
            </button>
          );
        })
      )}
    </PickerModal>
  );
}
