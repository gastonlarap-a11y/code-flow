/**
 * The geometry of a circular countdown, as numbers a `<circle>` can take.
 *
 * The only part of the fetch ring worth testing: an SVG cannot be asserted on in a suite with no
 * DOM, but "what happens when the interval is zero" and "what happens the second after the user
 * shortens it" can be, and both have a wrong answer that looks fine until you see it — a ring drawn
 * inside out, or one that overshoots its own circumference.
 */
export interface RingGeometry {
  /** Circumference, for `stroke-dasharray`. */
  readonly dashArray: number;
  /** How much of it to hide, for `stroke-dashoffset`. Zero draws the full circle. */
  readonly dashOffset: number;
}

/**
 * @param radius the circle's radius in px
 * @param remaining seconds left
 * @param total seconds the countdown started from
 */
export function ringGeometry(radius: number, remaining: number, total: number): RingGeometry {
  const dashArray = 2 * Math.PI * radius;
  // A total of zero means auto-fetch is off and there is nothing to draw. It is also the divisor,
  // so guarding it is not only about the visuals.
  if (total <= 0) return { dashArray, dashOffset: dashArray };
  // Clamped both ways: `remaining` briefly exceeds `total` right after the preference is lowered,
  // because the running countdown keeps its old value until the next tick — unclamped, that draws
  // a negative offset, which renders as an arc going the wrong way round.
  const elapsed = Math.min(Math.max(total - remaining, 0), total);
  return { dashArray, dashOffset: dashArray * (1 - elapsed / total) };
}
