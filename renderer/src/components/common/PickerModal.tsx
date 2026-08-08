import type { KeyboardEvent, Ref, ReactNode } from "react";
import { useDialog } from "../../lib/useDialog";

/**
 * The search-first dialog: type, see matches, pick one.
 *
 * Three surfaces were this shape — the command palette, "go to file", and the branch switcher — and
 * none of them fits `Modal`. `Modal` opens with an `<h2>` taken from its title, and these have no
 * heading on purpose: the field *is* the header, and the dialog's accessible name is what you are
 * searching for. Wrapping them would mean inventing a title for each.
 *
 * So they shared a shape and copied it, and the copies had drifted: three widths, two top offsets,
 * and two different surface tokens for the same floating panel. This is the one copy.
 *
 * `Escape` is handled here because all three did it on the input and one of them also did it on the
 * panel. Everything else about the list — grouping, the active row, `Enter` — stays with the caller,
 * which is where the results are.
 */
export function PickerModal({
  placeholder,
  value,
  onValueChange,
  onKeyDown,
  size = "md",
  listRef,
  onClose,
  children,
}: {
  /** Fills the field and names the dialog, so the two can never disagree. */
  placeholder: string;
  value: string;
  onValueChange: (value: string) => void;
  /** For `Enter` and the arrow keys — `Escape` is already handled. */
  onKeyDown?: (event: KeyboardEvent<HTMLInputElement>) => void;
  size?: PickerSize;
  /**
   * The scroll container, for a picker that moves an active row with the arrow keys and has to keep
   * it in view. Any keyboard-navigable list needs it; only "go to file" is one today.
   */
  listRef?: Ref<HTMLDivElement>;
  onClose: () => void;
  /** The result list. Scrolls inside the panel; the panel does not grow past 60vh. */
  children: ReactNode;
}) {
  const { dialogProps } = useDialog({ label: placeholder });

  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center bg-[color-mix(in_oklab,black_calc(var(--cf-overlay-scrim)*100%),transparent)] pt-24" onClick={onClose}>
      <div
        {...dialogProps}
        onClick={(event) => event.stopPropagation()}
        className={`flex max-h-[60vh] ${WIDTH[size]} max-w-[92vw] flex-col overflow-hidden rounded-[var(--radius-card)] border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] shadow-[var(--cf-shadow)]`}
      >
        <div className="flex shrink-0 items-center gap-2 border-b border-[var(--cf-border)] px-3 py-2">
          <input
            autoFocus
            value={value}
            onChange={(event) => onValueChange(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === "Escape") onClose();
              else onKeyDown?.(event);
            }}
            placeholder={placeholder}
            className="flex-1 bg-transparent text-body outline-none"
          />
        </div>
        <div ref={listRef} className="min-h-0 flex-1 overflow-auto p-1.5">
          {children}
        </div>
      </div>
    </div>
  );
}

/** The three widths the pickers were already using, named. */
export type PickerSize = "sm" | "md" | "lg";

const WIDTH = {
  sm: "w-[384px]",
  md: "w-[420px]",
  lg: "w-[576px]",
} as const satisfies Record<PickerSize, string>;

/**
 * The heading above a group of results. Shared for the same reason the shell is: all three wrote it
 * out, at the same size, with the same casing.
 */
export function PickerGroupLabel({ children }: { children: ReactNode }) {
  return (
    <p className="px-2 py-1 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
      {children}
    </p>
  );
}
