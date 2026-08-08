/**
 * Turns a React `useId()` value into a CSS `<dashed-ident>`.
 *
 * `Tooltip` and `RowActions` position themselves with CSS anchor positioning: the trigger declares
 * `anchor-name` and the floating element declares `position-anchor`, and the browser keeps them
 * together — no portal, no `getBoundingClientRect`, no scroll listener, and no clipping by an
 * ancestor's `overflow: hidden`, because the popover lives in the top layer. That is the whole
 * reason these two primitives are ~40 lines each instead of ~100 like `Select`.
 *
 * The catch is that the two ends have to agree on a name that is unique per instance, and React's
 * `useId()` emits `«r1»`-style values whose colons are not legal in a CSS identifier. Passing one
 * straight through produces no error anywhere — the declaration is simply dropped as invalid and
 * the tooltip renders in the corner of the screen. Hence this function, and hence its test.
 */

/** Anything that is not a letter, a digit, a hyphen or an underscore is not allowed in an ident. */
const ILLEGAL = /[^a-zA-Z0-9_-]/g;

/**
 * @param prefix short, human-readable tag for the kind of anchor (`"tooltip"`, `"row-actions"`) —
 *   it only exists to make the computed styles legible in devtools.
 * @param id the value from `useId()`.
 */
export function anchorName(prefix: string, id: string): string {
  return `--${prefix.replace(ILLEGAL, "-")}-${id.replace(ILLEGAL, "-")}`;
}
