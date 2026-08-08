import {
  cloneElement,
  useEffect,
  useId,
  useRef,
  useState,
  type CSSProperties,
  type ReactElement,
} from "react";
import { anchorName } from "../../lib/ui/anchorName";

/**
 * The app's tooltip, replacing the native `title` attribute.
 *
 * `title` was doing all the labelling in this app — 236 uses of it — and it is the wrong tool: it
 * appears after a delay the OS picks, in a style the OS picks, never on keyboard focus, and never
 * at all on a touch screen. This shows on hover *and* on `focus-visible`, so tabbing through a
 * toolbar explains it the same way pointing at it does.
 *
 * Positioning is CSS, not JavaScript. The trigger gets an `anchor-name`, the bubble gets a matching
 * `position-anchor`, and `position-area` places it — with `position-try-fallbacks` flipping it to
 * the other side near a window edge. Being a popover puts it in the top layer, so it is never
 * clipped by an ancestor's `overflow: hidden`, which is what forced `Select` into a body portal with
 * a manual `getBoundingClientRect` loop. Both APIs have shipped in Chromium for years and this is an
 * Electron app, so there is no fallback path to maintain.
 *
 * **The tooltip is never the only label.** `IconButton` puts the same string in `aria-label`. If
 * this component fails to render for any reason, the control is still named.
 */
export function Tooltip({
  label,
  shortcut,
  placement = "top",
  disabled = false,
  children,
}: {
  /** Already translated — this component does no i18n of its own. */
  label: string;
  /** Rendered as a keycap chip, e.g. `"⌘B"`. `null` is "this action has no binding right now". */
  shortcut?: string | null | undefined;
  placement?: "top" | "bottom" | "left" | "right";
  /** Suppresses the bubble without unmounting, for a control whose label is already visible. */
  disabled?: boolean;
  /**
   * A single element that can hold a ref and take pointer/focus handlers. Passed through
   * `cloneElement` rather than wrapped in a `<span>`, because a wrapper would break the flex and
   * grid layouts every toolbar in this app is built from.
   */
  children: ReactElement<Record<string, unknown>>;
}) {
  const bubble = useRef<HTMLSpanElement>(null);
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const [open, setOpen] = useState(false);
  const anchor = anchorName("tooltip", useId());

  // Show and hide go through the popover API rather than through conditional rendering: the element
  // has to already exist for the browser to promote it to the top layer.
  useEffect(() => {
    const node = bubble.current;
    if (!node) return;
    // `togglePopover` throws if the element is not connected — it always is here, but the state and
    // the DOM can disagree for a frame during a fast hover-out/hover-in.
    if (open && !node.matches(":popover-open")) node.showPopover();
    if (!open && node.matches(":popover-open")) node.hidePopover();
  }, [open]);

  // A bubble whose trigger disappears while it is showing would be left in the top layer with
  // nothing to anchor it — which is what happened to the close button's "Cerrar" every time
  // Settings was dismissed: the dialog unmounted, and its tooltip stayed on screen.
  useEffect(() => {
    const node = bubble.current;
    return () => {
      if (node?.matches(":popover-open")) node.hidePopover();
    };
  }, []);

  useEffect(() => () => clearTimeout(timer.current), []);

  // Delayed in, immediate out. A tooltip that lingers while the pointer moves down a toolbar covers
  // the next control, which is the failure mode Monaco's own hover widget has (see `.workbench-hover`
  // in `index.css`, deleted outright for exactly this).
  const show = (delay: number) => {
    clearTimeout(timer.current);
    if (disabled || !label) return;
    timer.current = setTimeout(() => setOpen(true), delay);
  };
  const hide = () => {
    clearTimeout(timer.current);
    setOpen(false);
  };

  // The trigger may already be an anchor for something else — `RowActions` anchors its menu to the
  // very button it wraps in a tooltip. `anchor-name` takes a list, so this appends rather than
  // replaces; overwriting it left the other popover unanchored and pinned to the viewport corner.
  const ownAnchor = (children.props.style as CSSProperties | undefined)?.anchorName;

  const trigger = cloneElement(children, {
    style: {
      ...(children.props.style as object),
      anchorName: ownAnchor ? `${String(ownAnchor)}, ${anchor}` : anchor,
    },
    onPointerEnter: (event: React.PointerEvent) => {
      (children.props.onPointerEnter as ((e: React.PointerEvent) => void) | undefined)?.(event);
      show(300);
    },
    onPointerLeave: (event: React.PointerEvent) => {
      (children.props.onPointerLeave as ((e: React.PointerEvent) => void) | undefined)?.(event);
      hide();
    },
    /**
     * Keyboard focus gets it immediately: the user arrived deliberately and is waiting to be told
     * what this is. **Only keyboard focus**, which `:focus-visible` is exactly the question for.
     *
     * This used to fire on any focus, on the assumption that a mouse click's focus was "harmless
     * and self-clearing" because the click right behind it calls `hide()`. That holds only when a
     * click actually follows. Focus moved by code has none — and two of those bracket every dialog:
     * the focus trap puts focus on the panel's first control when it opens (the close button, whose
     * "Cerrar" bubble then sat there), and hands it back to the opener when it closes (the settings
     * gear, whose bubble appeared with nobody touching it). Both were reported as stuck tooltips.
     */
    onFocus: (event: React.FocusEvent) => {
      (children.props.onFocus as ((e: React.FocusEvent) => void) | undefined)?.(event);
      if (event.target.matches(":focus-visible")) show(0);
    },
    onBlur: (event: React.FocusEvent) => {
      (children.props.onBlur as ((e: React.FocusEvent) => void) | undefined)?.(event);
      hide();
    },
    // On press, not on click: by the time `click` fires the button may already have opened a dialog
    // over it, and the bubble would be left describing something nobody can see any more.
    onPointerDown: (event: React.PointerEvent) => {
      (children.props.onPointerDown as ((e: React.PointerEvent) => void) | undefined)?.(event);
      hide();
    },
    // Pressing the button acts on it; the explanation has served its purpose and is in the way.
    onClick: (event: React.MouseEvent) => {
      hide();
      (children.props.onClick as ((e: React.MouseEvent) => void) | undefined)?.(event);
    },
  });

  return (
    <>
      {trigger}
      <span
        ref={bubble}
        // `manual` and not `auto`: an `auto` popover light-dismisses, which would close a menu the
        // user opened from the very control this is describing.
        popover="manual"
        // Not `role="tooltip"` with `aria-describedby`: the accessible name already carries this
        // string through `aria-label`, and announcing it twice is worse than not announcing it.
        aria-hidden
        style={{ positionAnchor: anchor, positionArea: PLACEMENT[placement] }}
        className="cf-fade-in pointer-events-none m-0 w-max max-w-[260px] rounded-[var(--radius-control)] border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] px-2 py-1 text-ui text-[var(--cf-text)] shadow-[var(--cf-shadow)] [position-try-fallbacks:flip-block,flip-inline] [margin:6px]"
      >
        {label}
        {shortcut && (
          <span className="ml-1.5 rounded-[4px] border border-[var(--cf-border)] px-1 text-badge text-[var(--cf-text-muted)]">
            {shortcut}
          </span>
        )}
      </span>
    </>
  );
}

/** `position-area` values — the grid cell the bubble occupies relative to its anchor. */
const PLACEMENT = {
  top: "block-start",
  bottom: "block-end",
  left: "inline-start",
  right: "inline-end",
} as const;
