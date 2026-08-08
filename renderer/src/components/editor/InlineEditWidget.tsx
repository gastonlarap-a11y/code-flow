import { useEffect, useRef, useState } from "react";
import { Loader2, Sparkles, X } from "lucide-react";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import { StopSquare } from "../../lib/ui/icons";
import { inlineEditWithAi } from "../../lib/ipc/commands";
import { isCancellation, newRunId, useAiRunStore } from "../../state/aiRunStore";
import { pushErrorToast } from "../../state/toastStore";
import { useT } from "../../state/languageStore";

/** Ctrl+I: describe the change in words, and the selected code is rewritten in place.
 *
 * The replacement lands in the editor's own buffer as a normal edit — one Ctrl+Z away from being
 * undone, and unsaved until the user decides otherwise. That's the deliberate difference from
 * "fix with AI", which lets an agent write to disk: this one never leaves the editor, so it can
 * be routed to any provider (a local model included) and carries no risk to the working tree.
 */
export function InlineEditWidget({
  filePath,
  fileContent,
  selection,
  onApply,
  onClose,
}: {
  filePath: string;
  fileContent: string;
  selection: string;
  onApply: (replacement: string) => void;
  onClose: () => void;
}) {
  const t = useT();
  const [instruction, setInstruction] = useState("");
  const [running, setRunning] = useState(false);
  const runIdRef = useRef<string | null>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const submit = async () => {
    const text = instruction.trim();
    if (!text || running) return;
    const runId = newRunId("inline");
    runIdRef.current = runId;
    useAiRunStore.getState().start(runId);
    setRunning(true);
    try {
      const replacement = await inlineEditWithAi(filePath, fileContent, selection, text, runId);
      onApply(replacement);
      onClose();
    } catch (e) {
      if (!isCancellation(e)) pushErrorToast(String(e));
    } finally {
      useAiRunStore.getState().finish(runId);
      setRunning(false);
      runIdRef.current = null;
    }
  };

  const stop = () => {
    const runId = runIdRef.current;
    if (runId) void useAiRunStore.getState().cancel(runId);
  };

  const selectionLines = selection.split("\n").length;

  return (
    <div className="absolute inset-x-3 top-3 z-20 rounded-lg border border-[var(--cf-accent)] bg-[var(--cf-surface)] shadow-[var(--cf-shadow)]">
      <div className="flex items-center gap-2 px-2.5 py-1.5">
        <Sparkles size={13} className="shrink-0 text-[var(--cf-accent)]" />
        <input
          autoFocus
          value={instruction}
          onChange={(e) => setInstruction(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey) {
              e.preventDefault();
              void submit();
            }
          }}
          disabled={running}
          placeholder={t("editor.inlineEditPlaceholder", { n: selectionLines })}
          className="min-w-0 flex-1 bg-transparent text-ui outline-none disabled:opacity-60"
        />
        {running ? (
          <Button variant="secondary" size="sm" icon={StopSquare} className="shrink-0" onClick={stop}>
            {t("ai.stop")}
          </Button>
        ) : (
          <Button
            variant="primary"
            size="sm"
            disabled={!instruction.trim()}
            className="shrink-0"
            onClick={() => void submit()}
          >
            {t("editor.inlineEditApply")}
          </Button>
        )}
        <IconButton label="common.close" icon={X} className="shrink-0" onClick={onClose} />
      </div>
      {running && (
        <div className="flex items-center gap-1.5 border-t border-[var(--cf-border)] px-2.5 py-1 text-badge text-[var(--cf-text-muted)]">
          <Loader2 size={11} className="animate-spin" />
          {t("ai.working")}
        </div>
      )}
    </div>
  );
}
