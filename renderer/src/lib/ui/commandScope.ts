import type { PaletteScope } from "../../state/uiStore";

/**
 * The prefix language of the command bar: one field that stands in for three pickers.
 *
 * This app had a command palette, a "go to file" palette and a branch switcher — three modals over
 * the same `PickerModal`, three shortcuts to remember, three answers to "where do I type to get
 * somewhere". A prefix picks the list instead, the way Linear, Raycast and VS Code all do it.
 *
 * `>` and `@` follow VS Code because that is where the muscle memory already is. Branches take `#`,
 * which the redesign proposal wrote as `⎇`: a character nobody can type is not a prefix, and `#` is
 * the only remaining key with an obvious association. That does spend the prefix the proposal had
 * pencilled in for work items (§7); when that module lands it needs a different one.
 */
export const SCOPE_PREFIXES: readonly { prefix: string; scope: PaletteScope }[] = [
  { prefix: ">", scope: "commands" },
  { prefix: "@", scope: "files" },
  { prefix: "#", scope: "branches" },
];

export interface ParsedQuery {
  /** The list to search. `all` when no prefix was typed. */
  scope: PaletteScope;
  /** The query with the prefix stripped — what actually gets matched. */
  term: string;
  /** The prefix that produced the scope, if any. Rendered as a chip in front of the field. */
  prefix: string | null;
}

/**
 * Split a raw field value into the list to search and the text to search it for.
 *
 * Only a *leading* prefix counts. A `#` inside a branch name (`fix/#123-crash`) is part of what is
 * being searched for, not a second scope change, so this looks at the first character and nothing
 * else.
 */
export function parseQuery(raw: string): ParsedQuery {
  for (const { prefix, scope } of SCOPE_PREFIXES) {
    if (raw.startsWith(prefix)) {
      return { scope, term: raw.slice(prefix.length).trimStart(), prefix };
    }
  }
  return { scope: "all", term: raw, prefix: null };
}

/**
 * The field value that puts the bar in `scope`, preserving what was already typed.
 *
 * Used when a scope arrives from somewhere other than the keyboard — a shortcut, or a click on the
 * branch pill — so the field shows the same prefix the user would have typed to get there. A scope
 * with no prefix of its own (`workspaces`, `projects`) is reached only by shortcut and keeps a bare
 * field rather than inventing a character to display.
 */
export function queryForScope(scope: PaletteScope, term = ""): string {
  const match = SCOPE_PREFIXES.find((p) => p.scope === scope);
  return match ? match.prefix + term : term;
}
