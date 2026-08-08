import { useEffect, useState } from "react";
import { useDialog } from "../../lib/useDialog";
import { DiffEditor, Editor } from "../../lib/monacoEditor";
import { AlertTriangle, Check, Columns2, Loader2, Sparkles, X } from "lucide-react";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import { readFileText, writeFileText, resolveConflictWithAi } from "../../lib/ipc/commands";
import { useRepoStore } from "../../state/repoStore";
import { useThemeStore } from "../../state/themeStore";
import { parseClaudeError } from "../../lib/claudeError";
import { languageForPath } from "../../lib/monacoLanguage";
import { useT } from "../../state/languageStore";

/** Which way the dialog closed — the batch run needs to tell "next file" from "stop". */
export type ResolveOutcome = "accepted" | "cancelled";

/**
 * AI-assisted resolution for a single conflicted file. On open it asks the backend to merge the
 * file's base/ours/theirs versions and shows the proposal in an editable Monaco editor (with a
 * side-by-side diff toggle against the current marker-laden working copy). Nothing touches disk
 * until the user accepts — then the edited proposal is written and the file is staged as resolved.
 */
export function ConflictResolveModal({
  filePath,
  queued,
  onClose,
}: {
  filePath: string;
  /** How many files are left in a "resolve them all" run, this one included. Absent for a single file. */
  queued?: number | undefined;
  onClose: (outcome: ResolveOutcome) => void;
}) {
  const t = useT();
  const repoPath = useRepoStore((s) => s.repoPath);
  const markConflictResolved = useRepoStore((s) => s.markConflictResolved);


  const [original, setOriginal] = useState("");
  const [proposal, setProposal] = useState("");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [accepting, setAccepting] = useState(false);
  const [showDiff, setShowDiff] = useState(false);

  const generate = async () => {
    if (!repoPath) return;
    setLoading(true);
    setError(null);
    try {
      const [current, proposed] = await Promise.all([
        readFileText(repoPath, filePath).catch(() => ""),
        resolveConflictWithAi(repoPath, filePath),
      ]);
      setOriginal(current);
      setProposal(proposed);
    } catch (e) {
      setError(parseClaudeError(String(e)).message);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void generate();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filePath]);

  const busy = loading || accepting;
  const language = languageForPath(filePath);
  const theme = useThemeStore((s) => s.monacoTheme);

  const accept = async () => {
    if (!repoPath || !proposal.trim()) return;
    setAccepting(true);
    try {
      await writeFileText(repoPath, filePath, proposal);
      await markConflictResolved(filePath);
      onClose("accepted");
    } catch (e) {
      setError(parseClaudeError(String(e)).message);
      setAccepting(false);
    }
  };

  const { titleId, dialogProps } = useDialog();


  return (
    <div
      className="fixed inset-0 z-[60] flex items-center justify-center bg-black/40 p-6"
      onClick={busy ? undefined : () => onClose("cancelled")}
    >
      <div
        {...dialogProps}
        onClick={(e) => e.stopPropagation()}
        className="flex h-full max-h-[85vh] w-[900px] max-w-[95vw] flex-col overflow-hidden rounded-xl border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] shadow-[var(--cf-shadow)]"
      >
        <div className="flex items-center gap-2 border-b border-[var(--cf-border)] p-3">
          <Sparkles size={15} className="shrink-0 text-[var(--cf-accent)]" />
          <span id={titleId} className="text-body font-semibold text-[var(--cf-text)]">{t("conflicts.aiResolveTitle")}</span>
          <span className="min-w-0 flex-1 truncate font-mono text-badge text-[var(--cf-text-muted)]">{filePath}</span>
          {/* In a batch run, say how many are left — otherwise each dialog looks like the last one. */}
          {queued !== undefined && (
            <span className="shrink-0 rounded px-1.5 py-0.5 text-badge font-medium bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]">
              {t("conflicts.aiQueueRemaining", { n: queued })}
            </span>
          )}
          {!loading && !error && (
            <Button variant="ghost" size="sm" icon={Columns2} className="shrink-0" onClick={() => setShowDiff((v) => !v)}>
              {showDiff ? t("conflicts.aiEdit") : t("conflicts.aiViewDiff")}
            </Button>
          )}
          {!busy && (
            <IconButton
              label="common.close"
              icon={X}
              className="shrink-0"
              onClick={() => onClose("cancelled")}
            />
          )}
        </div>

        <div className="min-h-0 flex-1">
          {loading ? (
            <div className="flex h-full flex-col items-center justify-center gap-2 text-body text-[var(--cf-text-muted)]">
              <Loader2 size={20} className="animate-spin text-[var(--cf-accent)]" />
              {t("conflicts.aiResolving")}
            </div>
          ) : error ? (
            <div className="flex h-full flex-col items-center justify-center gap-3 p-6 text-center">
              <AlertTriangle size={20} className="text-[var(--cf-danger)]" />
              <p className="max-w-[520px] text-ui text-[var(--cf-text)]">{error}</p>
              <Button variant="ghost" size="sm" onClick={generate}>
                {t("sidebar.retry")}
              </Button>
            </div>
          ) : showDiff ? (
            <DiffEditor
              height="100%"
              language={language}
              original={original}
              modified={proposal}
              theme={theme}
              options={{
                readOnly: true,
                fontSize: 13,
                renderSideBySide: true,
                useInlineViewWhenSpaceIsLimited: false,
                automaticLayout: true,
              }}
            />
          ) : (
            <Editor
              height="100%"
              language={language}
              value={proposal}
              theme={theme}
              onChange={(v) => setProposal(v ?? "")}
              options={{ fontSize: 13, minimap: { enabled: false }, automaticLayout: true }}
            />
          )}
        </div>

        <div className="flex items-center justify-between gap-2 border-t border-[var(--cf-border)] p-3">
          <span className="text-badge text-[var(--cf-text-muted)]">{t("conflicts.aiReviewHint")}</span>
          <div className="flex gap-2">
            <Button variant="ghost" disabled={busy} onClick={() => onClose("cancelled")}>
              {t("common.cancel")}
            </Button>
            <Button
              variant="primary"
              icon={Check}
              pending={accepting}
              disabled={busy || error !== null || !proposal.trim()}
              onClick={accept}
            >
              {t("conflicts.aiAccept")}
            </Button>
          </div>
        </div>
      </div>
    </div>
  );
}
