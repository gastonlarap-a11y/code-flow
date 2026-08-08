import { useEffect, useRef, useState } from "react";
import { ChevronDown, Plus, X } from "lucide-react";
import { createPortal } from "react-dom";
import { IconButton } from "../common/IconButton";
import { Tooltip } from "../common/Tooltip";
import { badgeColor, badgeLabel } from "./methodStyle";
import { useApiTabsStore, type ApiTab } from "../../state/apiTabsStore";
import { useRowHoverStore } from "../../state/rowHoverStore";
import { confirmAction } from "../../state/confirmStore";
import { useT } from "../../state/languageStore";
import { PROTOCOL_NAMES } from "../../lib/api/protocol";
import { API_PROTOCOLS, type ApiProtocol } from "../../types/api";

/**
 * The request tabs across the top of the builder.
 *
 * Deliberately *not* draggable: `apiStore` has no action that reorders `openTabs`, and a gesture
 * that rearranged them only in this component would be forgotten the moment the tab list is
 * persisted (which happens on every draft keystroke). Reordering belongs behind a store action,
 * not behind local state pretending to be one.
 */
export function RequestTabs() {
  const t = useT();
  const openTabs = useApiTabsStore((s) => s.openTabs);
  const activeTabId = useApiTabsStore((s) => s.activeTabId);
  const setActiveTab = useApiTabsStore((s) => s.setActiveTab);
  const closeTab = useApiTabsStore((s) => s.closeTab);
  const openScratchTab = useApiTabsStore((s) => s.openScratchTab);
  const hoveredKey = useRowHoverStore((s) => s.key);

  const stripRef = useRef<HTMLDivElement>(null);
  const [protocolMenu, setProtocolMenu] = useState<{ left: number; top: number } | null>(null);
  const tabRefs = useRef<Map<string, HTMLDivElement>>(new Map());

  // Opening a request from the tree can append a tab past the right edge of the strip.
  useEffect(() => {
    if (!activeTabId) return;
    tabRefs.current.get(activeTabId)?.scrollIntoView({ block: "nearest", inline: "nearest" });
  }, [activeTabId, openTabs.length]);

  // A horizontal strip inside a vertical layout never receives horizontal wheel deltas from a
  // plain mouse — translate the vertical ones, the way the editor's tab strip does.
  const onWheel = (e: React.WheelEvent<HTMLDivElement>) => {
    const el = stripRef.current;
    if (!el || e.deltaY === 0 || el.scrollWidth <= el.clientWidth) return;
    el.scrollLeft += e.deltaY;
  };

  const requestClose = async (tab: ApiTab) => {
    if (tab.dirty) {
      const name = tab.name || t("api.untitledRequest");
      if (!(await confirmAction(t("editor.closeDirtyConfirm", { name })))) return;
    }
    closeTab(tab.id);
  };

  return (
    <div className="flex shrink-0 items-stretch border-b border-[var(--cf-border)] bg-[var(--cf-bg)]">
      <div
        ref={stripRef}
        onWheel={onWheel}
        role="tablist"
        aria-label={t("api.openRequests")}
        className="cf-tab-strip flex min-w-0 flex-1 items-stretch overflow-x-auto"
      >
        {openTabs.map((tab) => {
          const active = tab.id === activeTabId;
          const hoverKey = `apitab:${tab.id}`;
          return (
            <div
              key={tab.id}
              ref={(el) => {
                if (el) tabRefs.current.set(tab.id, el);
                else tabRefs.current.delete(tab.id);
              }}
              role="tab"
              aria-selected={active}
              // Named explicitly, because a `role="tab"` takes its name from its contents — and the
              // close button inside it is content, so the tab announced itself as
              // "GET · Untitled request · Close".
              aria-label={`${badgeLabel(tab.draft.protocol, tab.draft.method)} ${tab.name || t("api.untitledRequest")}`}
              onPointerEnter={() => useRowHoverStore.getState().enter(hoverKey)}
              onPointerLeave={() => useRowHoverStore.getState().leave(hoverKey)}
              // Kills press-and-sweep text selection without costing the `click` that follows.
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => setActiveTab(tab.id)}
              onAuxClick={(e) => {
                if (e.button !== 1) return;
                e.preventDefault();
                void requestClose(tab);
              }}
              className={`group relative flex h-9 max-w-[240px] shrink-0 cursor-pointer select-none items-center gap-2 border-r border-[var(--cf-border)] pl-3 pr-2 text-ui transition-colors ${
                active
                  ? "bg-[var(--cf-surface)] text-[var(--cf-text)]"
                  : `text-[var(--cf-text-muted)] ${hoverKey === hoveredKey ? "cf-row-hover" : ""}`
              }`}
            >
              {active && <span className="absolute inset-x-0 top-0 h-[2px] bg-[var(--cf-accent)]" />}
              <span
                className="shrink-0 font-mono text-badge font-semibold"
                style={{ color: badgeColor(tab.draft.protocol, tab.draft.method) }}
              >
                {badgeLabel(tab.draft.protocol, tab.draft.method)}
              </span>
              <span className={`truncate ${tab.dirty ? "italic" : ""}`}>
                {tab.name || t("api.untitledRequest")}
              </span>
              {/* Unsaved state and "close this" are two different things, and merging them into one
                  slot is what forced the close button to hide until hover — leaving no way to close
                  a tab by keyboard or on a touch screen. They are two marks now, both always there. */}
              {tab.dirty && (
                <Tooltip label={t("api.unsaved")}>
                  <span className="ml-auto h-2 w-2 shrink-0 rounded-full bg-[var(--cf-accent)]" />
                </Tooltip>
              )}
              <IconButton
                label="common.close"
                icon={X}
                onClick={(e: React.MouseEvent) => {
                  e.stopPropagation();
                  void requestClose(tab);
                }}
                className={`shrink-0 ${tab.dirty ? "" : "ml-auto"} opacity-60 group-hover:opacity-100 group-focus-within:opacity-100`}
              />
            </div>
          );
        })}
      </div>

      {/* Split control: the plus is the common case (a plain HTTP request, one click), the caret
          is for the five protocols you'd otherwise have to create-then-convert to reach. */}
      <div className="flex shrink-0 items-stretch border-l border-[var(--cf-border)]">
        {/* This split earns its caret, unlike the send button's: the menu behind it holds six
            protocols, and the plain HTTP request the plus makes is the one people want most. */}
        <IconButton
          label="api.newRequest"
          icon={Plus}
          onClick={() => openScratchTab()}
          className="self-center"
        />
        <IconButton
          label="api.protocol"
          icon={ChevronDown}
          onClick={(e: React.MouseEvent<HTMLButtonElement>) => {
            const rect = e.currentTarget.getBoundingClientRect();
            setProtocolMenu({ left: rect.right, top: rect.bottom + 4 });
          }}
          className="self-center"
        />
      </div>

      {protocolMenu &&
        createPortal(
          <>
            {/* Full-viewport catcher instead of a document listener: it closes the menu on the
                same click that would otherwise also press whatever is underneath. */}
            <div className="fixed inset-0 z-[9998]" onMouseDown={() => setProtocolMenu(null)} />
            <div
              role="menu"
              style={{ position: "fixed", left: protocolMenu.left, top: protocolMenu.top }}
              className="z-[9999] w-[230px] -translate-x-full rounded-md border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-1 shadow-[var(--cf-shadow)]"
            >
              {API_PROTOCOLS.map((protocol: ApiProtocol) => (
                <button
                  key={protocol}
                  role="menuitem"
                  onClick={() => {
                    setProtocolMenu(null);
                    openScratchTab(protocol);
                  }}
                  className="flex w-full flex-col items-start rounded px-2 py-1 text-left hover:bg-[color-mix(in_oklab,var(--cf-accent)_16%,transparent)]"
                >
                  <span className="text-ui font-medium text-[var(--cf-text)]">
                    {t("api.newRequestOf", { protocol: PROTOCOL_NAMES[protocol] })}
                  </span>
                  <span className="text-badge text-[var(--cf-text-muted)]">
                    {t(`api.protocolHint.${protocol}` as const)}
                  </span>
                </button>
              ))}
            </div>
          </>,
          document.body,
        )}
    </div>
  );
}
