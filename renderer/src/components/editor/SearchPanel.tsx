import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  CaseSensitive,
  ChevronDown,
  ChevronRight,
  Loader2,
  MoreHorizontal,
  Regex,
  Replace,
  ReplaceAll,
  Search,
  WholeWord,
  X,
} from "lucide-react";
import { IconButton } from "../common/IconButton";
import { replaceInRepo, searchRepo, type SearchHit, type SearchOptions } from "../../lib/ipc/commands";
import { fileIconFor } from "../../lib/fileIcon";
import { useRepoStore } from "../../state/repoStore";
import { confirmAction } from "../../state/confirmStore";
import { pushErrorToast, useToastStore } from "../../state/toastStore";
import { useT } from "../../state/languageStore";

/** Long enough that typing a word doesn't fire a walk of the repo per keystroke, short enough
 * that the results feel like they're keeping up. */
const DEBOUNCE_MS = 250;

/** Find and replace across the project: the same controls an editor's search sidebar has —
 * case, whole word, regex, replace, and include/exclude globs.
 *
 * Replacing writes to files the user may not have open, so it always goes through the backend's
 * checkpoint first: every replace can be undone as a unit from the restore-points list.
 */
export function SearchPanel({
  repoPath,
  onOpenHit,
  onClose,
}: {
  repoPath: string;
  onOpenHit: (path: string, line: number) => void;
  onClose: () => void;
}) {
  const t = useT();
  const [query, setQuery] = useState("");
  const [replacement, setReplacement] = useState("");
  const [showReplace, setShowReplace] = useState(false);
  const [showFilters, setShowFilters] = useState(false);
  const [caseSensitive, setCaseSensitive] = useState(false);
  const [wholeWord, setWholeWord] = useState(false);
  const [useRegex, setUseRegex] = useState(false);
  const [include, setInclude] = useState("");
  const [exclude, setExclude] = useState("");
  const [hits, setHits] = useState<SearchHit[]>([]);
  const [truncated, setTruncated] = useState(false);
  const [searching, setSearching] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({});
  const [replacing, setReplacing] = useState(false);
  // Only the newest search may write results: a slow query for "a" must not land on top of the
  // fast one for "authenticate" the user has already typed.
  const runRef = useRef(0);

  const options: SearchOptions = useMemo(
    () => ({ caseSensitive, wholeWord, regex: useRegex, include, exclude }),
    [caseSensitive, wholeWord, useRegex, include, exclude],
  );

  const run = useCallback(
    async (text: string, current: SearchOptions) => {
      const token = ++runRef.current;
      if (!text.trim()) {
        setHits([]);
        setTruncated(false);
        setSearching(false);
        setError(null);
        return;
      }
      setSearching(true);
      try {
        const outcome = await searchRepo(repoPath, text, current);
        if (token !== runRef.current) return;
        setHits(outcome.hits);
        setTruncated(outcome.truncated);
        setError(null);
      } catch (e) {
        if (token !== runRef.current) return;
        setHits([]);
        setTruncated(false);
        // A half-typed regex lands here on nearly every keystroke, so it's shown inline rather
        // than as a toast.
        setError(String(e));
      } finally {
        if (token === runRef.current) setSearching(false);
      }
    },
    [repoPath],
  );

  useEffect(() => {
    const id = setTimeout(() => void run(query, options), DEBOUNCE_MS);
    return () => clearTimeout(id);
  }, [query, options, run]);

  const grouped = useMemo(() => {
    const byFile = new Map<string, SearchHit[]>();
    for (const hit of hits) {
      const existing = byFile.get(hit.path);
      if (existing) existing.push(hit);
      else byFile.set(hit.path, [hit]);
    }
    return [...byFile.entries()];
  }, [hits]);

  const replace = async (onlyPath?: string) => {
    const scope = onlyPath ?? null;
    const confirmed = await confirmAction(
      scope
        ? t("editor.confirmReplaceFile", { file: scope })
        : t("editor.confirmReplaceAll", { n: hits.length, files: grouped.length }),
      true,
    );
    if (!confirmed) return;
    setReplacing(true);
    try {
      const outcome = await replaceInRepo(repoPath, query, replacement, options, scope);
      useToastStore
        .getState()
        .pushToast(t("editor.replaced", { n: outcome.replacements, files: outcome.files }), "success");
      // The files changed underneath the rest of the app; the status and diffs it shows are now
      // stale, and the results list has to be rebuilt against the new content.
      void useRepoStore.getState().refreshAll();
      await run(query, options);
    } catch (e) {
      pushErrorToast(String(e));
    } finally {
      setReplacing(false);
    }
  };

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="shrink-0 border-b border-[var(--cf-border)] p-1.5">
        <div className="flex items-start gap-1">
          {/* The chevron that folds the replace row open, exactly where editors put it. */}
          <IconButton
            label="editor.toggleReplace"
            icon={showReplace ? ChevronDown : ChevronRight}
            active={showReplace}
            className="mt-0.5 shrink-0"
            onClick={() => setShowReplace((v) => !v)}
          />

          <div className="min-w-0 flex-1 space-y-1">
            <div className="flex items-center gap-1 rounded-md border border-[var(--cf-border)] bg-[var(--cf-bg)] px-1.5">
              <Search size={11} className="shrink-0 text-[var(--cf-text-muted)]" />
              <input
                autoFocus
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                onKeyDown={(e) => e.key === "Escape" && onClose()}
                placeholder={t("editor.searchPlaceholder")}
                className="min-w-0 flex-1 bg-transparent py-1 text-ui outline-none"
              />
              <IconButton
                label="editor.matchCase"
                icon={CaseSensitive}
                active={caseSensitive}
                className="shrink-0"
                onClick={() => setCaseSensitive((v) => !v)}
              />
              <IconButton
                label="editor.wholeWord"
                icon={WholeWord}
                active={wholeWord}
                className="shrink-0"
                onClick={() => setWholeWord((v) => !v)}
              />
              <IconButton
                label="editor.useRegex"
                icon={Regex}
                active={useRegex}
                className="shrink-0"
                onClick={() => setUseRegex((v) => !v)}
              />
            </div>

            {showReplace && (
              <div className="flex items-center gap-1 rounded-md border border-[var(--cf-border)] bg-[var(--cf-bg)] px-1.5">
                <Replace size={11} className="shrink-0 text-[var(--cf-text-muted)]" />
                <input
                  value={replacement}
                  onChange={(e) => setReplacement(e.target.value)}
                  placeholder={useRegex ? t("editor.replacePlaceholderRegex") : t("editor.replacePlaceholder")}
                  className="min-w-0 flex-1 bg-transparent py-1 text-ui outline-none"
                />
                <IconButton
                  label="editor.replaceAll"
                  icon={ReplaceAll}
                  pending={replacing}
                  disabled={hits.length === 0}
                  className="shrink-0"
                  onClick={() => void replace()}
                />
              </div>
            )}

            {showFilters && (
              <div className="space-y-1">
                <input
                  value={include}
                  onChange={(e) => setInclude(e.target.value)}
                  placeholder={t("editor.filesToInclude")}
                  className="w-full rounded-md border border-[var(--cf-border)] bg-[var(--cf-bg)] px-1.5 py-1 font-mono text-badge outline-none"
                />
                <input
                  value={exclude}
                  onChange={(e) => setExclude(e.target.value)}
                  placeholder={t("editor.filesToExclude")}
                  className="w-full rounded-md border border-[var(--cf-border)] bg-[var(--cf-bg)] px-1.5 py-1 font-mono text-badge outline-none"
                />
              </div>
            )}
          </div>

          <div className="mt-0.5 flex shrink-0 flex-col items-center gap-0.5">
            <IconButton label="editor.closeSearch" icon={X} onClick={onClose} />
            <IconButton
              label="editor.toggleFilters"
              icon={MoreHorizontal}
              active={showFilters || Boolean(include || exclude)}
              onClick={() => setShowFilters((v) => !v)}
            />
          </div>
        </div>
      </div>

      <div className="min-h-0 flex-1 overflow-auto">
        {error && <p className="px-3 py-2 text-badge text-[var(--cf-danger)]">{error}</p>}
        {searching && (
          <div className="flex items-center gap-1.5 px-3 py-2 text-badge text-[var(--cf-text-muted)]">
            <Loader2 size={11} className="animate-spin" />
            {t("editor.searching")}
          </div>
        )}
        {!searching && !error && query.trim() && hits.length === 0 && (
          <p className="px-3 py-2 text-badge text-[var(--cf-text-muted)]">{t("editor.noMatches")}</p>
        )}
        {!searching && hits.length > 0 && (
          <p className="px-3 py-1.5 text-badge text-[var(--cf-text-muted)]">
            {t("editor.matchCount", { hits: hits.length, files: grouped.length })}
            {truncated ? ` · ${t("editor.searchTruncated")}` : ""}
          </p>
        )}
        {grouped.map(([path, fileHits]) => {
          const { Icon, color } = fileIconFor(path);
          const isCollapsed = collapsed[path];
          return (
            <div key={path} className="group/file pb-1">
              <div className="flex items-center gap-1 px-1.5 py-0.5">
                <button
                  onClick={() => setCollapsed((c) => ({ ...c, [path]: !c[path] }))}
                  aria-expanded={!isCollapsed}
                  className="cf-focusable flex h-6 min-w-0 flex-1 items-center gap-1.5 text-left"
                >
                  {isCollapsed ? (
                    <ChevronRight size={10} className="shrink-0 text-[var(--cf-text-muted)]" />
                  ) : (
                    <ChevronDown size={10} className="shrink-0 text-[var(--cf-text-muted)]" />
                  )}
                  <Icon size={12} className="shrink-0" style={{ color }} />
                  <span className="truncate text-badge text-[var(--cf-text)]">{path}</span>
                  <span className="shrink-0 text-badge text-[var(--cf-text-muted)]">{fileHits.length}</span>
                </button>
                {showReplace && (
                  <IconButton
                    label="editor.replaceInFile"
                    icon={Replace}
                    disabled={replacing}
                    className="shrink-0 opacity-55 group-hover/file:opacity-100 group-focus-within/file:opacity-100"
                    onClick={() => void replace(path)}
                  />
                )}
              </div>
              {!isCollapsed &&
                fileHits.map((hit) => (
                  <button
                    key={`${hit.path}:${hit.line_no}`}
                    onClick={() => onOpenHit(hit.path, hit.line_no)}
                    className="flex w-full items-start gap-2 rounded px-2 py-0.5 pl-7 text-left hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
                  >
                    <span className="shrink-0 font-mono text-badge text-[var(--cf-text-muted)]">{hit.line_no}</span>
                    <span className="truncate font-mono text-badge text-[var(--cf-text-muted)]">
                      {hit.line.trim()}
                    </span>
                  </button>
                ))}
            </div>
          );
        })}
      </div>
    </div>
  );
}
