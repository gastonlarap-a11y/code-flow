import { useEffect, type ReactNode } from "react";
import { X, type LucideIcon } from "lucide-react";
import { IconButton } from "./IconButton";
import { useDialog } from "../../lib/useDialog";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";

/**
 * One modal shell, replacing 22 hand-rolled ones.
 *
 * The a11y half was already solved: `lib/useDialog.ts` and `lib/useFocusTrap.ts` give the panel its
 * `role`, its `aria-modal`, its accessible name, a Tab trap and focus restored to whatever opened
 * it — and 20 files already call them. What was still copied into every file was the *chrome*: the
 * backdrop, the Escape handler, the click-outside handler, the heading, and a close `X` that in 14
 * of those files had no label at all. That is what this absorbs.
 *
 * ## Why not `<dialog>`
 *
 * The obvious implementation is the native element, and it is the wrong one here. `showModal()`
 * promotes the dialog into the top layer, and five of this app's modals contain a `Select` whose
 * listbox is a `createPortal` into `document.body` at `z-[9999]` — below the top layer, therefore
 * invisible and unreachable the moment the dialog is native (`CreatePrModal`, `ConnectGithubModal`,
 * `ConnectAdoModal`, `ExportModal`, `EnvironmentModal`). The same holds for `ColorSwatchPicker`,
 * `ChatModelPicker` and `CodeSnippetPanel`. Revisit this when those four move to the popover API;
 * until then, a positioned `div` and the existing hooks are both correct and cheaper.
 */
export function Modal({
  title,
  titleText,
  titleParams,
  subtitle,
  icon: Icon,
  size = "md",
  onClose,
  dismissible = true,
  scroll = false,
  fill = false,
  toolbar,
  footer,
  children,
}: {
  title?: TranslationKey;
  /**
   * An already-formed title, for the few whose heading is data rather than a phrase — a stash
   * message, a file name. Exactly one of `title` / `titleText` is expected.
   */
  titleText?: string;
  titleParams?: Record<string, string | number>;
  /** Secondary line under the heading: what this dialog is about, when the title cannot say it. */
  subtitle?: string;
  /** Optional glyph beside the heading, matching the pattern the existing modals already use. */
  icon?: LucideIcon;
  size?: ModalSize;
  /** Called by the close button, by Escape and by a click on the scrim. */
  onClose: () => void;
  /**
   * Set to `false` while an operation is in flight, which hides the close affordance and inerts
   * Escape and the scrim. `CloneRepoModal` does this by hand today so a half-finished clone cannot
   * be dismissed out from under itself.
   */
  dismissible?: boolean;
  /**
   * Caps the panel at 80vh and scrolls the body inside it, instead of letting a long dialog grow
   * past the window. For lists and editors; a short form does not want it.
   */
  scroll?: boolean;
  /**
   * With `scroll`, hold the panel at its full height instead of shrinking to fit. For the ones whose
   * body is an editable table — otherwise the whole dialog jumps every time a row is added.
   */
  fill?: boolean;
  /** Controls in the header, before the close button. */
  toolbar?: ReactNode;
  /** Right-aligned action row. Put a `Button` per action here, primary last. */
  footer?: ReactNode;
  children: ReactNode;
}) {
  const t = useT();
  const { titleId, dialogProps } = useDialog();

  useEffect(() => {
    if (!dismissible) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [dismissible, onClose]);

  return (
    <div
      className="fixed inset-0 z-50 flex items-start justify-center bg-[color-mix(in_oklab,black_calc(var(--cf-overlay-scrim)*100%),transparent)] pt-24"
      onClick={dismissible ? onClose : undefined}
    >
      <div
        {...dialogProps}
        // The panel is inside the scrim, so without this every click on the dialog would dismiss it.
        onClick={(event) => event.stopPropagation()}
        className={`flex flex-col ${WIDTH[size]} max-w-[92vw] overflow-hidden rounded-[var(--radius-card)] border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] shadow-[var(--cf-shadow)]${
          scroll ? " max-h-[80vh]" : ""
        }${scroll && fill ? " h-[80vh]" : ""}`}
      >
        <div className="flex shrink-0 items-center gap-2 border-b border-[var(--cf-border)] px-4 py-3">
          {Icon && <Icon size={16} className="shrink-0 text-[var(--cf-text-muted)]" aria-hidden />}
          <div className="min-w-0 flex-1">
            {/* A real heading, not a styled div: it is what `aria-labelledby` points at. */}
            <h2 id={titleId} className="truncate text-body font-semibold text-[var(--cf-text)]">
              {titleText ?? (title ? t(title, titleParams) : "")}
            </h2>
            {subtitle && (
              <p className="truncate text-badge text-[var(--cf-text-muted)]">{subtitle}</p>
            )}
          </div>
          <div className="flex shrink-0 items-center gap-1">
            {toolbar}
            {dismissible && <IconButton label="common.close" icon={X} onClick={onClose} />}
          </div>
        </div>

        <div className={`px-4 py-4${scroll ? " min-h-0 flex-1 overflow-auto" : ""}`}>{children}</div>

        {footer && (
          <div className="flex shrink-0 justify-end gap-2 border-t border-[var(--cf-border)] px-4 py-3">
            {footer}
          </div>
        )}
      </div>
    </div>
  );
}

/**
 * The sizes the app actually uses, named. The steps come from what the modals were already set to
 * as arbitrary Tailwind classes — `max-w-2xl` through `max-w-5xl` — so the names replace the
 * guessing without changing any panel's width by more than a few pixels.
 */
export type ModalSize = "sm" | "md" | "lg" | "xl" | "2xl" | "3xl";

const WIDTH = {
  sm: "w-[380px]",
  md: "w-[460px]",
  lg: "w-[672px]",
  xl: "w-[768px]",
  "2xl": "w-[896px]",
  "3xl": "w-[1024px]",
} as const satisfies Record<ModalSize, string>;
