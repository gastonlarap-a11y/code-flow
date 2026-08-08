import type { ReactNode } from "react";
import type { LucideIcon } from "lucide-react";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";

/**
 * The bar at the top of a panel.
 *
 * Header bars are hand-set per area today — `h-8` here, `h-9` there, `h-10` in a third place — so
 * two panels side by side sit at different heights for no reason anyone chose. This is `h-9`
 * everywhere, with the eyebrow label the app already uses in 67 places
 * (`text-badge font-semibold uppercase tracking-wide text-muted`), which is its one genuine
 * system-wide convention.
 *
 * Actions go in `actions` as `IconButton`s and are never hover-gated: a control nobody can see is a
 * control nobody knows exists.
 */
export function PanelHeader({
  title,
  titleParams,
  icon: Icon,
  actions,
  children,
  className,
}: {
  title: TranslationKey;
  titleParams?: Record<string, string | number>;
  icon?: LucideIcon;
  /** Right-aligned controls. */
  actions?: ReactNode;
  /** Optional content between the title and the actions — a filter box, a count, a status dot. */
  children?: ReactNode;
  className?: string;
}) {
  const t = useT();

  return (
    <div
      className={`flex h-9 shrink-0 items-center gap-2 border-b border-[var(--cf-border)] px-2${
        className ? ` ${className}` : ""
      }`}
    >
      {Icon && <Icon size={14} className="shrink-0 text-[var(--cf-text-muted)]" aria-hidden />}
      <span className="shrink-0 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
        {t(title, titleParams)}
      </span>
      {children && <div className="min-w-0 flex-1">{children}</div>}
      {actions && <div className="ml-auto flex items-center gap-0.5">{actions}</div>}
    </div>
  );
}
