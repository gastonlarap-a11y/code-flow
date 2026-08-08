import { useEffect, type ReactNode } from "react";
import { motion, useReducedMotion } from "framer-motion";
import { X, type LucideIcon } from "lucide-react";
import { useDialog } from "../../lib/useDialog";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";
import { IconButton } from "./IconButton";

/**
 * The detail of one entity, in a panel that slides in from the right.
 *
 * The third shell, after `Modal` and `PickerModal`, and the line between them is what the surface
 * is *for*. A modal interrupts: it asks a question and will not let the app continue until it has
 * an answer. A drawer does not — it shows you a pull request, a stash, a work item, while the list
 * you picked it from stays where it was, so going through five of them is five clicks rather than
 * five open-read-close cycles. That is the pattern GitKraken, GitHub and Atlassian all settled on
 * for entity detail, and the reason modals here are left holding confirmations and short actions.
 *
 * It still takes focus while open, and `useDialog` is what gives it the role, the name, the trap
 * and the focus restore — the same three lines every other dialog in the app gets them from.
 *
 * **No consumer yet.** It is built for the work items module (§7 of the redesign proposal), which
 * is out of scope here, and shipped ahead of it deliberately rather than discovered late. The first
 * feature to use it should expect to adjust the width, and that is fine.
 */
export function DetailDrawer({
  title,
  titleParams,
  subtitle,
  icon: Icon,
  onClose,
  toolbar,
  footer,
  children,
}: {
  title: TranslationKey;
  titleParams?: Record<string, string | number>;
  subtitle?: string;
  icon?: LucideIcon;
  onClose: () => void;
  /** Controls in the header, before the close button. */
  toolbar?: ReactNode;
  /** Right-aligned action row pinned to the bottom. */
  footer?: ReactNode;
  children: ReactNode;
}) {
  const t = useT();
  const { titleId, dialogProps } = useDialog();
  const reduceMotion = useReducedMotion();

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  return (
    <div
      className="fixed inset-0 z-50 flex justify-end bg-[color-mix(in_oklab,black_calc(var(--cf-overlay-scrim)*100%),transparent)]"
      onClick={onClose}
    >
      <motion.div
        initial={reduceMotion ? false : { x: "100%" }}
        animate={{ x: 0 }}
        transition={reduceMotion ? { duration: 0 } : { type: "spring", stiffness: 520, damping: 44 }}
        // The panel sits inside the scrim, so without this every click on it would dismiss it.
        onClick={(event) => event.stopPropagation()}
        {...dialogProps}
        className="flex h-full w-[420px] max-w-[92vw] flex-col overflow-hidden border-l border-[var(--cf-border)] bg-[var(--cf-surface-raised)] shadow-[var(--cf-shadow)]"
      >
        <div className="flex shrink-0 items-center gap-2 border-b border-[var(--cf-border)] px-4 py-3">
          {Icon && <Icon size={16} className="shrink-0 text-[var(--cf-text-muted)]" aria-hidden />}
          <div className="min-w-0 flex-1">
            {/* A real heading, not a styled div: it is what `aria-labelledby` points at. */}
            <h2 id={titleId} className="truncate text-body font-semibold text-[var(--cf-text)]">
              {t(title, titleParams)}
            </h2>
            {subtitle && <p className="truncate text-badge text-[var(--cf-text-muted)]">{subtitle}</p>}
          </div>
          <div className="flex shrink-0 items-center gap-1">
            {toolbar}
            <IconButton label="common.close" icon={X} onClick={onClose} />
          </div>
        </div>

        <div className="min-h-0 flex-1 overflow-auto px-4 py-4">{children}</div>

        {footer && (
          <div className="flex shrink-0 justify-end gap-2 border-t border-[var(--cf-border)] px-4 py-3">
            {footer}
          </div>
        )}
      </motion.div>
    </div>
  );
}
