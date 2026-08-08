import { useEffect, useState } from "react";
import { Modal } from "../common/Modal";
import { getCommitDiff } from "../../lib/ipc/commands";
import { useRepoStore } from "../../state/repoStore";
import { DiffView } from "../git/DiffView";
import { BouncingDots } from "../common/BouncingDots";
import type { FileDiffInfo, StashInfo } from "../../types/domain";

export function StashDiffModal({ stash, onClose }: { stash: StashInfo; onClose: () => void }) {
  const repoPath = useRepoStore((s) => s.repoPath);
  const [diff, setDiff] = useState<FileDiffInfo[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!repoPath) return;
    let cancelled = false;
    setDiff(null);
    setError(null);
    getCommitDiff(repoPath, stash.oid)
      .then((d) => {
        if (!cancelled) setDiff(d);
      })
      .catch((e) => {
        if (!cancelled) setError(String(e));
      });
    return () => {
      cancelled = true;
    };
  }, [repoPath, stash.oid]);

  return (
    // `titleText`, not `title`: the heading is the stash's own message, which is data rather than a
    // phrase. `fill` holds the panel at full height so the diff does not resize as it loads.
    <Modal titleText={stash.message} size="3xl" scroll fill onClose={onClose}>
      <div className="-mx-4 -my-4 h-full">
        {error ? (
          <p className="p-4 text-ui text-[var(--cf-danger)]">{error}</p>
        ) : diff ? (
          <DiffView files={diff} />
        ) : (
          <div className="flex h-full items-center justify-center">
            <BouncingDots />
          </div>
        )}
      </div>
    </Modal>
  );
}
