import { useEffect, useState, type RefObject } from "react";

/** What the browser will move focus to with Tab, in document order. */
const FOCUSABLE = [
  "a[href]",
  "button:not([disabled])",
  "input:not([disabled])",
  "select:not([disabled])",
  "textarea:not([disabled])",
  '[tabindex]:not([tabindex="-1"])',
].join(",");


/**
 * Keeps Tab inside a modal, and gives focus back when it closes.
 *
 * Without this, tabbing past the last control in a dialog walks into the page behind it — the user
 * ends up editing a form they cannot see, under a backdrop that says the app is waiting for them.
 * A screen reader has the same problem in a worse form, which is why the dialogs that call this
 * also carry `role="dialog"` and `aria-modal`.
 *
 * Focus is captured on open and restored on close, so dismissing a dialog with Escape returns the
 * caret to whatever opened it rather than to the top of the document.
 *
 * **The opener is captured during render, and it has to be.** React applies `autoFocus` while
 * committing the panel, which is before any effect runs — so an effect that reads
 * `document.activeElement` reads the dialog's own field, and on close hands focus back to a node it
 * has just unmounted. Focus lands on `<body>` and a keyboard user starts again from the top of the
 * document. Every dialog in this app with an autofocused field had that bug, which is most of them;
 * it was found by tracing `focus()` in the running app, because renderer tests have no DOM.
 *
 * The capture is a state update during render — React's documented "adjust state when a prop
 * changes" — rather than a ref write, which the compiler rejects, or a global focus history, which
 * was tried and is not deterministic: anything that steals focus between the click and the dialog
 * mounting poisons it, which showed up as one failure in five live runs.
 *
 * The Tab listener is on the panel and in the bubble phase, so anything inside that handles Tab
 * itself — Monaco's editor, mainly — still wins.
 */
export function useFocusTrap(panel: RefObject<HTMLElement | null>, active = true): void {
  // `active` is `false` for the two dialogs that stay mounted and toggle (`ConfirmModal`,
  // `SettingsView`), so the capture keys off the transition rather than off mount alone.
  const [opener, setOpener] = useState<HTMLElement | null>(() =>
    active ? (document.activeElement as HTMLElement | null) : null,
  );
  const [wasActive, setWasActive] = useState(active);
  if (wasActive !== active) {
    setWasActive(active);
    if (active) setOpener(document.activeElement as HTMLElement | null);
  }

  useEffect(() => {
    if (!active) return;

    const element = panel.current;
    if (!element) return;

    // Only move focus if it is not already inside: a dialog with an autoFocus button has already
    // put it where the author wanted, and overriding that would undo a deliberate choice.
    if (!element.contains(document.activeElement)) {
      element.querySelector<HTMLElement>(FOCUSABLE)?.focus();
    }

    const handler = (event: KeyboardEvent) => {
      if (event.key !== "Tab" || event.defaultPrevented) return;

      const focusable = [...element.querySelectorAll<HTMLElement>(FOCUSABLE)].filter(
        (node) => node.offsetParent !== null || node === document.activeElement,
      );
      if (focusable.length === 0) return;

      const first = focusable[0]!;
      const last = focusable[focusable.length - 1]!;

      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    };

    element.addEventListener("keydown", handler);

    return () => {
      element.removeEventListener("keydown", handler);
      // Deferred a frame, because focus is being handed back *during* the gesture that closed the
      // dialog. A dialog dismissed from the keyboard still has a key in flight — Space fires its
      // click on keyup — and restoring focus synchronously puts the opener under that pending
      // activation: the settings gear got clicked a second time by the browser and reopened the
      // panel the user had just closed. One frame is enough for the original gesture to finish
      // resolving against the control it started on.
      requestAnimationFrame(() => {
        // Guarded, because the opener can be gone by now — a row action whose row was the thing the
        // dialog deleted. Focusing a detached node silently does nothing and leaves focus on
        // `<body>`, which is the state this restore exists to avoid; better to leave it where it is.
        if (opener && document.contains(opener)) opener.focus();
      });
    };
  }, [panel, active, opener]);
}
