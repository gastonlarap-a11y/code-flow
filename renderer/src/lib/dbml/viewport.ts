import type { Point, Rect } from "./layout";

/**
 * Pan and zoom for the schema canvas, as pure functions (DBML-008).
 *
 * A screen point is `world × scale + offset`. Kept out of the component so the one property worth
 * testing — zooming keeps the point under the cursor still — is asserted rather than eyeballed.
 */
export interface Viewport {
  x: number;
  y: number;
  scale: number;
}

export interface Size {
  width: number;
  height: number;
}

export const MIN_SCALE = 0.2;
export const MAX_SCALE = 2;
export const IDENTITY: Viewport = { x: 0, y: 0, scale: 1 };

/** Breathing room kept around the diagram when it is fitted to the view. */
export const FIT_PADDING = 48;

export function clampScale(scale: number): number {
  return Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale));
}

/** Where a screen point falls on the diagram. */
export function toWorld(view: Viewport, screen: Point): Point {
  return { x: (screen.x - view.x) / view.scale, y: (screen.y - view.y) / view.scale };
}

/** Zooms by `factor` around `screen`, so whatever is under that point stays under it. */
export function zoomAt(view: Viewport, screen: Point, factor: number): Viewport {
  const scale = clampScale(view.scale * factor);
  const anchor = toWorld(view, screen);
  return { scale, x: screen.x - anchor.x * scale, y: screen.y - anchor.y * scale };
}

/**
 * The view that shows all of `bounds` inside `size`, centred.
 *
 * Never zooms in past 1: fitting a two-table schema into a wide window should not blow the cards up
 * to twice their size — it should show them at their size, centred.
 */
export function fitBounds(bounds: Rect | null, size: Size, padding = FIT_PADDING): Viewport {
  if (bounds === null || size.width <= 0 || size.height <= 0 || bounds.width <= 0 || bounds.height <= 0) {
    return IDENTITY;
  }
  const available = { width: Math.max(1, size.width - padding * 2), height: Math.max(1, size.height - padding * 2) };
  const scale = clampScale(Math.min(available.width / bounds.width, available.height / bounds.height, 1));
  return {
    scale,
    x: (size.width - bounds.width * scale) / 2 - bounds.x * scale,
    y: (size.height - bounds.height * scale) / 2 - bounds.y * scale,
  };
}
