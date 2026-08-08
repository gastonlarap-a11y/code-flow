import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { ACCENT_OPTIONS } from "../../state/accentStore";
import { Tooltip } from "./Tooltip";
import { useT } from "../../state/languageStore";

// Reuses the same curated, contrast-checked palette as the accent color setting —
// one set of "safe" colors for the whole app instead of a freeform picker. The name travels with
// the hex: a swatch whose only label is "#6366f1" is not labelled.
const ICON_COLORS = ACCENT_OPTIONS.map((opt) => ({ color: opt.light, label: opt.label }));

const colorName = (value: string) => ICON_COLORS.find((c) => c.color === value)?.label ?? value;

const GAP = 4;
const EDGE = 8;

// Collapsed to just the currently selected color so it can sit compactly next to actions
// like the delete button, instead of always showing every option inline — click it to
// pop open the rest of the palette.
//
// The palette renders in a portal, positioned `fixed` from the swatch's viewport rect, because
// its callers sit inside scroll containers (the Settings modal clips with `overflow-hidden` /
// `overflow-auto`). An absolutely-positioned popover gets cropped at the container edge there,
// and no z-index fixes that — escaping the container is what does. Same approach as `Select`.
export function ColorSwatchPicker({ value, onChange }: { value: string; onChange: (color: string) => void }) {
  const t = useT();
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null);
  const btnRef = useRef<HTMLButtonElement>(null);
  const popRef = useRef<HTMLDivElement>(null);

  // Measured after mount (hidden until then) so the palette can flip above the swatch when it
  // wouldn't fit below, and stay inside the viewport horizontally.
  useLayoutEffect(() => {
    if (!open) {
      setPos(null);
      return;
    }
    const btn = btnRef.current;
    const pop = popRef.current;
    if (!btn || !pop) return;
    const rect = btn.getBoundingClientRect();
    const { width, height } = pop.getBoundingClientRect();
    const below = rect.bottom + GAP;
    const top = below + height > window.innerHeight - EDGE ? rect.top - height - GAP : below;
    const left = Math.max(EDGE, Math.min(rect.right - width, window.innerWidth - width - EDGE));
    setPos({ top, left });
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const onClickOutside = (e: MouseEvent) => {
      const target = e.target as Node;
      if (btnRef.current?.contains(target) || popRef.current?.contains(target)) return;
      setOpen(false);
    };
    // A fixed popover doesn't follow its anchor, so any scroll (in any ancestor, hence capture)
    // would leave it stranded — close instead of tracking.
    const onScroll = () => setOpen(false);
    window.addEventListener("mousedown", onClickOutside);
    window.addEventListener("scroll", onScroll, true);
    window.addEventListener("resize", onScroll);
    return () => {
      window.removeEventListener("mousedown", onClickOutside);
      window.removeEventListener("scroll", onScroll, true);
      window.removeEventListener("resize", onScroll);
    };
  }, [open]);

  return (
    <div className="flex shrink-0">
      {/* The dot stays 14px; the button around it is 24px, because that is the thing you press.
          WCAG 2.2 SC 2.5.8 floors a pointer target at 24, and this was the app's smallest. */}
      <Tooltip label={t("settings.pickColor", { name: colorName(value) })}>
        <button
          ref={btnRef}
          aria-label={t("settings.pickColor", { name: colorName(value) })}
          aria-haspopup="true"
          aria-expanded={open}
          onClick={(e) => {
            e.stopPropagation();
            setOpen((v) => !v);
          }}
          className="cf-focusable flex h-6 w-6 shrink-0 items-center justify-center rounded-md"
        >
          <span
            aria-hidden
            className="h-3.5 w-3.5 rounded-full ring-1 ring-inset ring-black/10 dark:ring-white/20"
            style={{ background: value }}
          />
        </button>
      </Tooltip>
      {open &&
        createPortal(
          <div
            ref={popRef}
            onClick={(e) => e.stopPropagation()}
            style={{
              top: pos?.top ?? 0,
              left: pos?.left ?? 0,
              visibility: pos ? "visible" : "hidden",
            }}
            className="fixed z-[9999] flex w-[136px] flex-wrap gap-1 rounded-lg border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-2 shadow-[var(--cf-shadow)]"
          >
            {ICON_COLORS.map(({ color, label }) => (
              <Tooltip key={color} label={label}>
                <button
                  aria-label={label}
                  aria-pressed={value === color}
                  onClick={() => {
                    onChange(color);
                    setOpen(false);
                  }}
                  className="cf-focusable flex h-6 w-6 items-center justify-center rounded-md"
                >
                  <span
                    aria-hidden
                    className="h-3.5 w-3.5 rounded-full"
                    style={{
                      background: color,
                      boxShadow:
                        value === color ? `0 0 0 1.5px var(--cf-surface-raised), 0 0 0 3px ${color}` : undefined,
                    }}
                  />
                </button>
              </Tooltip>
            ))}
          </div>,
          document.body,
        )}
    </div>
  );
}
