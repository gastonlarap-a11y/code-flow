import { useRef, useState } from "react";
import type { LucideIcon } from "lucide-react";
import { ActivePill } from "./ActivePill";
import { tabKeyResult, type TabActivation } from "../../lib/ui/tabActivation";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";

export interface TabOption<Id extends string = string> {
  id: Id;
  labelKey: TranslationKey;
  icon?: LucideIcon;
  disabled?: boolean;
  /** Count or status shown after the label — unread items, request count, and so on. */
  badge?: string | number;
  /**
   * Colours the badge. `neutral` is a count and is the default; `success`/`danger` are for a badge
   * that reports an outcome — the API response's "3/5" of tests, where a failing run must not read
   * the same as a headers count. The badge's own text always says it too, so the colour is emphasis
   * rather than the message.
   */
  badgeTone?: "neutral" | "success" | "danger";
  /**
   * "This section has something in it" when there is no number worth showing — a request body is
   * present, a script is written. Ignored when `badge` is set, since a count says strictly more.
   */
  dot?: boolean;
}

/**
 * One idiom for switching between sections *inside* a panel.
 *
 * The app currently has four that do not agree: the TabBar's pills, `ApiSidebar`'s local strip,
 * the Editor's icon rail and Settings' vertical list. This is the one for panel sections; view
 * switching stays with the TabBar, and the Editor rail stays as a documented VS-Code-shaped
 * exception. `ProviderTabs` is the shape this generalises.
 *
 * Keyboard behaviour follows the ARIA tabs pattern and lives in `lib/ui/tabActivation.ts` (over
 * `menuNavigation.ts`), where it can be tested without a DOM: arrows move between tabs skipping
 * disabled ones and wrapping, Home/End jump to the ends, and the strip is a single tab stop — Tab
 * moves past it rather than through every tab in it. Whether an arrow also *selects* is the
 * `activation` prop; see it for when automatic is wrong.
 *
 * This renders the strip only. The panel half of the pattern is `tabPanelProps`, exported below —
 * the tabs cannot render it themselves without owning their content, and they deliberately do not.
 */
export function Tabs<Id extends string>({
  options,
  activeId,
  onSelect,
  layoutId,
  activation = "automatic",
  label,
  className,
}: {
  options: readonly TabOption<Id>[];
  activeId: Id;
  onSelect: (id: Id) => void;
  /** Must be unique per rendered strip, or two strips share one pill and throw it between them. */
  layoutId: string;
  /**
   * Whether an arrow key selects the tab it lands on.
   *
   * The APG's rule is that selection may follow focus only when the panel is already loaded and
   * appears with no perceptible delay; when showing it costs something, arrowing through the strip
   * has to stop doing it. `manual` moves focus and waits for Enter or Space.
   *
   * "Costs something" is not only latency. The AI panel's Analyze tab *starts a Claude run* when it
   * mounts, so with automatic activation a single arrow key would spend money.
   */
  activation?: TabActivation;
  /** Names the strip for assistive tech. Already translated. */
  label?: string;
  className?: string;
}) {
  const t = useT();
  const strip = useRef<HTMLDivElement>(null);
  const activeIndex = options.findIndex((option) => option.id === activeId);
  // Under manual activation the focused tab and the selected one come apart, so focus needs its own
  // state. `null` means "focus has not moved yet", i.e. it sits on the selected tab.
  const [focusedIndex, setFocusedIndex] = useState<number | null>(null);
  const cursor = focusedIndex ?? activeIndex;

  const focusTab = (index: number) => {
    setFocusedIndex(index);
    strip.current?.querySelectorAll<HTMLElement>('[role="tab"]')[index]?.focus();
  };

  const onKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
    const result = tabKeyResult(event.key, options, cursor, activation);
    if (result.kind === "none") return;

    event.preventDefault();
    if (result.kind !== "select") focusTab(result.index);
    if (result.kind !== "focus") onSelect(options[result.index]!.id);
  };

  return (
    <div
      ref={strip}
      role="tablist"
      aria-label={label}
      onKeyDown={onKeyDown}
      // Leaving the strip resets the cursor, so coming back lands on the selected tab rather than
      // wherever the last arrow key stopped.
      onBlur={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget)) setFocusedIndex(null);
      }}
      className={`flex items-center gap-1${className ? ` ${className}` : ""}`}
    >
      {options.map((option, index) => {
        const Icon = option.icon;
        const active = option.id === activeId;
        return (
          <button
            key={option.id}
            id={tabId(layoutId, option.id)}
            role="tab"
            type="button"
            aria-selected={active}
            // Points at the panel this tab governs. `tabPanelProps` puts the other half on it; a
            // `role="tab"` with nothing to control is only half the pattern.
            aria-controls={panelId(layoutId, option.id)}
            disabled={option.disabled}
            // Roving tabindex: the strip is one stop, and the arrows move within it. Under manual
            // activation the reachable tab is the focused one, which need not be the selected one.
            tabIndex={index === cursor ? 0 : -1}
            onClick={() => onSelect(option.id)}
            className={`cf-focusable cf-interactive relative flex h-7 items-center gap-1.5 rounded-[var(--radius-control)] px-2.5 text-ui font-medium disabled:pointer-events-none disabled:opacity-50 ${
              active
                ? "text-[var(--cf-accent)]"
                : "text-[var(--cf-text-muted)] hover:text-[var(--cf-text)]"
            }`}
          >
            {active && <ActivePill layoutId={layoutId} />}
            <span className="relative flex items-center gap-1.5">
              {Icon && <Icon size={14} aria-hidden />}
              {t(option.labelKey)}
              {option.badge !== undefined && option.badge !== "" ? (
                <span
                  className={`rounded-full bg-[color-mix(in_oklab,currentColor_12%,transparent)] px-1.5 text-badge font-semibold ${
                    BADGE_TONE[option.badgeTone ?? "neutral"]
                  }`}
                >
                  {option.badge}
                </span>
              ) : (
                option.dot && <span className="h-1.5 w-1.5 rounded-full bg-[var(--cf-success)]" />
              )}
            </span>
          </button>
        );
      })}
    </div>
  );
}

/** Inherit by default, so a count badge takes the tab's own colour and stays quiet. */
const BADGE_TONE = {
  neutral: "",
  success: "text-[var(--cf-success)]",
  danger: "text-[var(--cf-danger)]",
} as const;

/** The ids the two halves of the pattern agree on. Derived from `layoutId`, which is already unique
 *  per strip by contract. */
const tabId = (layoutId: string, id: string) => `${layoutId}-tab-${id}`;
const panelId = (layoutId: string, id: string) => `${layoutId}-panel-${id}`;

/**
 * The attributes the panel belonging to a tab has to carry. Spread on whatever element wraps the
 * tab's content:
 *
 * ```tsx
 * <Tabs options={TABS} activeId={tab} onSelect={setTab} layoutId="ai-panel" />
 * <div {...tabPanelProps("ai-panel", tab)}>…</div>
 * ```
 *
 * `focusable` adds `tabIndex={0}`, and defaults to `false` on purpose: the APG only asks for it when
 * the panel has no focusable element to land on. Setting it unconditionally adds a tab stop in front
 * of every panel that already starts with an input — which is most of them here.
 */
export function tabPanelProps(layoutId: string, activeId: string, focusable = false) {
  return {
    id: panelId(layoutId, activeId),
    role: "tabpanel" as const,
    "aria-labelledby": tabId(layoutId, activeId),
    ...(focusable ? { tabIndex: 0 } : {}),
  };
}
