import { create } from "zustand";

/** The id `confirmAction`'s own affirmative button answers with. */
const CONFIRM = "confirm";

/** One way out of a dialog that has more than two. */
export interface ConfirmChoice {
  id: string;
  /** Already translated — this store does no i18n of its own. */
  label: string;
  variant?: "primary" | "danger" | "ghost";
}

interface ConfirmRequest {
  message: string;
  danger: boolean;
  /** Overrides the generic "Confirm" button label when naming the action is clearer. */
  confirmLabel?: string | undefined;
  /** Present only for `chooseAction`: one button per entry, plus Cancel. */
  choices?: ConfirmChoice[] | undefined;
  /** An outcome to read, not a decision to make: one button, no Cancel. */
  acknowledge?: boolean | undefined;
  /** `null` is "cancelled", by button, by Escape or by clicking the scrim. */
  resolve: (value: string | null) => void;
}

interface ConfirmState {
  request: ConfirmRequest | null;
  ask: (message: string, danger?: boolean, confirmLabel?: string) => Promise<boolean>;
  choose: (message: string, choices: ConfirmChoice[], danger?: boolean) => Promise<string | null>;
  tell: (message: string, buttonLabel: string) => Promise<void>;
  respond: (value: string | null) => void;
}

export const useConfirmStore = create<ConfirmState>((set, get) => ({
  request: null,

  ask: (message, danger = true, confirmLabel) =>
    new Promise<boolean>((resolve) => {
      set({ request: { message, danger, confirmLabel, resolve: (value) => resolve(value === CONFIRM) } });
    }),

  choose: (message, choices, danger = false) =>
    new Promise<string | null>((resolve) => {
      set({ request: { message, danger, choices, resolve } });
    }),

  tell: (message, buttonLabel) =>
    new Promise<void>((resolve) => {
      set({
        request: {
          message,
          danger: false,
          acknowledge: true,
          choices: [{ id: CONFIRM, label: buttonLabel, variant: "primary" }],
          resolve: () => resolve(),
        },
      });
    }),

  respond: (value) => {
    get().request?.resolve(value);
    set({ request: null });
  },
}));

/** Drop-in replacement for `window.confirm()` that pops the app's own styled modal instead
 * of the browser-native dialog — every discard/delete action in the app should route
 * through this rather than rolling its own confirm UI. */
export const confirmAction = (message: string, danger = true, confirmLabel?: string) =>
  useConfirmStore.getState().ask(message, danger, confirmLabel);

/**
 * The same dialog when the answer is not yes/no.
 *
 * Resolves to the chosen `id`, or `null` when the user cancels — and a cancel is a cancel, not a
 * failure: the caller returns quietly rather than reporting an error. Blocked checkouts are what
 * this exists for, where "stash it", "bring it along" and "never mind" are three different answers.
 */
export const chooseAction = (message: string, choices: ConfirmChoice[], danger = false) =>
  useConfirmStore.getState().choose(message, choices, danger);

/**
 * The same dialog for an outcome the user has to read rather than decide on — one button, no Cancel.
 *
 * For results that leave the app looking like nothing happened: a stash that applied over content
 * the branch already had empties the Changes panel and explains nothing, and a toast that fades in
 * five seconds is how that reads as lost work rather than as a no-op.
 */
export const tellUser = (message: string, buttonLabel: string) =>
  useConfirmStore.getState().tell(message, buttonLabel);

export { CONFIRM as CONFIRM_CHOICE_ID };
