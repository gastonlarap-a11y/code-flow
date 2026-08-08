import { useId, useRef } from "react";
import { useFocusTrap } from "./useFocusTrap";

/**
 * Everything a modal panel needs to be one, for a screen reader and for the keyboard.
 *
 * This exists because the app has seventeen modals and only `ApiModal` was ever shared chrome; the
 * rest hand-roll a backdrop, an Escape handler and a panel. Repeating four attributes and a hook
 * call across sixteen files is how fifteen of them end up correct and one does not — and the one
 * that does not is invisible until somebody tries to use the app without a mouse.
 *
 * Spread `dialogProps` on the panel — the inner element, not the backdrop — and put `titleId` on
 * whatever names it:
 *
 * ```tsx
 * const { titleId, dialogProps } = useDialog();
 * <div className="fixed inset-0 …" onClick={onClose}>
 *   <div {...dialogProps} onClick={stop} className="…">
 *     <h3 id={titleId}>Clone repository</h3>
 * ```
 *
 * A dialog with no heading passes `label` instead and is named directly.
 */
export function useDialog(options: { label?: string; active?: boolean } = {}) {
  const { label, active = true } = options;

  const panel = useRef<HTMLDivElement>(null);
  const titleId = useId();

  useFocusTrap(panel, active);

  return {
    titleId,
    panel,
    dialogProps: {
      ref: panel,
      role: "dialog",
      "aria-modal": true,
      // Exactly one of these, never both: a dialog with two names is read out twice.
      ...(label ? { "aria-label": label } : { "aria-labelledby": titleId }),
    } as const,
  };
}
