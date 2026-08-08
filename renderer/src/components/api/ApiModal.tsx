import { type ReactNode } from "react";
import type { LucideIcon } from "lucide-react";
import { Modal, type ModalSize } from "../common/Modal";

/**
 * The API client's modals, on the shared `Modal`.
 *
 * This used to be its own shell — the same backdrop, Escape handler and focus trap as everyone
 * else's, lifted into one place because six of these shipped at once. `Modal` is now that place for
 * the whole app, and it grew the two things this had that it did not: a `subtitle`, and a body that
 * scrolls inside an 80vh panel.
 *
 * What survives is this thin adapter, because the six callers speak in already-translated strings
 * (their titles carry collection and environment names) while `Modal` takes a `TranslationKey`.
 * `titleText` is the door for exactly that. Keeping the adapter also keeps the six diffs honest:
 * they change shell, not layout.
 */
export function ApiModal({
  icon,
  title,
  subtitle,
  size = "lg",
  fill = false,
  busy = false,
  onClose,
  toolbar,
  footer,
  children,
}: {
  icon: LucideIcon;
  title: string;
  subtitle?: string | undefined;
  size?: ModalSize;
  /** Hold the panel at full height — for the ones whose body is an editable table. */
  fill?: boolean;
  /** Locks the exits: an import or a collection run must not be dismissed by a stray click, because
   *  the work would carry on with nothing left showing it. */
  busy?: boolean;
  onClose: () => void;
  /** Rendered at the right of the header, before the close button. */
  toolbar?: ReactNode;
  footer?: ReactNode;
  children: ReactNode;
}) {
  return (
    <Modal
      titleText={title}
      {...(subtitle ? { subtitle } : {})}
      icon={icon}
      size={size}
      scroll
      fill={fill}
      dismissible={!busy}
      onClose={onClose}
      {...(toolbar ? { toolbar } : {})}
      {...(footer ? { footer } : {})}
    >
      {children}
    </Modal>
  );
}

/** Every text/number input in these modals; keeps the border + focus ring identical. */
export function Field({
  id,
  ariaLabel,
  value,
  onChange,
  placeholder,
  type = "text",
  disabled,
  mono = false,
  className = "",
}: {
  /** Set it when a separate `<label htmlFor>` names this input instead of wrapping it. */
  id?: string;
  /**
   * Names the input where no `<label>` can reach it — the cookie and variable grids are CSS grids,
   * so their column headings are visual only and every cell was reaching a screen reader unnamed.
   */
  ariaLabel?: string;
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  type?: "text" | "password" | "number";
  disabled?: boolean;
  mono?: boolean;
  className?: string;
}) {
  return (
    <input
      {...(id ? { id } : {})}
      {...(ariaLabel ? { "aria-label": ariaLabel } : {})}
      type={type}
      value={value}
      disabled={disabled}
      placeholder={placeholder}
      onChange={(e) => onChange(e.target.value)}
      className={`w-full rounded-md border border-[var(--cf-border)] bg-transparent px-2 py-1.5 text-ui outline-none focus:border-[var(--cf-accent)] disabled:opacity-50 ${
        mono ? "font-mono" : ""
      } ${className}`}
    />
  );
}

/** Label + control on one row, the density the settings panes use. */
export function Row({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label className="flex items-center gap-3 py-1">
      <span className="min-w-0 flex-1">
        <span className="block text-ui text-[var(--cf-text)]">{label}</span>
        {hint && <span className="block text-badge text-[var(--cf-text-muted)]">{hint}</span>}
      </span>
      <span className="flex w-[180px] shrink-0 justify-end">{children}</span>
    </label>
  );
}
