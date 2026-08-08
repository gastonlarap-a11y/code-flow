import { memo, Suspense, useMemo, useRef, useState } from "react";
import { lazyRetry } from "../../lib/lazyRetry";
import { Columns2, FileDiff, Rows3 } from "lucide-react";
import { IconButton } from "../common/IconButton";
import type { FileDiffInfo } from "../../types/domain";
import { EmptyState } from "../common/EmptyState";
import { SkeletonRows } from "../common/Skeleton";
import { useT } from "../../state/languageStore";
import { fileStatusLabelKey, fileStatusColor as statusColor } from "../../lib/fileStatus";

// The unified view below is plain DOM; only the split view needs Monaco. Loading it on demand is
// what keeps ~19 MB of editor out of the chunk that paints the commit list, which is the first
// thing the app shows. `.then` rather than a default export: `lazy` wants one, and the codebase
// exports by name everywhere else.
const SplitFileDiff = lazyRetry(() =>
  import("./SplitFileDiff").then((m) => ({ default: m.SplitFileDiff })),
);

type ViewMode = "unified" | "split";

function lineClasses(origin: string): string {
  if (origin === "+") return "bg-[color-mix(in_oklab,var(--cf-success)_14%,transparent)] text-[var(--cf-text)]";
  if (origin === "-") return "bg-[color-mix(in_oklab,var(--cf-danger)_14%,transparent)] text-[var(--cf-text)]";
  return "text-[var(--cf-text-muted)]";
}

/** A compact overview strip along the right edge, in the same spirit as VS Code's overview
 * ruler: one colored tick per added/removed line, positioned proportionally to that line's
 * place in the overall diff, clickable to jump straight there. Only shown in unified mode —
 * the split view already gets Monaco's own overview ruler for free. */
function ChangeMap({
  files,
  containerRef,
}: {
  files: FileDiffInfo[];
  containerRef: React.RefObject<HTMLDivElement | null>;
}) {
  const { totalRows, marks } = useMemo(() => {
    let row = 0;
    const marks: { row: number; color: string }[] = [];
    for (const file of files) {
      row += 1;
      for (const hunk of file.hunks) {
        row += 1;
        for (const line of hunk.lines) {
          if (line.origin === "+") marks.push({ row, color: "var(--cf-success)" });
          else if (line.origin === "-") marks.push({ row, color: "var(--cf-danger)" });
          row += 1;
        }
      }
    }
    return { totalRows: row, marks };
  }, [files]);

  if (totalRows === 0) return null;

  const jumpTo = (e: React.MouseEvent<HTMLDivElement>) => {
    const el = containerRef.current;
    if (!el) return;
    const rect = e.currentTarget.getBoundingClientRect();
    const ratio = Math.min(1, Math.max(0, (e.clientY - rect.top) / rect.height));
    el.scrollTo({ top: ratio * el.scrollHeight, behavior: "smooth" });
  };

  return (
    <div
      onClick={jumpTo}
      className="sticky top-0 h-full w-3 shrink-0 cursor-pointer self-stretch bg-black/[0.02] dark:bg-white/[0.04]"
    >
      <div className="relative h-full w-full">
        {marks.map((m, i) => (
          <div
            key={i}
            className="absolute left-0.5 right-0.5 rounded-[1px]"
            style={{ top: `${(m.row / totalRows) * 100}%`, height: 2, background: m.color }}
          />
        ))}
      </div>
    </div>
  );
}

