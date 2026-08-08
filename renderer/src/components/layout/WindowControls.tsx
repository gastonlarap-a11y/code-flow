import { Minus, Square, X } from "lucide-react";
import { getCurrentWindow } from "../../lib/bridge/shell";
import { useT } from "../../state/languageStore";

const win = getCurrentWindow();

/**
 * On macOS the traffic lights are the real system buttons (see the shell window config:
 * `titleBarStyle: Overlay` keeps native decorations — and with them the rounded window corners and
 * the green button's real fullscreen behavior — while letting the webview draw under the title bar).
 * They're drawn by AppKit *over* the webview, so all the header has to do is leave a gap wide enough
 * not to collide with them: they run from x=20 to roughly x=74.
 *
 * The gap was 62px, which is narrower than the buttons it was meant to clear. With the header's own
 * 8px padding that left the first app control starting around x=78 — four pixels off the green
 * button — so the back/forward chevrons read as a fourth and fifth traffic light. 84px clears them
 * with room to breathe.
 */
export function MacControlsSpacer() {
  return <div aria-hidden className="w-[84px]" />;
}

/**
 * The Windows caption buttons.
 *
 * Deliberately not `IconButton`s: these imitate the OS's own window chrome, which means a 44×36 hit
 * area, no rounding, no gap between them, and a red hover on close. A design-system button would
 * look like an app control sitting where the window controls belong.
 *
 * The glyphs follow Windows too — an outlined `Square` is what it draws for maximize. That is the
 * one place the icon dictionary lets `Square` mean something other than "stop a running process",
 * because the dictionary's entry is the *filled* square; every stop in the app carries `fill-current`
 * to keep the two apart.
 *
 * They also keep the native `title` rather than `Tooltip`, for the same reason: the app's bubble is
 * an app affordance, and these three are the window's. The i18n'd `aria-label` is what accessibility
 * actually depends on, and it is right there beside it. This file is the single entry in
 * `scripts/ui-conventions.test.mjs`'s native-title allowlist, and this paragraph is the reason that
 * test demands be written in the file it names.
 *
 * Split out of `TitleBar.tsx` when the 2.0 command header replaced it. Nothing here changed: the
 * window's chrome is the window's chrome regardless of what the app draws beside it.
 */
export function WindowsControls() {
  const t = useT();

  return (
    <div className="flex items-center">
      <button
        aria-label={t("titlebar.minimize")}
        title={t("titlebar.minimize")}
        onClick={() => win.minimize()}
        className="flex h-9 w-11 items-center justify-center text-[var(--cf-text)]/70 hover:bg-black/10"
      >
        <Minus size={14} />
      </button>
      <button
        aria-label={t("titlebar.maximize")}
        title={t("titlebar.maximize")}
        onClick={() => win.toggleMaximize()}
        className="flex h-9 w-11 items-center justify-center text-[var(--cf-text)]/70 hover:bg-black/10"
      >
        <Square size={12} />
      </button>
      <button
        aria-label={t("titlebar.closeWindow")}
        title={t("titlebar.closeWindow")}
        onClick={() => win.close()}
        className="flex h-9 w-11 items-center justify-center text-[var(--cf-text)]/70 hover:bg-red-500 hover:text-white"
      >
        <X size={14} />
      </button>
    </div>
  );
}
