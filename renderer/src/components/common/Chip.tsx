import type { LucideIcon } from "lucide-react";
import type { ReactNode } from "react";

/**
 * A small label that states a fact: a status, a count, a provider.
 *
 * The markup existed already, hand-typed in a dozen places — `rounded-full`, a soft accent fill,
 * `text-badge` — and every copy was one token away from being a different shade of the same idea.
 * This is that shape once. It is not a button and never becomes one: a chip that does something is
 * a `Button`, because the thing that tells you a control is a control is that it looks like one.
 *
 * `tone` names *what the chip means*, not what colour it is. `neutral` is the default because most
 * chips are counts and names; the semantic tones are for the four states the palette already
 * defines, and `accent` is for identity — the provider a repository belongs to, say.
 */
export type ChipTone = "neutral" | "accent" | "success" | "warning" | "danger" | "info";

/**
 * How loudly the tone is painted.
 *
 * `soft` is the default and what every chip in the app wants: a tinted wash that sits quietly on a
 * card. `outline` is for a chip that must not read as a status — a hairline and muted ink, so it
 * annotates rather than announces. `solid` is the loudest and, honestly, has no call site yet: it
 * is here because the proposal names the three, and the first thing that needs a chip to compete
 * with a button will reach for it.
 */
export type ChipVariant = "soft" | "solid" | "outline";

/** Soft fill plus its ink, per tone. The semantic four mix their own token so a chip stays legible
 * on both `--cf-surface` and `--cf-surface-raised` without a second variable per tone. */
const SOFT: Record<ChipTone, string> = {
  neutral: "bg-black/[0.05] text-[var(--cf-text-muted)] dark:bg-white/[0.08]",
  accent: "bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]",
  success: "bg-[color-mix(in_oklab,var(--cf-success)_16%,transparent)] text-[var(--cf-success)]",
  warning: "bg-[color-mix(in_oklab,var(--cf-warning)_16%,transparent)] text-[var(--cf-warning)]",
  danger: "bg-[color-mix(in_oklab,var(--cf-danger)_16%,transparent)] text-[var(--cf-danger)]",
  info: "bg-[color-mix(in_oklab,var(--cf-info)_16%,transparent)] text-[var(--cf-info)]",
};

/**
 * Full-strength fill, with the text colour that survives on it.
 *
 * `accent` uses the `--cf-accent-solid` / `--cf-accent-on-solid` pair rather than white, for the
 * reason the design rules give: white on the ink accent measured 1.67–4.47:1 across the eight
 * options. The semantic four are dark enough at full strength for white in both themes.
 */
const SOLID: Record<ChipTone, string> = {
  neutral: "bg-[var(--cf-text-muted)] text-[var(--cf-surface)]",
  accent: "bg-[var(--cf-accent-solid)] text-[var(--cf-accent-on-solid)]",
  success: "bg-[var(--cf-success)] text-white",
  warning: "bg-[var(--cf-warning)] text-black",
  danger: "bg-[var(--cf-danger)] text-white",
  info: "bg-[var(--cf-info)] text-white",
};

/** A hairline in the tone's own colour, no fill. `neutral` borrows the app's border token so it
 * matches every other hairline instead of inventing a grey. */
const OUTLINE: Record<ChipTone, string> = {
  neutral: "border border-[var(--cf-border)] text-[var(--cf-text-muted)]",
  accent: "border border-[var(--cf-accent)] text-[var(--cf-accent)]",
  success: "border border-[var(--cf-success)] text-[var(--cf-success)]",
  warning: "border border-[var(--cf-warning)] text-[var(--cf-warning)]",
  danger: "border border-[var(--cf-danger)] text-[var(--cf-danger)]",
  info: "border border-[var(--cf-info)] text-[var(--cf-info)]",
};

const VARIANT: Record<ChipVariant, Record<ChipTone, string>> = {
  soft: SOFT,
  solid: SOLID,
  outline: OUTLINE,
};

export function Chip({
  children,
  tone = "neutral",
  variant = "soft",
  icon: Icon,
  className,
}: {
  children: ReactNode;
  tone?: ChipTone;
  variant?: ChipVariant;
  /** Sits before the label at 11px, matching the type scale rather than the 14px of controls. */
  icon?: LucideIcon;
  /** Layout only — margins and shrink behaviour. Colour belongs to `tone`. */
  className?: string;
}) {
  return (
    <span
      className={`inline-flex shrink-0 items-center gap-1 rounded-full px-2 py-0.5 text-badge font-medium ${
        VARIANT[variant][tone]
      }${className ? ` ${className}` : ""}`}
    >
      {Icon && <Icon size={11} aria-hidden />}
      {children}
    </span>
  );
}
