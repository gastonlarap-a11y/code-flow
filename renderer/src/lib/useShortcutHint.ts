import { useShortcutsStore, bindingFor } from "../state/shortcutsStore";
import { chordLabel } from "./keys";
import type { ShortcutId } from "./shortcuts";

/**
 * Returns a formatter for `title` tooltips that appends an action's current key combination —
 * `"Toggle sidebar (⌘B)"`. Reading the binding through the store rather than baking it into the
 * string keeps every tooltip in sync the moment the user rebinds something in settings.
 */
export function useShortcutHint(): (id: ShortcutId, label: string) => string {
  const overrides = useShortcutsStore((s) => s.overrides);
  return (id, label) => {
    const chord = bindingFor(id, overrides);
    return chord ? `${label} (${chordLabel(chord)})` : label;
  };
}

/**
 * The same live binding, unformatted — `"⌘B"`, or `null` when the action has none.
 *
 * `Tooltip` renders the shortcut as a keycap chip beside the label rather than as parenthesised
 * text, and the control's `aria-label` stays the plain label: a screen reader announcing
 * "Toggle sidebar Cmd B" reads the binding as part of the name of the thing.
 */
export function useShortcutChord(): (id: ShortcutId) => string | null {
  const overrides = useShortcutsStore((s) => s.overrides);
  return (id) => {
    const chord = bindingFor(id, overrides);
    return chord ? chordLabel(chord) : null;
  };
}
