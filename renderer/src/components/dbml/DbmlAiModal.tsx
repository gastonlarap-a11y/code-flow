import { useMemo, useState } from "react";
import { AlertTriangle, ArrowLeft, Check, Sparkles } from "lucide-react";
import { Modal } from "../common/Modal";
import { Button } from "../common/Button";
import { Chip } from "../common/Chip";
import { Select } from "../common/Select";
import { AiRunLog } from "../ai/AiRunLog";
import { DiffEditor } from "../../lib/monacoEditor";
import { renderMarkdown } from "../../lib/markdown";
import { checkProposal, rewritesDocument, type DbmlProposal } from "../../lib/dbml/assist";
import { dbmlAssist } from "../../lib/ipc/commands";
import { isCancellation, newRunId, useAiRunStore } from "../../state/aiRunStore";
import { useThemeStore } from "../../state/themeStore";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";
import type { DbmlAssistMode } from "../../types/domain";

/**
 * The AI over one schema document (DBML-016, DBML-017).
 *
 * Three actions behind one dialog, because they differ in what happens to the answer and in nothing
 * else: `edit` comes back as a schema that has to survive the parser before it is offered, the other
 * two as prose that is read. That asymmetry is the whole design — **nothing the model writes reaches
 * the buffer without being parsed first**, and nothing reaches disk at all: applying puts the
 * proposal in the editor, where the existing save button and Ctrl+Z still own it.
 */
const MODES: readonly { value: DbmlAssistMode; label: TranslationKey; placeholder: TranslationKey }[] = [
  { value: "edit", label: "dbml.ai.modeEdit", placeholder: "dbml.ai.placeholderEdit" },
  { value: "review", label: "dbml.ai.modeReview", placeholder: "dbml.ai.placeholderReview" },
  { value: "explain", label: "dbml.ai.modeExplain", placeholder: "dbml.ai.placeholderExplain" },
];

/** What came back, once a run finished. `null` while the dialog is still a form. */
type Answer =
  | { mode: "edit"; raw: string; proposal: DbmlProposal }
  | { mode: "review" | "explain"; text: string };

export function DbmlAiModal({
  source,
  relPath,
  onApply,
  onClose,
}: {
  source: string;
  relPath: string;
  /** Hands the rewritten schema to the editor buffer. Never writes to disk. */
  onApply: (next: string) => void;
  onClose: () => void;
}) {
  const t = useT();
  const theme = useThemeStore((s) => s.monacoTheme);

  const [mode, setMode] = useState<DbmlAssistMode>("edit");
  const [instruction, setInstruction] = useState("");
  const [running, setRunning] = useState(false);
  const [runId, setRunId] = useState<string | null>(null);
  const [answer, setAnswer] = useState<Answer | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [logExpanded, setLogExpanded] = useState(false);

  const active = MODES.find((option) => option.value === mode) ?? MODES[0]!;

  // The edit mode is the only one that cannot run on its own — the sidecar refuses it too, but a
  // disabled button says so before the round trip.
  const ready = !rewritesDocument(mode) || instruction.trim().length > 0;

  const run = async () => {
    const id = newRunId("dbml");
    setRunId(id);
    setRunning(true);
    setError(null);
    useAiRunStore.getState().start(id);
    try {
      const reply = await dbmlAssist(mode, source, instruction.trim(), id);
      setAnswer(
        mode === "edit"
          ? { mode, raw: reply, proposal: checkProposal(source, reply) }
          : { mode, text: reply },
      );
    } catch (e) {
      // A run the user stopped is not a failure to report back to them.
      if (!isCancellation(e)) setError(String(e));
    } finally {
      useAiRunStore.getState().finish(id);
      setRunning(false);
    }
  };

  const askAgain = () => {
    setAnswer(null);
    setError(null);
  };

  return (
    <Modal
      title="dbml.ai.title"
      subtitle={relPath}
      icon={Sparkles}
      size={answer === null ? "lg" : "3xl"}
      scroll
      fill={answer !== null}
      dismissible={!running}
      toolbar={
        answer !== null && (
          <Button variant="ghost" size="sm" icon={ArrowLeft} onClick={askAgain}>
            {t("dbml.ai.askAgain")}
          </Button>
        )
      }
      footer={
        answer === null ? (
          <>
            <Button variant="secondary" onClick={onClose} disabled={running}>
              {t("dbml.cancel")}
            </Button>
            <Button
              variant="primary"
              icon={Sparkles}
              pending={running}
              disabled={running || !ready}
              onClick={() => void run()}
            >
              {t("dbml.ai.run")}
            </Button>
          </>
        ) : (
          <ResultActions answer={answer} onApply={onApply} onClose={onClose} />
        )
      }
      onClose={onClose}
    >
      {answer === null ? (
        <div className="flex flex-col gap-4">
          <label className="flex flex-col gap-1.5">
            <span className="text-relaxed text-[var(--cf-text)]">{t("dbml.ai.mode")}</span>
            <Select
              value={mode}
              onChange={(value) => setMode(value as DbmlAssistMode)}
              ariaLabel={t("dbml.ai.mode")}
              options={MODES.map((option) => ({ value: option.value, label: t(option.label) }))}
            />
          </label>

          <label className="flex flex-col gap-1.5">
            <span className="text-relaxed text-[var(--cf-text)]">
              {t(rewritesDocument(mode) ? "dbml.ai.instruction" : "dbml.ai.focus")}
            </span>
            <textarea
              autoFocus
              value={instruction}
              onChange={(e) => setInstruction(e.target.value)}
              disabled={running}
              rows={4}
              spellCheck={false}
              placeholder={t(active.placeholder)}
              className="w-full resize-y rounded-md border border-[var(--cf-border)] bg-transparent px-2.5 py-1.5 text-body leading-relaxed outline-none focus:border-[var(--cf-accent)] disabled:opacity-60"
            />
          </label>

          <p className="text-badge text-[var(--cf-text-muted)]">
            {t(rewritesDocument(mode) ? "dbml.ai.editHint" : "dbml.ai.readHint")}
          </p>

          {runId !== null && (
            <AiRunLog
              runId={runId}
              running={running}
              expanded={logExpanded}
              onToggle={() => setLogExpanded((v) => !v)}
            />
          )}

          {error !== null && <Failure message={t("dbml.ai.failed", { error })} />}
        </div>
      ) : (
        <Result answer={answer} source={source} theme={theme} />
      )}
    </Modal>
  );
}

