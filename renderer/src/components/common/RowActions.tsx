import { useEffect, useId, useRef, useState } from "react";
import { MoreHorizontal, type LucideIcon } from "lucide-react";
import { Tooltip } from "./Tooltip";
import { anchorName } from "../../lib/ui/anchorName";
import { menuKeyAction, nextEnabledIndex } from "../../lib/ui/menuNavigation";
import { iconButtonStyle } from "../../lib/ui/controlStyles";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";

export interface RowAction {
  id: string;
  labelKey: TranslationKey;
  /** Interpolations for `labelKey`, when the action names something — "Move to {name}". */
  labelParams?: Record<string, string | number>;
  icon: LucideIcon;
  onSelect: () => void;
  disabled?: boolean;
  /** Renders in the danger colour and sits below a separator — deletes, drops, discards. */
  danger?: boolean;
}

/**
 * The persistent per-row action menu.
 *
 * The audit found 32 `group-hover:*` reveals across 17 files — renaming a branch, dropping a stash,
 * moving a project, opening a repo in Finder. Every one of them is invisible until the pointer
 * happens to rest on that exact row, so nothing tells a user the action exists, and a keyboard or
 * touch user never finds it at all. This trigger is always rendered, just dimmed until the row is
 * hovered or focused, so the affordance is permanent and only the emphasis is conditional.
 *
 * The menu is a popover anchored in CSS, which matters here more than anywhere else: these rows are
 * inside `FileTree` and `CollectionTree`, which are virtualized. Being in the top layer means the
 * menu is not clipped by the scroll container, and scrolling the owning row out of the window
 * unmounts the menu with it — which is the behaviour you want and costs nothing to get.
 */
export function RowActions({
  actions,
  label = "common.moreActions",
  size = "sm",
  className,
}: {
  actions: readonly RowAction[];
  /** Names the trigger. Override it when a row-specific name reads better than "More actions". */
  label?: TranslationKey;
  size?: "md" | "sm";
  className?: string;
}) {
  const t = useT();
  const trigger = useRef<HTMLButtonElement>(null);
  const menu = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(-1);
  const id = useId();
  const anchor = anchorName("row-actions", id);
  // `popoverTarget` is matched against an element id, so the two ends need the same string.
  const menuId = `row-actions-menu-${id}`;
  const style = iconButtonStyle("ghost", size);

  const close = () => {
    menu.current?.hidePopover();
    trigger.current?.focus();
  };

  // Focus follows the active item so a screen reader announces each one as it is reached.
  useEffect(() => {
    if (!open || activeIndex < 0) return;
    menu.current?.querySelectorAll<HTMLElement>('[role="menuitem"]')[activeIndex]?.focus();
  }, [open, activeIndex]);

  // Opening from the keyboard lands on an item directly, so the first arrow press does not have to
  // be spent moving off nothing.
  const openAt = (index: number) => {
    setActiveIndex(index);
    menu.current?.showPopover();
  };

  const onTriggerKeyDown = (event: React.KeyboardEvent) => {
    if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
    event.preventDefault();
    const backwards = event.key === "ArrowUp";
    openAt(nextEnabledIndex(actions, backwards ? 0 : -1, backwards ? -1 : 1));
  };

  const onMenuKeyDown = (event: React.KeyboardEvent) => {
    const action = menuKeyAction(event.key, actions, activeIndex);
    if (action.kind === "none") return;
    event.preventDefault();
    if (action.kind === "close") return close();
    if (action.kind === "move") return setActiveIndex(action.index);
    actions[action.index]!.onSelect();
    close();
  };

  const [regular, destructive] = [
    actions.filter((a) => !a.danger),
    actions.filter((a) => a.danger),
  ];

  return (
    <>
      <Tooltip label={t(label)} disabled={open}>
        <button
          ref={trigger}
          type="button"
          aria-label={t(label)}
          aria-haspopup="menu"
          aria-expanded={open}
          // The browser owns open/close for the pointer. Doing it by hand meant the click that
          // opened the menu was also, to the browser, a click outside it — so `auto`'s light-dismiss
          // shut it again within the same gesture and nothing ever appeared. Declaring the invoker
          // relationship is what tells Chromium this button belongs to that popover.
          popoverTarget={menuId}
          // Opening a menu is not clicking the row it sits in. `stopPropagation` does not cancel
          // this button's own default action — which is what `popoverTarget` rides on — nor the
          // browser's light-dismiss, so the menu still opens and closes exactly as before.
          onClick={(e) => e.stopPropagation()}
          onKeyDown={onTriggerKeyDown}
          // Dimmed, never hidden. `opacity-0` is what made these actions undiscoverable.
          className={`${style.className} opacity-55 group-hover:opacity-100 group-focus-within:opacity-100 aria-expanded:opacity-100${
            className ? ` ${className}` : ""
          }`}
          style={{ anchorName: anchor }}
        >
          <MoreHorizontal size={style.iconSize} aria-hidden />
        </button>
      </Tooltip>

      <div
        ref={menu}
        id={menuId}
        role="menu"
        // `auto`, unlike the tooltip's `manual`: clicking anywhere else should dismiss this, and the
        // browser's light-dismiss does that better than a document listener would.
        popover="auto"
        // The popover is the source of truth for whether it is open — it can be dismissed by a click
        // outside or by Escape without any of our code running — so React state follows it here
        // rather than the other way round.
        onToggle={(event) => {
          const opened = (event as unknown as { newState: string }).newState === "open";
          setOpen(opened);
          if (!opened) setActiveIndex(-1);
        }}
        onKeyDown={onMenuKeyDown}
        style={{ positionAnchor: anchor, positionArea: "block-end span-inline-start" }}
        className="cf-fade-in m-0 min-w-[180px] rounded-[var(--radius-control)] border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-1 shadow-[var(--cf-shadow)] [margin-block-start:4px] [position-try-fallbacks:flip-block]"
      >
        {regular.map((action) => (
          <MenuItem key={action.id} action={action} index={actions.indexOf(action)} onRun={close} />
        ))}
        {destructive.length > 0 && regular.length > 0 && (
          <div className="my-1 h-px bg-[var(--cf-border)]" role="separator" />
        )}
        {destructive.map((action) => (
          <MenuItem key={action.id} action={action} index={actions.indexOf(action)} onRun={close} />
        ))}
      </div>
    </>
  );
}

function MenuItem({
  action,
  index,
  onRun,
}: {
  action: RowAction;
  index: number;
  onRun: () => void;
}) {
  const t = useT();
  const Icon = action.icon;

  return (
    <button
      role="menuitem"
      type="button"
      // The menu owns arrow navigation; each item is reachable through it, not through Tab.
      tabIndex={-1}
      data-index={index}
      disabled={action.disabled}
      // Stops at this button, like every other control that sits inside a clickable row. The menu
      // is `popover="auto"` but not a portal, so it is still a DOM child of that row and its clicks
      // bubble into it — picking "Rename" also opened the row's own dialog.
      onClick={(e) => {
        e.stopPropagation();
        action.onSelect();
        onRun();
      }}
      className={`cf-focusable cf-interactive flex h-7 w-full items-center gap-2 rounded-[4px] px-2 text-left text-ui disabled:pointer-events-none disabled:opacity-50 hover:bg-[color-mix(in_oklab,currentColor_calc(var(--cf-overlay-hover)*100%),transparent)] ${
        action.danger ? "text-[var(--cf-danger)]" : "text-[var(--cf-text)]"
      }`}
    >
      <Icon size={14} className="shrink-0" aria-hidden />
      {t(action.labelKey, action.labelParams)}
    </button>
  );
}
