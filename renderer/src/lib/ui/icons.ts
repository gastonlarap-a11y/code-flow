import { forwardRef, createElement } from "react";
import { Square, type LucideIcon, type LucideProps } from "lucide-react";

/**
 * "Stop a running process", as one glyph.
 *
 * The icon dictionary (`docs/UX-REDESIGN.md` §II.3) reserves the *filled* square for stopping, and
 * an outlined one means something else entirely — it is what Windows draws for "maximize", which is
 * why `WindowControls` is allowed to use it. The distinction lives in a `fill` that four different files
 * were each writing by hand (`DebugPanel`, `RunnerModal`, `ChatPanel`, the API request builder),
 * with one of them omitting it and one using `X` instead.
 *
 * Exporting the finished glyph makes the rule mechanical: a stop button takes `StopSquare`, and
 * nothing else in the app fills a square.
 */
export const StopSquare: LucideIcon = forwardRef<SVGSVGElement, LucideProps>((props, ref) =>
  createElement(Square, { ...props, ref, fill: "currentColor" }),
);

StopSquare.displayName = "StopSquare";
