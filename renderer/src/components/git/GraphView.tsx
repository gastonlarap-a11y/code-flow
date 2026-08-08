import { Fragment, memo, useMemo } from "react";
import { computeGraphLayout, laneColor } from "../../lib/graphLayout";
import { useRepoStore } from "../../state/repoStore";
import { useLayoutStore } from "../../state/layoutStore";
import { confirmAction } from "../../state/confirmStore";
import { DiffView } from "./DiffView";
import { EmptyState } from "../common/EmptyState";
import { ResizeHandle } from "../common/ResizeHandle";
import { ChevronDown, ChevronRight, History, RotateCcw, X } from "lucide-react";
import { IconButton } from "../common/IconButton";
import { Tooltip } from "../common/Tooltip";
import { CARD } from "../common/panelChrome";
import { useT } from "../../state/languageStore";
import { Skeleton, SkeletonRows } from "../common/Skeleton";
import { fileStatusColor, fileStatusLabelKey } from "../../lib/fileStatus";
import type { CommitFileInfo } from "../../types/domain";

const ROW_HEIGHT = 30;
const LANE_WIDTH = 16;
const DOT_RADIUS = 4;
const DIFF_MIN = 280;
const DIFF_MAX = 900;
const COL_MIN = 50;
const COL_MAX = 600;
const COLUMN_GAP = 8; // matches Tailwind gap-2
/** Every state of an expanded commit is a whole number of these, which is what lets the graph
 * SVG — laid out by row index, not by measuring the DOM — stay aligned once rows are inserted
 * into the middle of the list. Never let a file row wrap or grow. */
const FILE_ROW_HEIGHT = 24;
const SKELETON_FILE_ROWS = 3;

/** The key a file is selected by, matching what `repoStore.selectCommitFile` stores. */
function filePath(file: CommitFileInfo): string {
  return file.new_path ?? file.old_path ?? "";
}

function formatDate(ts: number): string {
  const d = new Date(ts * 1000);
  return d.toLocaleDateString(undefined, { month: "short", day: "numeric" });
}

function formatFullDateTime(ts: number): string {
  const d = new Date(ts * 1000);
  return d.toLocaleString(undefined, { dateStyle: "full", timeStyle: "short" });
}

/** The files an expanded commit touched, listed under its row. Picking one is what fetches a
 * diff — the list itself carries no content at all (GIT-035).
 *
 * Its total height must be exactly `expansionHeight` as `CommitTable` computes it, so every
 * branch here is a whole number of `FILE_ROW_HEIGHT` rows. */
function CommitFileRows({ id, width }: { id: string; width: number }) {
  const files = useRepoStore((s) => s.commitFiles);
  const loading = useRepoStore((s) => s.commitFilesLoading);
  const selected = useRepoStore((s) => s.selectedCommitFile);
  const selectCommitFile = useRepoStore((s) => s.selectCommitFile);
  const t = useT();

  if (loading) {
    return (
      <div id={id} style={{ width }}>
        {Array.from({ length: SKELETON_FILE_ROWS }).map((_, i) => (
          <div key={i} style={{ height: FILE_ROW_HEIGHT }} className="flex items-center pl-9 pr-3">
            <Skeleton className="h-3" style={{ width: `${45 + ((i * 17) % 35)}%` }} />
          </div>
        ))}
      </div>
    );
  }

  if (files.length === 0) {
    return (
      <div
        id={id}
        style={{ width, height: FILE_ROW_HEIGHT }}
        className="flex items-center pl-9 pr-3 text-ui text-[var(--cf-text-muted)]"
      >
        {t("graph.noFilesInCommit")}
      </div>
    );
  }

  return (
    <div id={id} style={{ width }}>
      {files.map((file) => {
        const path = filePath(file);
        const isSelected = path === selected;
        return (
          <button
            key={path}
            onClick={() => void selectCommitFile(file)}
            aria-pressed={isSelected}
            style={{ height: FILE_ROW_HEIGHT }}
            className={`cf-focusable flex w-full items-center gap-2 pl-9 pr-3 text-left ${
              isSelected ? "bg-[var(--cf-accent-soft)]" : "hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
            }`}
          >
            {/* The status letter is the one thing on this row that needs decoding — same
                treatment it gets in the Changes panel. */}
            <Tooltip label={t(fileStatusLabelKey(file.status))}>
              <span
                className="w-4 shrink-0 text-center text-badge font-semibold uppercase"
                style={{ color: fileStatusColor(file.status) }}
              >
                {file.status[0]}
              </span>
            </Tooltip>
            <span className="min-w-0 flex-1 truncate font-mono text-ui text-[var(--cf-text)]">{path}</span>
          </button>
        );
      })}
    </div>
  );
}

