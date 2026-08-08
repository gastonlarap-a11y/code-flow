import { useCallback, useEffect, useState } from "react";
import { History, Loader2, RotateCcw, Trash2 } from "lucide-react";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import { Modal } from "../common/Modal";
import {
  deleteAiCheckpoint,
  listAiCheckpoints,
  restoreAiCheckpoint,
  type AiCheckpoint,
} from "../../lib/ipc/commands";
import { useRepoStore } from "../../state/repoStore";
import { confirmAction } from "../../state/confirmStore";
import { pushErrorToast, useToastStore } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";
import { EmptyState } from "../common/EmptyState";

/** Maps the backend's stable action keys onto translated labels. An unknown key (an older
 * checkpoint, a kind added later) falls back to showing the raw key rather than nothing. */
const KIND_LABELS: Record<string, TranslationKey> = {
  chat: "checkpoints.kindChat",
  "fix-finding": "checkpoints.kindFix",
  "replace-all": "checkpoints.kindReplace",
};

/** The undo list: every snapshot taken before something was allowed to rewrite the working tree
 * — an AI run, a project-wide replace — with the files it would put back. Snapshots that would
 * restore nothing are dropped by the backend, so everything listed here is a real, reversible
 * change. */
export function CheckpointsModal({ repoPath, onClose }: { repoPath: string; onClose: () => void }) {
  const t = useT();
  const [checkpoints, setCheckpoints] = useState<AiCheckpoint[] | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const reload = useCallback(async () => {
    setCheckpoints(await listAiCheckpoints(repoPath).catch(() => []));
  }, [repoPath]);

  useEffect(() => {
    void reload();
  }, [reload]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const restore = async (checkpoint: AiCheckpoint) => {
    const ok = await confirmAction(
      t("checkpoints.confirmRestore", { n: checkpoint.changed_paths.length }),
      true,
    );
    if (!ok) return;
    setBusyId(checkpoint.id);
    try {
      const restored = await restoreAiCheckpoint(repoPath, checkpoint.id);
      useToastStore.getState().pushToast(t("checkpoints.restored", { n: restored.length }), "success");
      // The files changed on disk; the working diff and status the rest of the app shows are
      // now stale until they're re-read.
      void useRepoStore.getState().refreshAll();
      await reload();
    } catch (e) {
      pushErrorToast(String(e));
    } finally {
      setBusyId(null);
    }
  };

  const remove = async (checkpoint: AiCheckpoint) => {
    setBusyId(checkpoint.id);
    try {
      await deleteAiCheckpoint(repoPath, checkpoint.id);
      await reload();
    } catch (e) {
      pushErrorToast(String(e));
    } finally {
      setBusyId(null);
    }
  };



  return (
    <Modal title="checkpoints.title" icon={History} size="lg" scroll onClose={onClose}>
      <div>
          {checkpoints === null ? (
            <div className="flex justify-center py-8">
              <Loader2 size={16} className="animate-spin text-[var(--cf-text-muted)]" />
            </div>
          ) : checkpoints.length === 0 ? (
            <EmptyState icon={History} title={t("checkpoints.empty")} subtitle={t("checkpoints.emptyHint")} />
          ) : (
            <div className="space-y-2">
              {checkpoints.map((checkpoint) => {
                const kindKey = KIND_LABELS[checkpoint.kind];
                return (
                  <div
                    key={checkpoint.id}
                    className="rounded-lg border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-2.5"
                  >
                    <div className="flex items-center gap-2">
                      <span className="text-ui font-medium">{kindKey ? t(kindKey) : checkpoint.kind}</span>
                      <span className="text-badge text-[var(--cf-text-muted)]">
                        {new Date(checkpoint.created_at * 1000).toLocaleString()}
                      </span>
                      <Button
                        variant="secondary"
                        size="sm"
                        icon={RotateCcw}
                        pending={busyId === checkpoint.id}
                        disabled={busyId !== null}
                        className="ml-auto"
                        onClick={() => void restore(checkpoint)}
                      >
                        {t("checkpoints.restore")}
                      </Button>
                      <IconButton
                        label="checkpoints.forget"
                        icon={Trash2}
                        variant="danger"
                        disabled={busyId !== null}
                        onClick={() => void remove(checkpoint)}
                      />
                    </div>
                    <ul className="mt-1.5 space-y-0.5">
                      {checkpoint.changed_paths.slice(0, 6).map((path) => (
                        <li key={path} className="truncate font-mono text-badge text-[var(--cf-text-muted)]">
                          {path}
                        </li>
                      ))}
                      {checkpoint.changed_paths.length > 6 && (
                        <li className="text-badge text-[var(--cf-text-muted)]">
                          {t("checkpoints.andMore", { n: checkpoint.changed_paths.length - 6 })}
                        </li>
                      )}
                    </ul>
                  </div>
                );
              })}
            </div>
          )}
      </div>
    </Modal>
  );
}
