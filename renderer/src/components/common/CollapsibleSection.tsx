import { useState, type ReactNode } from "react";
import { ChevronDown, ChevronRight, type LucideIcon } from "lucide-react";

export function CollapsibleSection({
  icon: Icon,
  title,
  action,
  defaultOpen = false,
  children,
}: {
  icon: LucideIcon;
  title: string;
  action?: ReactNode;
  defaultOpen?: boolean;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(defaultOpen);

  return (
    <div>
      <div className="mb-1 flex items-center justify-between">
        {/* The eyebrow is the disclosure control, so it carries the state (`aria-expanded`) and a
            24px box — at `text-badge`'s 16px line-height alone it was a 17px-tall target, under the
            floor `lib/ui/controlStyles.ts` holds every other control to. The negative margin keeps
            the text optically flush with the rows below it despite the padding. */}
        <button
          onClick={() => setOpen((v) => !v)}
          aria-expanded={open}
          className="cf-focusable cf-interactive -mx-1 flex items-center gap-1 rounded-md px-1 py-1 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)] hover:text-[var(--cf-text)]"
        >
          {open ? <ChevronDown size={12} aria-hidden /> : <ChevronRight size={12} aria-hidden />}
          <Icon size={12} aria-hidden />
          {title}
        </button>
        {action}
      </div>
      {open && children}
    </div>
  );
}