/** Everything left of the diff panel: sticky column headers + the commit rows/graph SVG.
 * Memoized (and reading its own store slices rather than taking props) so dragging the diff
 * panel's resize handle — which only touches `graphDiffWidth` — doesn't force this
 * potentially long commit list to re-render on every pointermove tick. */
const CommitTable = memo(function CommitTable() {
  const commits = useRepoStore((s) => s.commits);
  const commitsLoading = useRepoStore((s) => s.commitsLoading);
  const branches = useRepoStore((s) => s.branches);
  const selectedCommitId = useRepoStore((s) => s.selectedCommitId);
  const commitFileCount = useRepoStore((s) => s.commitFiles.length);
  const commitFilesLoading = useRepoStore((s) => s.commitFilesLoading);
  const selectCommit = useRepoStore((s) => s.selectCommit);
  const undoCommit = useRepoStore((s) => s.undoCommit);
  const colHash = useLayoutStore((s) => s.sizes.graphColHash);
  const colDate = useLayoutStore((s) => s.sizes.graphColDate);
  const colAuthor = useLayoutStore((s) => s.sizes.graphColAuthor);
  const colMessage = useLayoutStore((s) => s.sizes.graphColMessage);
  const colRefs = useLayoutStore((s) => s.sizes.graphColRefs);
  const setSize = useLayoutStore((s) => s.setSize);
  const commitSize = useLayoutStore((s) => s.commitSize);
  const t = useT();

  const layout = useMemo(() => computeGraphLayout(commits), [commits]);
  const headCommitId = branches.find((b) => b.is_head)?.target ?? null;

  if (commits.length === 0 && commitsLoading) {
    return <SkeletonRows count={12} className="cf-fade-in" />;
  }

  if (commits.length === 0) {
    return <EmptyState icon={History} title={t("graph.noCommits")} subtitle={t("graph.noCommitsHint")} />;
  }

  // Left-to-right order: Commit, Date, Author, Message, Refs, then the lane graph —
  // keeping the graph fixed-width and last avoids it colliding with the sticky header
  // when the row is very wide, and every text column has a known pixel width so the
  // graph's offset can be computed exactly instead of relying on flex measurement.
  const columns = [
    { key: "graphColHash" as const, width: colHash, label: t("graph.colCommit") },
    { key: "graphColDate" as const, width: colDate, label: t("graph.colDate") },
    { key: "graphColAuthor" as const, width: colAuthor, label: t("graph.colAuthor") },
    { key: "graphColMessage" as const, width: colMessage, label: t("graph.colMessage") },
    { key: "graphColRefs" as const, width: colRefs, label: t("graph.colRefs") },
  ];
  const textColumnsWidth = columns.reduce((sum, c) => sum + c.width, 0) + COLUMN_GAP * columns.length;

  // Expanding a commit inserts file rows into the middle of a list the SVG places by index, so
  // every row below the expanded one has to be pushed down by exactly the height those rows take.
  // `CommitFileRows` guarantees that height by keeping each of its states a whole number of
  // `FILE_ROW_HEIGHT` rows — measuring the DOM instead would mean a frame of misaligned edges.
  const expandedRow = selectedCommitId
    ? (layout.rows.find((r) => r.commit.id === selectedCommitId)?.row ?? null)
    : null;
  const fileRowCount = commitFilesLoading ? SKELETON_FILE_ROWS : Math.max(commitFileCount, 1);
  const expansionHeight = expandedRow === null ? 0 : fileRowCount * FILE_ROW_HEIGHT;

  const svgWidth = layout.laneCount * LANE_WIDTH + 12;
  const svgHeight = layout.rows.length * ROW_HEIGHT + expansionHeight;
  // Coordinates local to the graph SVG itself, which is offset by textColumnsWidth via `left`.
  const laneX = (lane: number) => lane * LANE_WIDTH + LANE_WIDTH / 2;
  const rowY = (row: number) =>
    row * ROW_HEIGHT + (expandedRow !== null && row > expandedRow ? expansionHeight : 0) + ROW_HEIGHT / 2;
  const totalWidth = textColumnsWidth + svgWidth;

  return (
    <div className="flex-1 overflow-auto">
      <div
        className="sticky top-0 z-10 flex h-6 min-w-full items-center gap-2 border-b border-[var(--cf-border)] bg-[var(--cf-surface)] px-3 text-badge"
        style={{ width: totalWidth + 24, willChange: "transform", contain: "paint" }}
      >
        {columns.map((col) => (
          <div key={col.key} style={{ width: col.width }} className="flex shrink-0 items-center">
            <span className="min-w-0 flex-1 truncate text-center text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
              {col.label}
            </span>
            <ResizeHandle
              axis="x"
              value={col.width}
              min={COL_MIN}
              max={COL_MAX}
              onChange={(w) => setSize(col.key, w)}
              onCommit={(w) => commitSize(col.key, w)}
            />
          </div>
        ))}
        {/* GRAFO has no fixed width — `flex-1` lets it absorb whatever space is left so the column
            always fills to the panel's right edge (with `min-w-full` above keeping the whole header
            at least as wide as the panel), instead of ending at content width and looking cut off. */}
        <span className="flex-1 text-center text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
          {t("graph.colGraph")}
        </span>
      </div>

      <div className="relative min-w-full" style={{ width: totalWidth + 24, minHeight: svgHeight }}>
        <svg
          width={svgWidth}
          height={svgHeight}
          style={{ left: textColumnsWidth, top: 0 }}
          className="pointer-events-none absolute"
        >
          {layout.edges.map((edge, i) => {
            const x1 = laneX(edge.fromLane);
            const y1 = rowY(edge.fromRow);
            const x2 = laneX(edge.toLane);
            const y2 = rowY(edge.toRow);
            const color = laneColor(edge.fromLane);
            if (x1 === x2) {
              return <line key={i} x1={x1} y1={y1} x2={x2} y2={y2} stroke={color} strokeWidth={2} />;
            }
            const midY = (y1 + y2) / 2;
            return (
              <path
                key={i}
                d={`M ${x1} ${y1} C ${x1} ${midY}, ${x2} ${midY}, ${x2} ${y2}`}
                stroke={color}
                strokeWidth={2}
                fill="none"
              />
            );
          })}
          {layout.rows.map((r) => (
            <circle key={r.commit.id} cx={laneX(r.lane)} cy={rowY(r.row)} r={DOT_RADIUS} fill={laneColor(r.lane)} />
          ))}
        </svg>

        <div>
          {layout.rows.map((r) => {
            const isSelected = r.commit.id === selectedCommitId;
            const isHead = r.commit.id === headCommitId;
            const filesId = `commit-files-${r.commit.id}`;
            const Chevron = isSelected ? ChevronDown : ChevronRight;
            return (
              <Fragment key={r.commit.id}>
              <div
                style={{ height: ROW_HEIGHT }}
                className={`group flex w-full items-center gap-2 px-3 text-body ${
                  isSelected ? "bg-[var(--cf-accent-soft)]" : "hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
                }`}
              >
                <button
                  onClick={() => void selectCommit(isSelected ? null : r.commit.id)}
                  aria-expanded={isSelected}
                  aria-controls={isSelected ? filesId : undefined}
                  style={{ width: textColumnsWidth }}
                  className="cf-focusable flex h-6 shrink-0 items-center gap-2 text-left"
                >
                  {/* Inside the Commit column rather than before it: a chevron of its own would
                      shift every row right and leave the sticky headers pointing at nothing. */}
                  <span style={{ width: colHash }} className="flex shrink-0 items-center gap-1">
                    <Chevron size={14} aria-hidden className="shrink-0 text-[var(--cf-text-muted)]" />
                    <span className="min-w-0 truncate font-mono text-badge text-[var(--cf-text-muted)]">
                      {r.commit.short_id}
                    </span>
                  </span>
                  <Tooltip label={formatFullDateTime(r.commit.timestamp)}>
                    <span style={{ width: colDate }} className="shrink-0 truncate text-[var(--cf-text-muted)]">
                      {formatDate(r.commit.timestamp)}
                    </span>
                  </Tooltip>
                  <span style={{ width: colAuthor }} className="shrink-0 truncate text-[var(--cf-text-muted)]">
                    {r.commit.author_name}
                  </span>
                  <span style={{ width: colMessage }} className="shrink-0 truncate text-[var(--cf-text)]">
                    {r.commit.summary}
                  </span>
                  <span style={{ width: colRefs }} className="flex shrink-0 gap-1 overflow-hidden">
                    {r.commit.refs.slice(0, 2).map((ref) => (
                      <span
                        key={ref}
                        className="truncate rounded px-1.5 py-0.5 text-badge font-medium"
                        style={{
                          background: "var(--cf-accent-soft)",
                          color: "var(--cf-accent)",
                        }}
                      >
                        {ref}
                      </span>
                    ))}
                  </span>
                </button>
                {isHead && r.commit.parent_ids.length > 0 && (
                  /* Was `hidden group-hover:block`: undoing the last commit could only be found by
                     resting the pointer on that one row. Dimmed and always there now. */
                  <IconButton
                    label="graph.undoCommit"
                    icon={RotateCcw}
                    variant="danger"
                    className="shrink-0 opacity-55 group-hover:opacity-100 group-focus-within:opacity-100"
                    onClick={async (e: React.MouseEvent) => {
                      e.stopPropagation();
                      if (await confirmAction(t("graph.undoConfirm"))) {
                        void undoCommit(r.commit.id);
                      }
                    }}
                  />
                )}
              </div>
              {isSelected && <CommitFileRows id={filesId} width={textColumnsWidth} />}
              </Fragment>
            );
          })}
        </div>
      </div>
    </div>
  );
});