function DiffViewImpl({ files }: { files: FileDiffInfo[] }) {
  const t = useT();
  const [mode, setMode] = useState<ViewMode>("unified");
  const scrollRef = useRef<HTMLDivElement>(null);

  if (files.length === 0) {
    return <EmptyState icon={FileDiff} title={t("diff.noChanges")} subtitle={t("diff.noChangesHint")} />;
  }

  const modeToggle = (
    <div className="flex items-center gap-0.5 rounded-md border border-[var(--cf-border)] p-0.5">
      <IconButton
        label="diff.unifiedView"
        icon={Rows3}
        active={mode === "unified"}
        onClick={() => setMode("unified")}
      />
      <IconButton
        label="diff.splitView"
        icon={Columns2}
        active={mode === "split"}
        onClick={() => setMode("split")}
      />
    </div>
  );

  if (mode === "split") {
    return (
      <div className="flex h-full">
        <div ref={scrollRef} className="min-w-0 flex-1 overflow-auto">
          <div className="flex items-center justify-end border-b border-[var(--cf-border)] px-3 py-1.5">{modeToggle}</div>
          {/* One boundary for the whole list rather than one per file: the chunk arrives once, and
              a fallback per file would flash a column of skeletons for a single load. */}
          <Suspense fallback={<SkeletonRows count={10} />}>
            <div className="divide-y divide-[var(--cf-border)]">
              {files.map((file, i) => {
                const color = statusColor(file.status);
                return (
                  <div key={i}>
                    <div
                      className="sticky top-0 z-10 flex items-center gap-2 border-b-2 bg-[var(--cf-surface-raised)] px-3 py-2 text-ui font-semibold shadow-sm"
                      style={{ borderBottomColor: color, willChange: "transform", contain: "paint" }}
                    >
                      <span
                        className="rounded px-1.5 py-0.5 text-badge font-bold uppercase tracking-wide"
                        style={{ background: `color-mix(in oklab, ${color} 18%, transparent)`, color }}
                      >
                        {t(fileStatusLabelKey(file.status))}
                      </span>
                      <span className="truncate font-mono text-[var(--cf-text)]">{file.new_path ?? file.old_path}</span>
                    </div>
                    <SplitFileDiff file={file} />
                  </div>
                );
              })}
            </div>
          </Suspense>
        </div>
        <ChangeMap files={files} containerRef={scrollRef} />
      </div>
    );
  }

  return (
    <div className="flex h-full">
      <div ref={scrollRef} className="min-w-0 flex-1 overflow-auto">
        <div className="flex items-center justify-end border-b border-[var(--cf-border)] px-3 py-1.5">{modeToggle}</div>
        <div className="divide-y divide-[var(--cf-border)]">
          {files.map((file, i) => {
            const color = statusColor(file.status);
            return (
              <div key={i}>
                <div
                  className="sticky top-0 z-10 flex items-center gap-2 border-b-2 bg-[var(--cf-surface-raised)] px-3 py-2 text-ui font-semibold shadow-sm"
                  style={{ borderBottomColor: color, willChange: "transform", contain: "paint" }}
                >
                  <span
                    className="rounded px-1.5 py-0.5 text-badge font-bold uppercase tracking-wide"
                    style={{ background: `color-mix(in oklab, ${color} 18%, transparent)`, color }}
                  >
                    {t(fileStatusLabelKey(file.status))}
                  </span>
                  <span className="truncate font-mono text-[var(--cf-text)]">{file.new_path ?? file.old_path}</span>
                </div>
                {file.hunks.map((hunk, hIdx) => (
                  // `select-text` re-enables selection here (the app-wide `body { user-select: none }`
                  // otherwise makes this custom-rendered diff feel like an image). The line-number
                  // gutters keep `select-none`, so a copy grabs the code without the line numbers —
                  // matching what the Monaco-backed split view already allows.
                  <div key={hIdx} className="select-text font-mono text-ui leading-5">
                    <div className="bg-[var(--cf-accent-soft)] px-3 py-1 text-[var(--cf-accent)]">{hunk.header}</div>
                    {hunk.lines.map((line, lIdx) => (
                      <div key={lIdx} className={`flex gap-3 px-3 ${lineClasses(line.origin)}`}>
                        <span className="w-8 shrink-0 select-none text-right text-[var(--cf-text-muted)]">
                          {line.old_lineno ?? ""}
                        </span>
                        <span className="w-8 shrink-0 select-none text-right text-[var(--cf-text-muted)]">
                          {line.new_lineno ?? ""}
                        </span>
                        <span className="whitespace-pre-wrap break-all">
                          {line.origin === "+" || line.origin === "-" ? line.origin : " "}
                          {line.content}
                        </span>
                      </div>
                    ))}
                  </div>
                ))}
              </div>
            );
          })}
        </div>
      </div>
      <ChangeMap files={files} containerRef={scrollRef} />
    </div>
  );
}

/** Memoized on `files` — dragging the diff panel's resize handle only changes the panel's
 * width in the parent (`GraphView`/`ChangesPanel`), which re-renders every drag tick; without
 * this, a large commit's whole line-by-line diff tree (or several Monaco `DiffEditor`s in
 * split mode) would get rebuilt on every pointermove instead of just resizing. */
export const DiffView = memo(DiffViewImpl);
