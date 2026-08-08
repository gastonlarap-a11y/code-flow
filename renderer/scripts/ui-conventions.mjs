import { readFileSync, readdirSync } from "node:fs";
import { join, relative } from "node:path";

/**
 * The two conventions this repo can check mechanically, and the walk that finds them.
 *
 * Kept apart from the test so the same scan can be run by hand while migrating an area — see
 * `ui-conventions.test.mjs` for what it is for and why the exemption list is allowed to exist.
 */

/** Every `.tsx` under a directory, depth-first, as repo-relative paths. */
export function componentFiles(root) {
  const out = [];
  const walk = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const path = join(dir, entry.name);
      if (entry.isDirectory()) walk(path);
      else if (entry.name.endsWith(".tsx")) out.push(path);
    }
  };
  walk(root);
  return out.map((path) => relative(root, path).replaceAll("\\", "/")).sort();
}

/**
 * A size picked per component instead of from the scale.
 *
 * Two shapes, because closing only the first left the door open: `text-[13px]` is the arbitrary
 * value, and `text-sm` is Tailwind's own step, which is 14px — a sixth size the scale does not have.
 * Twenty-one of the app's twenty-five `text-sm` were in Settings, i.e. the loophole was being used.
 */
const PIXEL_TEXT = /text-\[\d+(?:\.\d+)?px\]|\btext-(?:xs|sm|base|lg|xl|2xl)\b/g;

/** Where a JSX tag opens. Lowercase names are DOM elements; capitalised ones are components. */
const TAG_OPEN = /<([A-Za-z][\w.]*)/g;

/** A `title` attribute — the native tooltip when it sits on a DOM element. */
const TITLE_ATTR = /\btitle=/g;

/**
 * Native `title` attributes on DOM elements.
 *
 * An attribute belongs to the nearest tag opened before it, so the owner is the last `<tag` at a
 * lower offset. `title` on a capitalised tag is a component's own prop (`Modal`, `EmptyState`,
 * `CollapsibleSection` all take one) and is not a tooltip at all.
 *
 * `<iframe title>` is exempt because it is not a tooltip either: the HTML spec makes it the frame's
 * accessible name, and taking it away would leave the frame unnamed.
 */
export function nativeTitles(source) {
  const opens = [...source.matchAll(TAG_OPEN)];
  const hits = [];
  for (const match of source.matchAll(TITLE_ATTR)) {
    const at = match.index;
    let owner = null;
    for (const open of opens) {
      if (open.index > at) break;
      owner = open[1];
    }
    if (owner && owner !== "iframe" && owner[0] === owner[0].toLowerCase()) {
      hits.push({ tag: owner, line: source.slice(0, at).split("\n").length });
    }
  }
  return hits;
}

export function pixelTextSizes(source) {
  return [...source.matchAll(PIXEL_TEXT)].map((match) => ({
    value: match[0],
    line: source.slice(0, match.index).split("\n").length,
  }));
}

export function readSource(root, file) {
  return readFileSync(join(root, file), "utf8");
}
