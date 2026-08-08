import { Suspense, useState } from "react";
import { lazyRetry } from "../../lib/lazyRetry";
import { AlertTriangle, Check, Code2, GitMerge, Sparkles, X } from "lucide-react";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import { useRepoStore } from "../../state/repoStore";
import { useUiStore } from "../../state/uiStore";
import { confirmAction } from "../../state/confirmStore";
import { useT } from "../../state/languageStore";
// Two Monaco editors, reached only from a repository that is actually mid-conflict. A static
// import would put them in the chunk that renders the Changes panel for everyone else.
const ConflictResolveModal = lazyRetry(() =>
  import("./ConflictResolveModal").then((m) => ({ default: m.ConflictResolveModal })),
);
import type { ResolveOutcome } from "./ConflictResolveModal";

export function ConflictsBanner() {
  const conflicts = useRepoStore((s) => s.conflicts);
  const merging = useRepoStore((s) => s.merging);
  const resolveConflict = useRepoStore((s) => s.resolveConflict);
  const markConflictResolved = useRepoStore((s) => s.markConflictResolved);
  const completeMerge = useRepoStore((s) => s.completeMerge);
  const abortMerge = useRepoStore((s) => s.abortMerge);
  const discardCarried = useRepoStore((s) => s.discardConflicted);
  const busy = useRepoStore((s) => s.busy);
  const openInEditor = useUiStore((s) => s.openInEditor);
  const t = useT();
  const [message, setMessage] = useState("Merge");
  const [aiFile, setAiFile] = useState<string | null>(null);
  /** Paths still to walk through with the AI, oldest first. Empty when not batch-resolving. */
  const [queue, setQueue] = useState<string[]>([]);

  // The queue is a snapshot, and each acceptance refreshes `conflicts` — so the next file is the
  // first one still conflicted. Skipping that check would reopen a file already resolved.
  const remaining = queue.filter((path) => conflicts.some((c) => c.path === path));
  const openFile = aiFile ?? remaining[0] ?? null;

  /** Accepting moves to the next file; cancelling stops the run — Escape means "stop", not "skip". */
  const afterResolve = (outcome: ResolveOutcome) => {
    setAiFile(null);
    setQueue(outcome === "accepted" ? remaining.slice(1) : []);
  };

  return (
    <div className="border-b border-[var(--cf-border)] bg-[color-mix(in_oklab,var(--cf-warning)_10%,transparent)] p-3">
      <div className="mb-2 flex items-center gap-2 text-body font-semibold text-[var(--cf-text)]">
        <AlertTriangle size={14} className="text-[var(--cf-warning)]" />
        {/* The same conflicts arrive by two roads — a merge, or a stash that would not apply —
            and telling the user which one they are in is the difference between the footer
            below making sense and not. */}
        <span className="flex-1">{merging ? t("conflicts.title") : t("conflicts.titleFromStash")}</span>
        {conflicts.length > 1 && (
          <Button
            variant="ghost"
            size="sm"
            icon={Sparkles}
            disabled={queue.length > 0}
            onClick={() => setQueue(conflicts.map((c) => c.path))}
          >
            {t("conflicts.aiResolveAll")}
          </Button>
        )}
      </div>

      <div className="mb-3 space-y-1">
        {conflicts.map((c) => (
          <div
            key={c.path}
            className="flex items-center gap-2 rounded-md bg-[var(--cf-surface)] px-2 py-1.5 text-ui"
          >
            <span className="flex-1 min-w-0 truncate font-mono">{c.path}</span>
            <Button
              variant="ghost"
              size="sm"
              icon={Sparkles}
              tooltip={t("conflicts.aiResolveTitle")}
              className="shrink-0"
              onClick={() => setAiFile(c.path)}
            >
              {t("conflicts.aiResolve")}
            </Button>
            <Button variant="ghost" size="sm" className="shrink-0" onClick={() => resolveConflict(c.path, "ours")}>
              {t("conflicts.keepOurs")}
            </Button>
            <Button variant="ghost" size="sm" className="shrink-0" onClick={() => resolveConflict(c.path, "theirs")}>
              {t("conflicts.keepTheirs")}
            </Button>
            <IconButton
              label="conflicts.editManually"
              icon={Code2}
              className="shrink-0"
              onClick={() => openInEditor(c.path)}
            />
            <IconButton
              label="conflicts.markResolved"
              icon={Check}
              variant="success"
              className="shrink-0"
              onClick={() => markConflictResolved(c.path)}
            />
          </div>
        ))}
        {conflicts.length === 0 && (
          <p className="rounded-md bg-[var(--cf-surface)] px-2 py-1.5 text-ui text-[var(--cf-success)]">
            {t("conflicts.allResolved")}
          </p>
        )}
      </div>

      {merging ? (
        <div className="flex items-center gap-2">
          <input
            value={message}
            onChange={(e) => setMessage(e.target.value)}
            className="flex-1 rounded-md border border-[var(--cf-border)] bg-transparent px-2 py-1 text-ui outline-none focus:border-[var(--cf-accent)]"
          />
          <Button
            variant="primary"
            size="sm"
            icon={GitMerge}
            disabled={busy || conflicts.length > 0 || !message.trim()}
            onClick={() => completeMerge(message.trim())}
          >
            {t("conflicts.completeMerge")}
          </Button>
          <Button
            variant="danger"
            size="sm"
            icon={X}
            disabled={busy}
            onClick={async () => {
              if (await confirmAction(t("conflicts.abortConfirm"))) void abortMerge();
            }}
          >
            {t("conflicts.abortMerge")}
          </Button>
        </div>
      ) : (
        // No merge to complete, and deliberately no "abort merge": `Merge.Abort` never checks
        // whether a merge is in progress — it is a `reset --hard HEAD` that would take every
        // uncommitted change with it (GIT-018). Discarding here is safe for the opposite reason:
        // the stash these conflicts came from is still in the list.
        <div className="flex items-center gap-2">
          <p className="flex-1 text-ui text-[var(--cf-text-muted)]">{t("conflicts.stashStillThere")}</p>
          <Button
            variant="danger"
            size="sm"
            icon={X}
            disabled={busy}
            onClick={async () => {
              if (await confirmAction(t("conflicts.discardCarriedConfirm"))) void discardCarried();
            }}
          >
            {t("conflicts.discardCarried")}
          </Button>
        </div>
      )}
      {openFile && (
        <Suspense fallback={null}>
          <ConflictResolveModal
            key={openFile}
            filePath={openFile}
            queued={remaining.length > 1 ? remaining.length : undefined}
            onClose={afterResolve}
          />
        </Suspense>
      )}
    </div>
  );
}