/** The answer itself: a diff for a rewrite, rendered markdown for the two that are read. */
function Result({ answer, source, theme }: { answer: Answer; source: string; theme: string }) {
  const t = useT();
  const html = useMemo(
    () => (answer.mode === "edit" ? "" : renderMarkdown(answer.text)),
    [answer],
  );

  if (answer.mode !== "edit") {
    return (
      <div
        className="cf-markdown-preview select-text"
        dangerouslySetInnerHTML={{ __html: html }}
      />
    );
  }

  const { proposal, raw } = answer;

  if (proposal.kind === "invalid" || proposal.kind === "empty") {
    return (
      <div className="flex flex-col gap-3">
        <Failure
          message={
            proposal.kind === "empty"
              ? t("dbml.ai.answerEmpty")
              : t("dbml.ai.answerInvalid", { error: proposal.error })
          }
        />
        {/* What it actually said. Without this the user is told the answer was rejected and has no
            way to see whether it was close. */}
        {raw.trim().length > 0 && (
          <pre className="max-h-64 select-text overflow-auto rounded-md border border-[var(--cf-border)] bg-[var(--cf-surface)] p-2 font-mono text-badge leading-[1.5] text-[var(--cf-text-muted)]">
            {raw}
          </pre>
        )}
      </div>
    );
  }

  if (proposal.kind === "unchanged") {
    return <Failure message={t("dbml.ai.answerUnchanged")} tone="warning" />;
  }

  return (
    <div className="flex h-full min-h-[320px] flex-col gap-2">
      <div className="flex flex-wrap items-center gap-1.5">
        <Chip tone="accent">{t("dbml.ai.tablesAdded", { n: proposal.summary.added.length })}</Chip>
        <Chip>{t("dbml.ai.tablesKept", { n: proposal.summary.kept })}</Chip>
        {/* A model that obeys the format and returns half the document is the failure the diff
            buries; the count puts it above the button instead. */}
        {proposal.summary.removed.length > 0 && (
          <Chip tone="danger" icon={AlertTriangle}>
            {t("dbml.ai.tablesRemoved", {
              n: proposal.summary.removed.length,
              tables: proposal.summary.removed.join(", "),
            })}
          </Chip>
        )}
      </div>

      <div className="min-h-0 flex-1 overflow-hidden rounded-md border border-[var(--cf-border)]">
        <DiffEditor
          height="100%"
          language="sql"
          original={source}
          modified={proposal.source}
          theme={theme}
          options={{
            readOnly: true,
            fontSize: 12,
            renderSideBySide: true,
            useInlineViewWhenSpaceIsLimited: false,
            automaticLayout: true,
            minimap: { enabled: false },
          }}
        />
      </div>

      <p className="text-badge text-[var(--cf-text-muted)]">{t("dbml.ai.applyHint")}</p>
    </div>
  );
}

/** The footer for a finished run: apply only where there is something valid to apply. */
function ResultActions({
  answer,
  onApply,
  onClose,
}: {
  answer: Answer;
  onApply: (next: string) => void;
  onClose: () => void;
}) {
  const t = useT();
  const applicable = answer.mode === "edit" && answer.proposal.kind === "ok" ? answer.proposal.source : null;

  return (
    <>
      <Button variant="secondary" onClick={onClose}>
        {t(applicable === null ? "common.close" : "dbml.ai.discard")}
      </Button>
      {applicable !== null && (
        <Button
          variant="primary"
          icon={Check}
          onClick={() => {
            onApply(applicable);
            onClose();
          }}
        >
          {t("dbml.ai.apply")}
        </Button>
      )}
    </>
  );
}

/**
 * A refusal, in the tone of what it means.
 *
 * `danger` is "this cannot be used", `warning` is "this ran and produced nothing to apply" — an
 * unchanged schema is an outcome, not a fault. Both classes are written out because Tailwind
 * resolves class names at build time and a composed one would never reach the stylesheet.
 */
function Failure({ message, tone = "danger" }: { message: string; tone?: "danger" | "warning" }) {
  return (
    <p
      role="status"
      className={`flex items-start gap-2 rounded-md border p-3 text-ui ${
        tone === "danger"
          ? "border-[var(--cf-danger)] bg-[color-mix(in_oklab,var(--cf-danger)_10%,var(--cf-surface))] text-[var(--cf-danger)]"
          : "border-[var(--cf-warning)] bg-[color-mix(in_oklab,var(--cf-warning)_10%,var(--cf-surface))] text-[var(--cf-warning)]"
      }`}
    >
      <AlertTriangle size={14} className="mt-0.5 shrink-0" aria-hidden />
      <span className="min-w-0 select-text whitespace-pre-wrap break-words">{message}</span>
    </p>
  );
}
