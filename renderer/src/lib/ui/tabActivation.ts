import { menuKeyAction, type MenuItemState } from "./menuNavigation";

/**
 * Whether an arrow key that lands on a tab also selects it.
 *
 * The APG allows selection to follow focus only when the panel is already loaded and appears with no
 * perceptible delay. `manual` is for everything else — including, in this app, a tab whose panel
 * *starts a Claude run* when it mounts, where an arrow key would otherwise spend money.
 */
export type TabActivation = "automatic" | "manual";

/** What the tab strip should do with a key. */
export type TabKeyResult =
  | { kind: "none" }
  | { kind: "focus"; index: number }
  | { kind: "focus-and-select"; index: number }
  | { kind: "select"; index: number };

/**
 * The one place the two activation modes differ, kept out of the component so it can be tested —
 * renderer tests have no DOM, so a rendered tab strip cannot be driven with a keyboard.
 *
 * `cursor` is where focus currently is, which under manual activation is not necessarily the
 * selected tab. Movement and disabled-skipping come from `menuNavigation`; this only decides whether
 * a move also commits.
 */
export function tabKeyResult(
  key: string,
  tabs: readonly MenuItemState[],
  cursor: number,
  activation: TabActivation,
): TabKeyResult {
  const action = menuKeyAction(key, tabs, cursor, "horizontal");

  if (action.kind === "move") {
    return activation === "automatic"
      ? { kind: "focus-and-select", index: action.index }
      : { kind: "focus", index: action.index };
  }

  // Enter/Space only mean something when focus can sit on an unselected tab, which is exactly what
  // manual activation allows. Under automatic there is nothing left to commit.
  if (action.kind === "activate" && activation === "manual") {
    return { kind: "select", index: action.index };
  }

  return { kind: "none" };
}
