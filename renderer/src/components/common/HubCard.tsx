import type { LucideIcon } from "lucide-react";
import type { ReactNode } from "react";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";
import { CARD } from "./panelChrome";

/**
 * One card on the Home hub: a heading, a body that is usually a short list, and nothing else
 * competing for the click.
 *
 * The rule it encodes is that a card has **one** primary action, in the header, and the rows inside
 * are the other way in. A hub whose cards each grow their own toolbar stops being a landing page
 * and becomes four half-panels — which is what the app already has, one module at a time.
 *
 * It wears `CARD` rather than restating it, so a hub card and a view are the same object at
 * different sizes.
 */
export function HubCard({
  title,
  icon: Icon,
  action,
  children,
}: {
  title: TranslationKey;
  icon: LucideIcon;
  /** The card's single primary control. A `Button` in `ghost`/`sm`, by convention. */
  action?: ReactNode;
  children: ReactNode;
}) {
  const t = useT();

  return (
    <section className={`flex min-h-0 flex-col overflow-hidden ${CARD}`}>
      <header className="flex h-9 shrink-0 items-center gap-2 border-b border-[var(--cf-border)] px-3">
        <span className="flex h-5 w-5 shrink-0 items-center justify-center rounded-md bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]">
          <Icon size={12} aria-hidden />
        </span>
        <h2 className="min-w-0 flex-1 truncate text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
          {t(title)}
        </h2>
        {action}
      </header>
      <div className="min-h-0 flex-1 overflow-y-auto p-1.5">{children}</div>
    </section>
  );
}