export function GraphView() {
  const commits = useRepoStore((s) => s.commits);
  const selectedCommitId = useRepoStore((s) => s.selectedCommitId);
  const selectedCommitFile = useRepoStore((s) => s.selectedCommitFile);
  const commitFileDiff = useRepoStore((s) => s.commitFileDiff);
  const commitFileDiffLoading = useRepoStore((s) => s.commitFileDiffLoading);
  const selectCommitFile = useRepoStore((s) => s.selectCommitFile);
  const diffWidth = useLayoutStore((s) => s.sizes.graphDiffWidth);
  const setSize = useLayoutStore((s) => s.setSize);
  const commitSize = useLayoutStore((s) => s.commitSize);

  const selectedCommit = commits.find((c) => c.id === selectedCommitId) ?? null;
  // The panel belongs to a *file*, not to a commit: expanding a commit only lists its files.
  const openFile = selectedCommit && selectedCommitFile ? selectedCommitFile : null;

  return (
    <div className="flex h-full min-h-0 gap-1.5">
      <div className={`flex min-w-0 flex-1 flex-col overflow-hidden ${CARD}`}>
        <CommitTable />
      </div>

      {selectedCommit && openFile && (
        <>
          <ResizeHandle
            axis="x"
            value={diffWidth}
            min={DIFF_MIN}
            max={DIFF_MAX}
            invert
            onChange={(w) => setSize("graphDiffWidth", w)}
            onCommit={(w) => commitSize("graphDiffWidth", w)}
          />
          <div
            style={{ width: diffWidth }}
            className={`flex shrink-0 flex-col overflow-hidden ${CARD}`}
          >
            <div className="flex items-center justify-between border-b border-[var(--cf-border)] px-3 py-1.5">
              <span className="truncate text-ui font-medium text-[var(--cf-text-muted)]">
                {selectedCommit.short_id} — {openFile}
              </span>
              {/* Closes the file and leaves the commit expanded — the list is where the user was. */}
              <IconButton
                label="graph.closeFile"
                icon={X}
                className="shrink-0"
                onClick={() => void selectCommitFile(null)}
              />
            </div>
            <div className="min-h-0 flex-1">
              {commitFileDiffLoading ? <SkeletonRows count={10} /> : <DiffView files={commitFileDiff} />}
            </div>
          </div>
        </>
      )}
    </div>
  );
}
