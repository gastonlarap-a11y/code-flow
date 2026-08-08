import { useState, type ReactNode } from "react";
import { ChevronDown, type LucideIcon } from "lucide-react";

/** A titled card that visually groups a set of settings, with an icon chip + subtitle header —
 * the same card language used elsewhere in the app (connection cards, the AI panel headers).
 * When `collapsible`, the header toggles the body open/closed (a chevron marks the state). */
export function GroupCard({
  icon: Icon,
  title,
  subtitle,
  headerExtra,
  children,
  collapsible = false,
  defaultOpen = true,
}: {
  icon: LucideIcon;
  title: string;
  subtitle?: string;
  /** Shown under the subtitle and *outside* the collapsible body — for what describes the card
   * itself rather than its contents. The integrations list puts its capability chips here: they
   * say what a provider can do, which is exactly what you want to read before deciding to open it. */
  headerExtra?: ReactNode;
  children: ReactNode;
  collapsible?: boolean;
  defaultOpen?: boolean;
}) {
  const [open, setOpen] = useState(defaultOpen);
  const expanded = !collapsible || open;

  const header = (
    <>
      <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]">
        <Icon size={14} />
      </span>
      <div className="min-w-0 flex-1">
        <p className="text-relaxed font-semibold leading-tight">{title}</p>
        {subtitle && <p className="mt-0.5 text-body leading-snug text-[var(--cf-text-muted)]">{subtitle}</p>}
      </div>
      {collapsible && (
        <ChevronDown
          size={16}
          className={`mt-0.5 shrink-0 text-[var(--cf-text-muted)] transition-transform ${open ? "" : "-rotate-90"}`}
        />
      )}
    </>
  );

  return (
    <div className="rounded-xl border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-4">
      {collapsible ? (
        <button
          type="button"
          onClick={() => setOpen((v) => !v)}
          className={`flex w-full items-start gap-2.5 text-left ${expanded ? "mb-4" : ""}`}
        >
          {header}
        </button>
      ) : (
        <div className="mb-4 flex items-start gap-2.5">{header}</div>
      )}
      {/* An expanded header already spaced itself; a collapsed one did not, and without the top
          margin the chips would sit flush against the title. */}
      {headerExtra && <div className={expanded ? "mb-4" : "mt-3"}>{headerExtra}</div>}
      {expanded && children}
    </div>
  );
}
