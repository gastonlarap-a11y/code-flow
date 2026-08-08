import { useEffect } from "react";
import { TriangleAlert } from "lucide-react";
import { Button } from "./Button";
import { CONFIRM_CHOICE_ID, useConfirmStore } from "../../state/confirmStore";
import { useT } from "../../state/languageStore";
import { useDialog } from "../../lib/useDialog";

/**
 * The app's one confirmation dialog, and the one modal that keeps its own shell.
 *
 * `Modal` renders an `<h2>` from its `title`, and this dialog has no heading on purpose: the message
 * *is* the dialog, and it labels itself through `titleId`. Wrapping it would mean either an empty
 * heading or repeating the message twice.
 *
 * The destructive confirm is `variant="danger"` — red text, not the filled red it used to be. That
 * is not only consistency with every other destructive control in the app: white on `--cf-danger`
 * measures 2.77:1 in the dark theme, an outright AA failure, while the text variant clears AA on
 * every theme `scripts/theme-contrast.test.mjs` checks. The 32px red badge beside the message
 * carries the severity.
 */
export function ConfirmModal() {
  const request = useConfirmStore((s) => s.request);
  const respond = useConfirmStore((s) => s.respond);
  const t = useT();
  const { titleId, dialogProps } = useDialog({ active: request !== null });

  useEffect(() => {
    if (!request) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") respond(null);
      // Enter takes the first choice, which is the one the buttons lead with and the one
      // `autoFocus` is already on — the same "the obvious answer" Enter means for a plain confirm.
      if (e.key === "Enter") respond(request.choices?.[0]?.id ?? CONFIRM_CHOICE_ID);
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [request, respond]);

  if (!request) return null;

  return (
    <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/30" onClick={() => respond(null)}>
      <div
        // There is no heading here — the message *is* the dialog — so it labels itself.
        {...dialogProps}
        onClick={(e) => e.stopPropagation()}
        className="w-[380px] max-w-[90vw] rounded-xl border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-4 shadow-[var(--cf-shadow)]"
      >
        <div className="mb-4 flex items-start gap-3">
          <span
            className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-full ${
              request.danger
                ? "bg-[color-mix(in_oklab,var(--cf-danger)_16%,transparent)] text-[var(--cf-danger)]"
                : "bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]"
            }`}
          >
            <TriangleAlert size={16} />
          </span>
          <p id={titleId} className="flex-1 pt-1 text-body leading-snug text-[var(--cf-text)]">
            {request.message}
          </p>
        </div>
        {/* Three named actions do not fit a 380px row, and wrapping them mid-row reads as broken
            alignment — so more than two ways out stack full-width instead, in the order they should
            be considered: the leading action first, the alternative under it, cancel last. The
            plain yes/no dialog keeps its familiar `[Cancel] [Confirm]` row. */}
        {request.choices ? (
          <div className="flex flex-col gap-2">
            {request.choices.map((choice, i) => (
              <Button
                key={choice.id}
                variant={choice.variant ?? (i === 0 ? "primary" : "ghost")}
                autoFocus={i === 0}
                className="w-full justify-center"
                onClick={() => respond(choice.id)}
              >
                {choice.label}
              </Button>
            ))}
            {/* An acknowledgement has nothing to cancel — the thing already happened. */}
            {!request.acknowledge && (
              <Button variant="ghost" className="w-full justify-center" onClick={() => respond(null)}>
                {t("common.cancel")}
              </Button>
            )}
          </div>
        ) : (
          <div className="flex justify-end gap-2">
            <Button variant="ghost" onClick={() => respond(null)}>
              {t("common.cancel")}
            </Button>
            <Button
              variant={request.danger ? "danger" : "primary"}
              autoFocus
              onClick={() => respond(CONFIRM_CHOICE_ID)}
            >
              {request.confirmLabel ?? t("common.confirm")}
            </Button>
          </div>
        )}
      </div>
    </div>
  );
}
