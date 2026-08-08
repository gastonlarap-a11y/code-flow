/**
 * Where a control's size and variant stop being a per-component opinion.
 *
 * The audit counted 388 `<button>` elements across ~100 files and no shared primitive: 38 near-copies
 * of "the primary button" that had drifted apart in padding (`px-2` / `px-2.5` / `px-3`), in height,
 * and even in what disabled looks like (`opacity-40` in some, `opacity-50` in others). The most
 * common icon-button box was 20px, and a few were 16px — under the 24px floor WCAG 2.2 SC 2.5.8
 * sets for a pointer target.
 *
 * This module is the answer, and it lives in `lib/` rather than next to the components for a
 * practical reason: renderer tests run in `environment: "node"` with no jsdom, and `.test.tsx` is
 * silently skipped by the Vitest include glob. Pure functions here are testable; JSX is not. So the
 * minimums are asserted by `controlStyles.test.ts` on every variant/size pair, which means a future
 * edit that shrinks a box below 24px fails CI instead of reaching a user.
 */

/**
 * Visual weight, in the order a user should read them: one primary action per surface.
 *
 * The last three are not weights but *outcomes* — approve, request changes, close a pull request —
 * where the colour is part of what the button means. They live here because the alternative is what
 * the PR footer had: a local `PR_ACTION_TONES` map, spelled out statically with a comment about
 * Tailwind never generating an interpolated `--cf-${tone}`.
 */
export type ControlVariant = "primary" | "secondary" | "ghost" | "danger" | "success" | "warning";

/**
 * `md` is for "decide" surfaces (Settings, modals, PR actions) where labels need room; `sm` is for
 * "work" surfaces (trees, toolbars, diffs) where density is the point. There is no third, smaller
 * step — that is the whole reason this type exists.
 */
export type ControlSize = "md" | "sm";

/** Hit-target floor in CSS pixels, per zone. Below 24 the control fails WCAG 2.2 SC 2.5.8. */
export const MIN_TARGET_PX = { md: 28, sm: 24 } as const;

/** Icon floor in CSS pixels. Anything under 14 is a badge glyph, not a control. */
export const MIN_ICON_PX = 14;

/** Every control is focusable-with-a-visible-ring and transitions its hover — no exceptions. */
const BASE =
  "cf-focusable cf-interactive inline-flex items-center justify-center " +
  "rounded-[var(--radius-control)] font-medium select-none " +
  "disabled:pointer-events-none disabled:opacity-50";

/**
 * Hover and active washes come from the overlay scale rather than from a per-file guess — the audit
 * found nine different opacities in use between 0.03 and 0.4. `currentColor` keeps the wash tied to
 * the control's own text colour, so it reads correctly on every one of the 24 code themes without a
 * light/dark branch.
 */
const GHOST_WASH =
  "hover:bg-[color-mix(in_oklab,currentColor_calc(var(--cf-overlay-hover)*100%),transparent)] " +
  "active:bg-[color-mix(in_oklab,currentColor_calc(var(--cf-overlay-active)*100%),transparent)]";

const VARIANT: Record<ControlVariant, string> = {
  // `brightness-110` rather than a second accent shade: the accent is user-chosen from eight
  // options and themes may not override it, so a hardcoded hover colour would fight the setting.
  //
  // The fill is `--cf-accent-solid` and not `--cf-accent`, and the text is `--cf-accent-on-solid`
  // and not `white`. This used to be white on the ink accent, which measured 4.47:1 at best and
  // 1.67:1 at worst across the eight options — a failure on six of eight in the light theme and on
  // all eight in the dark. See `state/accentStore.ts`, which stamps the pair, and its test.
  primary: "bg-[var(--cf-accent-solid)] text-[var(--cf-accent-on-solid)] hover:brightness-110",
  secondary: `border border-[var(--cf-border)] bg-[var(--cf-surface)] text-[var(--cf-text)] ${GHOST_WASH}`,
  ghost: `text-[var(--cf-text-muted)] hover:text-[var(--cf-text)] ${GHOST_WASH}`,
  // Destructive actions are never a bare icon and never share the ghost treatment: the colour is
  // the warning, and `Trash2` plus this variant is the only sanctioned pairing (icon dictionary).
  danger: `text-[var(--cf-danger)] ${GHOST_WASH}`,
  success: `border border-[var(--cf-border)] text-[var(--cf-success)] ${GHOST_WASH}`,
  warning: `border border-[var(--cf-border)] text-[var(--cf-warning)] ${GHOST_WASH}`,
};

/** Text-bearing buttons: height and padding, paired with a type-scale step (never `text-[Npx]`). */
const BUTTON_SIZE = {
  md: { className: "h-8 gap-2 px-3 text-body", icon: 16, target: 32 },
  sm: { className: "h-7 gap-1.5 px-2.5 text-ui", icon: 14, target: 28 },
} as const satisfies Record<ControlSize, { className: string; icon: number; target: number }>;

/** Icon-only buttons: a square box, sized to the floor for its zone. */
const ICON_SIZE = {
  md: { className: "h-7 w-7", icon: 16, target: 28 },
  sm: { className: "h-6 w-6", icon: 14, target: 24 },
} as const satisfies Record<ControlSize, { className: string; icon: number; target: number }>;

export interface ControlStyle {
  /** The full class string for the element. */
  className: string;
  /** Pixel size to pass to the lucide icon. */
  iconSize: number;
  /** The resulting hit target's shorter side, in CSS pixels. Asserted in tests, not rendered. */
  targetPx: number;
}

/** Resolves the classes for a text-bearing button. */
export function buttonStyle(variant: ControlVariant, size: ControlSize): ControlStyle {
  const step = BUTTON_SIZE[size];
  return {
    className: `${BASE} ${VARIANT[variant]} ${step.className}`,
    iconSize: step.icon,
    targetPx: step.target,
  };
}

/** Resolves the classes for an icon-only button. */
export function iconButtonStyle(variant: ControlVariant, size: ControlSize): ControlStyle {
  const step = ICON_SIZE[size];
  return {
    className: `${BASE} ${VARIANT[variant]} ${step.className}`,
    iconSize: step.icon,
    targetPx: step.target,
  };
}

/** Both size steps, so a test can enumerate them without restating the union. */
export const CONTROL_SIZES = ["md", "sm"] as const satisfies readonly ControlSize[];

/** Every variant, same reason. */
export const CONTROL_VARIANTS = [
  "primary",
  "secondary",
  "ghost",
  "danger",
  "success",
  "warning",
] as const satisfies readonly ControlVariant[];
