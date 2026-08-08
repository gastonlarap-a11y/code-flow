import { useEffect, useState } from "react";
import { Modal } from "../common/Modal";
import { getCommitFileDiff, listCommitFiles } from "../../lib/ipc/commands";
import { useRepoStore } from "../../state/repoStore";
import { DiffView } from "../git/DiffView";
import { BouncingDots } from "../common/BouncingDots";
import { Tooltip } from "../common/Tooltip";
import { fileStatusColor, fileStatusLabelKey } from "../../lib/fileStatus";
import { useT } from "../../state/languageStore";
import type { CommitFileInfo, CommitInfo, FileDiffInfo } from "../../types/domain";

/** The key a file is selected by — the same one the graph uses (GIT-035). */
function filePath(file: CommitFileInfo): string {
  return file.new_path ?? file.old_path ?? "";
}

/**
 * One commit, its files listed on the left and the picked file's diff on the right.
 *
 * State is local rather than `repoStore`'s: `commitFiles`/`selectedCommitFile` there belong to the
 * graph view, and sharing them would make opening this dialog move that view's selection.
 */
export function CommitDiffModal({ commit, onClose }: { commit: CommitInfo; onClose: () => void }) {
  const repoPath = useRepoStore((s) => s.repoPath);
  const [files, setFiles] = useState<CommitFileInfo[] | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [diff, setDiff] = useState<FileDiffInfo[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const t = useT();

  useEffect(() => {
    if (!repoPath) return;
    let cancelled = false;
    setFiles(null);
    setError(null);
    listCommitFiles(repoPath, commit.id)
      .then((listed) => {
        if (cancelled) return;
        setFiles(listed);
        // Opening on an empty right-hand pane reads as a broken dialog, so the first file is
        // picked for the user — this is a viewer, not a form.
        const first = listed.at(0);
        setSelected(first ? filePath(first) : null);
      })
      .catch((e) => {
        if (!cancelled) setError(String(e));
      });
    return () => {
      cancelled = true;
    };
  }, [repoPath, commit.id]);

  const file = files?.find((f) => filePath(f) === selected) ?? null;

  useEffect(() => {
    if (!repoPath || !file) return;
    let cancelled = false;
    const path = filePath(file);
    setDiff(null);
    getCommitFileDiff(repoPath, commit.id, path, file.old_path)
      .then((d) => {
        if (!cancelled) setDiff(d);
      })
      .catch((e) => {
        if (!cancelled) setError(String(e));
      });
    return () => {
      cancelled = true;
    };
  }, [repoPath, commit.id, file]);

  return (
    // `titleText`, not `title`: the heading is the commit's own summary, which is data rather than
    // a phrase. `fill` holds the panel at full height so nothing resizes as the diff loads.
    <Modal titleText={`${commit.short_id} — ${commit.summary}`} size="3xl" scroll fill onClose={onClose}>
      <div className="-mx-4 -my-4 flex h-full">
        {error ? (
          <p className="p-4 text-ui text-[var(--cf-danger)]">{error}</p>
        ) : !files ? (
          <div className="flex h-full w-full items-center justify-center">
            <BouncingDots />
          </div>
        ) : files.length === 0 ? (
          <p className="p-4 text-ui text-[var(--cf-text-muted)]">{t("sidebar.commitTouchedNoFiles")}</p>
        ) : (
          <>
            <div className="w-64 shrink-0 overflow-auto border-r border-[var(--cf-border)] py-1">
              {files.map((f) => {
                const path = filePath(f);
                const isSelected = path === selected;
                return (
                  <button
                    key={path}
                    onClick={() => setSelected(path)}
                    aria-pressed={isSelected}
                    className={`cf-focusable flex h-6 w-full items-center gap-2 px-2 text-left ${
                      isSelected
                        ? "bg-[var(--cf-accent-soft)]"
                        : "hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
                    }`}
                  >
                    <Tooltip label={t(fileStatusLabelKey(f.status))}>
                      <span
                        className="w-4 shrink-0 text-center text-badge font-semibold uppercase"
                        style={{ color: fileStatusColor(f.status) }}
                      >
                        {f.status[0]}
                      </span>
                    </Tooltip>
                    <span className="min-w-0 flex-1 truncate font-mono text-ui text-[var(--cf-text)]">
                      {path}
                    </span>
                  </button>
                );
              })}
            </div>
            <div className="min-w-0 flex-1">
              {diff ? (
                <DiffView files={diff} />
              ) : (
                <div className="flex h-full items-center justify-center">
                  <BouncingDots />
                </div>
              )}
            </div>
          </>
        )}
      </div>
    </Modal>
  );
}
