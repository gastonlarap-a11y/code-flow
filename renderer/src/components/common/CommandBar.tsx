import { Search } from "lucide-react";
import { useUiStore } from "../../state/uiStore";
import { useT } from "../../state/languageStore";
import { useShortcutChord } from "../../lib/useShortcutHint";

/**
 * The command bar: a real field in the header, not a button that says a shortcut exists.
 *
 * The three pickers this app had were all keyboard-only doors — you had to know `Mod+Shift+P` to
 * find out the palette existed at all. A visible field is the Linear/Arc/Raycast answer: the place
 * to type is on screen, and the prefixes (`lib/ui/commandScope.ts`) are discoverable from the hint
 * beside it rather than from documentation.
 *
 * It does not own a list. Typing here opens the overlay with what was typed already in it, which is
 * why `openCommandPalette` takes a query: rendering results under a header-width field would put a
 * popup where the window controls are on Windows, and the overlay is already built, focus-trapped
 * and scroll-managed. The field is the door; `CommandPalette` is the room, and it is what reads the
 * prefix — passing the raw text through means one parser rather than two that must agree.
 */
export function CommandBar() {
  const openCommandPalette = useUiStore((s) => s.openCommandPalette);
  const t = useT();
  const chord = useShortcutChord();
  const binding = chord("app.commandPalette");

  return (
    <div className="flex min-w-0 flex-1 justify-center px-2">
      <button
        type="button"
        onClick={() => openCommandPalette("all")}
        onKeyDown={(e) => {
          // A printable character starts a search rather than being swallowed: this looks like an
          // input, so typing into it has to behave like one. The keystroke is handed on as the
          // initial query instead of being dropped and re-typed.
          if (e.key.length === 1 && !e.metaKey && !e.ctrlKey && !e.altKey) {
            e.preventDefault();
            openCommandPalette("all", e.key);
          }
        }}
        aria-label={t("commandbar.label")}
        className="cf-focusable flex h-7 w-full max-w-[420px] items-center gap-2 rounded-control border border-[var(--cf-border)] bg-[var(--cf-surface)]/70 px-2.5 text-left text-ui text-[var(--cf-text-muted)] transition-colors hover:border-[var(--cf-accent)]/40 hover:bg-[var(--cf-surface)]"
      >
        <Search size={14} className="shrink-0" aria-hidden />
        <span className="min-w-0 flex-1 truncate">{t("commandbar.placeholder")}</span>
        {/* Read from the user's own keymap, never written as a literal: a rebind would leave a
            hardcoded chord lying with nothing failing. */}
        {binding && (
          <span className="shrink-0 rounded border border-[var(--cf-border)] px-1 text-badge tabular-nums">
            {binding}
          </span>
        )}
      </button>
    </div>
  );
}
