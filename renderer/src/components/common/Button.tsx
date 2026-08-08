import { Loader2, type LucideIcon } from "lucide-react";
import type { ButtonHTMLAttributes, ReactNode } from "react";
import { Tooltip } from "./Tooltip";
import { buttonStyle, type ControlSize, type ControlVariant } from "../../lib/ui/controlStyles";

type NativeButtonProps = Omit<ButtonHTMLAttributes<HTMLButtonElement>, "className" | "children">;

/**
 * The app's button. Every visual decision lives in `lib/ui/controlStyles.ts`, which is where the
 * 38 drifted copies of "the primary button" are reconciled and where the hit-target and icon floors
 * are enforced by a test.
 *
 * `children` is required and is the visible label. A button that shows only an icon is a different
 * component — `IconButton` — because it needs a label prop it cannot do without.
 */
export function Button({
  variant = "secondary",
  size = "md",
  icon: Icon,
  pending = false,
  tooltip,
  disabled,
  children,
  className,
  ...rest
}: {
  variant?: ControlVariant;
  size?: ControlSize;
  /** Optional leading icon; its size follows `size` rather than being chosen per call site. */
  icon?: LucideIcon;
  /**
   * In-flight: swaps the icon for a spinner and disables the button, so the second click on
   * "Commit" cannot start a second commit. Every place in the app that does this today re-implements
   * it, and a few forget the disabling half.
   */
  pending?: boolean;
  /**
   * Extra explanation on hover — most usefully *why* the button is disabled, which the label itself
   * cannot say. Already translated.
   *
   * Wrapped in a span rather than anchored on the button, because a disabled button fires no
   * pointer events at all: anchoring there means the one state that needs an explanation is the one
   * state that cannot show it.
   */
  tooltip?: string;
  children: ReactNode;
  /** Appended, not merged — for layout only (`w-full`, `mt-2`), never to restyle the button. */
  className?: string;
} & NativeButtonProps) {
  const style = buttonStyle(variant, size);
  const Leading = pending ? Loader2 : Icon;

  const button = (
    <button
      type="button"
      disabled={disabled || pending}
      aria-busy={pending || undefined}
      className={`${style.className}${className ? ` ${className}` : ""}`}
      {...rest}
    >
      {Leading && (
        <Leading size={style.iconSize} className={pending ? "animate-spin" : undefined} aria-hidden />
      )}
      {children}
    </button>
  );

  if (!tooltip) return button;

  return (
    <Tooltip label={tooltip}>
      <span className={`inline-flex${className?.includes("w-full") ? " w-full" : ""}`}>{button}</span>
    </Tooltip>
  );
}
