import { Loader2, type LucideIcon } from "lucide-react";
import type { ButtonHTMLAttributes } from "react";
import { Tooltip } from "./Tooltip";
import { iconButtonStyle, type ControlSize, type ControlVariant } from "../../lib/ui/controlStyles";
import { useT } from "../../state/languageStore";
import { useShortcutChord } from "../../lib/useShortcutHint";
import type { TranslationKey } from "../../lib/i18n/translations";
import type { ShortcutId } from "../../lib/shortcuts";

type NativeButtonProps = Omit<
  ButtonHTMLAttributes<HTMLButtonElement>,
  "className" | "children" | "aria-label" | "title"
>;

/**
 * The only sanctioned icon-only control.
 *
 * The audit found 19 icon buttons in the app with neither a `title` nor an `aria-label` — 14 of them
 * the close `X` on a modal. To a screen reader those are a button called "button". The fix is not a
 * review checklist, it is this signature: `label` is required and typed as a `TranslationKey`, so an
 * unlabelled icon button does not compile, and the same string feeds both the tooltip and the
 * `aria-label`. Accessibility does not depend on the tooltip rendering.
 *
 * Sizes come from `lib/ui/controlStyles.ts`: 24px in dense zones, 28px in decide zones, icons at
 * 14px and 16px. The app's most common icon button today is a 20px box holding a 12px glyph.
 */
export function IconButton({
  label,
  labelParams,
  icon: Icon,
  size = "sm",
  variant = "ghost",
  shortcut,
  shortcutLabel,
  pending = false,
  tooltip = true,
  active,
  disabled,
  className,
  ...rest
}: {
  /** Required. Names the control for assistive tech and fills the tooltip. */
  label: TranslationKey;
  /** Interpolations for `label`, when it carries `{placeholders}`. */
  labelParams?: Record<string, string | number>;
  icon: LucideIcon;
  size?: ControlSize;
  variant?: ControlVariant;
  /** Appends this action's live key binding to the tooltip, re-read whenever the user rebinds it. */
  shortcut?: ShortcutId;
  /**
   * An already-formatted chord, for the bindings this app does not own — Monaco holds ⌘P, ⇧⌘F and
   * the rest of `EDITOR_RESERVED`, so they have no `ShortcutId` to look up. Read it from
   * `reservedChordFor` rather than typing it, which is what the editor rail used to do.
   * `shortcut` wins when both are given.
   */
  shortcutLabel?: string;
  /** In-flight: spinner plus disabled, same contract as `Button`. */
  pending?: boolean;
  /**
   * Suppresses the bubble only. The `aria-label` stays either way — use this when the control sits
   * next to text that already says the same thing, never to opt out of labelling it.
   */
  tooltip?: boolean;
  /**
   * For a button that toggles something on and off — a panel, a filter. Paints the accent and, more
   * importantly, announces `aria-pressed`, so the state is not conveyed by colour alone.
   * Leave it `undefined` for a button that just does something.
   */
  active?: boolean;
  /** Appended for layout only. */
  className?: string;
} & NativeButtonProps) {
  const t = useT();
  const chord = useShortcutChord();
  const style = iconButtonStyle(variant, size);
  const text = t(label, labelParams);
  const Glyph = pending ? Loader2 : Icon;

  return (
    <Tooltip label={text} shortcut={shortcut ? chord(shortcut) : shortcutLabel} disabled={!tooltip}>
      <button
        type="button"
        aria-label={text}
        aria-pressed={active}
        disabled={disabled || pending}
        aria-busy={pending || undefined}
        // The accent goes last so it wins over the variant's own colour regardless of the order
        // Tailwind happens to emit the two utilities in.
        className={`${style.className}${active ? " !text-[var(--cf-accent)]" : ""}${className ? ` ${className}` : ""}`}
        {...rest}
      >
        <Glyph size={style.iconSize} className={pending ? "animate-spin" : undefined} aria-hidden />
      </button>
    </Tooltip>
  );
}
