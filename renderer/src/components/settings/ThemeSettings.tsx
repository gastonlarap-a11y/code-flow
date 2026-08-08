import { useState, type ReactNode } from "react";
import { Check, ChevronDown, ChevronRight, Droplet, Laptop, Moon, Palette, Sun } from "lucide-react";
import { useThemeStore } from "../../state/themeStore";
import { findTheme, themesFor } from "../../lib/codeThemes";
import { ACCENT_OPTIONS, useAccentStore } from "../../state/accentStore";
import { useDensityStore } from "../../state/densityStore";
import { DENSITIES, densityPx, type TreeDensity } from "../../lib/ui/density";
import { ActivePill } from "../common/ActivePill";
import { Tooltip } from "../common/Tooltip";
import type { ThemePreference } from "../../types/domain";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";

const OPTIONS: { id: ThemePreference; labelKey: TranslationKey; icon: typeof Sun }[] = [
  { id: "light", labelKey: "settings.themeLight", icon: Sun },
  { id: "dark", labelKey: "settings.themeDark", icon: Moon },
  { id: "system", labelKey: "settings.themeSystem", icon: Laptop },
];

/**
 * A settings row that folds away. The `summary` is what makes folding safe here: the current
 * choice stays readable while closed, so nothing has to be expanded just to check it.
 *
 * Deliberately *not* `GroupCard`. That one is the top-level card — icon chip, `p-4`, a subtitle
 * under the title — and the theme picker nests these three deep, where that padding compounds into
 * a wall. Two disclosure shapes, because they are used at two different depths.
 */
function Panel({
  icon: Icon,
  title,
  summary,
  defaultOpen = false,
  children,
}: {
  icon: typeof Sun;
  title: string;
  summary?: ReactNode;
  defaultOpen?: boolean;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div className="rounded-lg border border-[var(--cf-border)]">
      <button
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        className="cf-focusable flex w-full items-center gap-1.5 px-2.5 py-2 text-left"
      >
        {open ? (
          <ChevronDown size={12} className="shrink-0 text-[var(--cf-text-muted)]" />
        ) : (
          <ChevronRight size={12} className="shrink-0 text-[var(--cf-text-muted)]" />
        )}
        <Icon size={13} className="shrink-0 text-[var(--cf-text-muted)]" />
        <span className="text-body font-medium">{title}</span>
        <span className="ml-auto flex min-w-0 items-center gap-1.5">{summary}</span>
      </button>
      {open && <div className="border-t border-[var(--cf-border)] p-2.5">{children}</div>}
    </div>
  );
}

/** The chosen accent, shown in a collapsed header — the dot is the actual color in force for
 * the mode on screen, since each accent carries a different shade per mode. */
function AccentSummary() {
  const resolved = useThemeStore((s) => s.resolved);
  const accentId = useAccentStore((s) => s.accentId);
  const option = ACCENT_OPTIONS.find((o) => o.id === accentId) ?? ACCENT_OPTIONS[0]!;
  return (
    <>
      <span
        className="h-3.5 w-3.5 shrink-0 rounded-full"
        style={{ background: resolved === "dark" ? option.dark : option.light }}
      />
      <span className="truncate text-body text-[var(--cf-text-muted)]">{option.label}</span>
    </>
  );
}

/** Accent swatches plus a live preview of the three places the accent actually lands: a solid
 * button, a soft-tinted selection, and a link. Picking a color from eight identical dots is
 * guesswork; seeing what it does to the UI isn't. */
function AccentPicker() {
  const t = useT();
  const resolved = useThemeStore((s) => s.resolved);
  const accentId = useAccentStore((s) => s.accentId);
  const setAccent = useAccentStore((s) => s.setAccent);

  return (
    <div className="space-y-2.5">
      <p className="text-body text-[var(--cf-text-muted)]">{t("settings.accentColorHint")}</p>
      <div className="flex flex-wrap gap-2">
        {ACCENT_OPTIONS.map((option) => {
          const selected = accentId === option.id;
          const swatch = resolved === "dark" ? option.dark : option.light;
          return (
            <Tooltip key={option.id} label={option.label}>
            <button
              aria-label={option.label}
              aria-pressed={selected}
              onClick={() => setAccent(option.id, resolved)}
              className="flex h-7 w-7 items-center justify-center rounded-full transition-transform hover:scale-110"
              style={{
                background: swatch,
                // Ring drawn with a shadow so it doesn't shift the layout when it appears.
                boxShadow: selected ? `0 0 0 2px var(--cf-surface), 0 0 0 4px ${swatch}` : undefined,
              }}
            >
              {selected && <Check size={13} className="text-white" strokeWidth={3} />}
            </button>
            </Tooltip>
          );
        })}
      </div>

      <div className="flex items-center gap-2 rounded-lg border border-[var(--cf-border)] bg-[var(--cf-bg)] px-2.5 py-2">
        {/* The same pair `Button variant="primary"` paints, so the preview stays an honest one. */}
        <span className="rounded-md bg-[var(--cf-accent-solid)] px-2 py-1 text-badge font-medium text-[var(--cf-accent-on-solid)]">
          {t("settings.accentPreviewButton")}
        </span>
        <span className="rounded-md bg-[var(--cf-accent-soft)] px-2 py-1 text-badge font-medium text-[var(--cf-accent)]">
          {t("settings.accentPreviewSelected")}
        </span>
        <span className="text-badge text-[var(--cf-accent)] underline">{t("settings.accentPreviewLink")}</span>
      </div>
    </div>
  );
}

/** The selected scheme, shown in a collapsed header: its name next to a chip painted in its own
 * background and border, so the palette is recognizable before opening anything. */
function ThemeSummary({ mode }: { mode: "light" | "dark" }) {
  const id = useThemeStore((s) => (mode === "dark" ? s.darkThemeId : s.lightThemeId));
  const theme = findTheme(id, mode);
  return (
    <>
      <span
        className="h-3.5 w-3.5 shrink-0 rounded-full border"
        style={{ background: theme.ui.bg, borderColor: theme.ui.border }}
      />
      <span className="truncate text-body text-[var(--cf-text-muted)]">{theme.name}</span>
    </>
  );
}

/** The schemes available for one mode, each previewed with its own colors — a name like
 * "Gruvbox" means nothing until you see it, so every card paints itself in the palette it's
 * offering (background, a comment, a keyword, a string). */
function ThemeGrid({ mode }: { mode: "light" | "dark" }) {
  const selectedId = useThemeStore((s) => (mode === "dark" ? s.darkThemeId : s.lightThemeId));
  const setThemeId = useThemeStore((s) => s.setThemeId);

  return (
    <div className="grid grid-cols-2 gap-2 sm:grid-cols-3">
      {themesFor(mode).map((theme) => {
        const selected = theme.id === selectedId;
        return (
          <button
            key={theme.id}
            onClick={() => void setThemeId(mode, theme.id)}
            aria-pressed={selected}
            style={{ background: theme.ui.bg, borderColor: selected ? undefined : theme.ui.border }}
            className={`cf-focusable overflow-hidden rounded-lg border px-2.5 py-2 text-left ${
              selected ? "border-[var(--cf-accent)] ring-1 ring-[var(--cf-accent)]" : ""
            }`}
          >
            <span className="flex items-center gap-1" style={{ color: theme.ui.text }}>
              <span className="truncate text-body font-medium">{theme.name}</span>
              {selected && <Check size={11} className="ml-auto shrink-0 text-[var(--cf-accent)]" />}
            </span>
            <span className="mt-1 block font-mono text-badge leading-[1.4]">
              <span style={{ color: theme.tokens.comment }}>// preview</span>
              <br />
              <span style={{ color: theme.tokens.keyword }}>const </span>
              <span style={{ color: theme.tokens.variable }}>name</span>
              <span style={{ color: theme.tokens.operator }}> = </span>
              <span style={{ color: theme.tokens.string }}>"{theme.id.split("-")[0]}"</span>
            </span>
          </button>
        );
      })}
    </div>
  );
}

/** One key per step, so the labels stay translatable rather than being derived from the ids. */
const DENSITY_LABELS: Record<TreeDensity, TranslationKey> = {
  compact: "settings.densityCompact",
  cozy: "settings.densityCozy",
  roomy: "settings.densityRoomy",
};

export function ThemeSettings() {
  const t = useT();
  const preference = useThemeStore((s) => s.preference);
  const setPreference = useThemeStore((s) => s.setPreference);
  const resolved = useThemeStore((s) => s.resolved);
  const selectedDensity = useDensityStore((s) => s.density);
  const setDensity = useDensityStore((s) => s.setDensity);

  return (
    <section>
      <h3 className="mb-1 text-title font-semibold">{t("settings.appearance")}</h3>
      <p className="mb-3 text-relaxed text-[var(--cf-text-muted)]">{t("settings.chooseTheme")}</p>
      <div className="flex gap-2">
        {OPTIONS.map(({ id, labelKey, icon: Icon }) => (
          <button
            key={id}
            onClick={() => setPreference(id)}
            aria-pressed={preference === id}
            className={`cf-focusable relative flex flex-1 flex-col items-center gap-1.5 rounded-lg border px-3 py-3 text-body ${
              preference === id
                ? "border-transparent text-[var(--cf-accent)]"
                : "border-[var(--cf-border)] text-[var(--cf-text-muted)] hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
            }`}
          >
            {preference === id && <ActivePill layoutId="cf-theme-mode-pill" inset="-inset-px" radius="rounded-lg" />}
            <span className="relative flex flex-col items-center gap-1.5">
              <Icon size={18} />
              {t(labelKey)}
            </span>
          </button>
        ))}
      </div>

      <h3 className="mb-1 mt-6 text-title font-semibold">{t("settings.treeDensity")}</h3>
      <p className="mb-3 text-relaxed text-[var(--cf-text-muted)]">{t("settings.treeDensityHint")}</p>
      <div className="flex gap-2">
        {DENSITIES.map((density) => (
          <button
            key={density}
            onClick={() => void setDensity(density)}
            aria-pressed={selectedDensity === density}
            className={`cf-focusable cf-interactive relative flex flex-1 flex-col items-center gap-1.5 rounded-lg border px-3 py-3 text-body ${
              selectedDensity === density
                ? "border-transparent text-[var(--cf-accent)]"
                : "border-[var(--cf-border)] text-[var(--cf-text-muted)] hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
            }`}
          >
            {selectedDensity === density && (
              <ActivePill layoutId="cf-tree-density-pill" inset="-inset-px" radius="rounded-lg" />
            )}
            <span className="relative flex flex-col items-center gap-1.5">
              {/* Three stacked bars at the height being chosen — the setting previewing itself,
                  which reads faster than the pixel count does. */}
              <span aria-hidden className="flex flex-col gap-[3px]">
                {[0, 1, 2].map((i) => (
                  <span
                    key={i}
                    style={{ height: densityPx(density) / 3 }}
                    className="w-9 rounded-[2px] bg-current opacity-40"
                  />
                ))}
              </span>
              {t(DENSITY_LABELS[density])}
              <span className="text-badge opacity-70">{densityPx(density)}px</span>
            </span>
          </button>
        ))}
      </div>

      <h3 className="mb-1 mt-6 text-title font-semibold">{t("settings.codeTheme")}</h3>
      <p className="mb-3 text-relaxed text-[var(--cf-text-muted)]">{t("settings.codeThemeHint")}</p>
      <div className="space-y-2">
        {/* Accent first: it's one decision, and it's the one people change most. */}
        <Panel icon={Droplet} title={t("settings.accentColor")} summary={<AccentSummary />}>
          <AccentPicker />
        </Panel>
        <Panel icon={Palette} title={t("settings.editorThemes")} summary={<ThemeSummary mode={resolved} />}>
          <div className="space-y-2">
            {/* The mode you're actually looking at opens first — the other is a deliberate visit. */}
            <Panel
              icon={Moon}
              title={t("settings.forDarkMode")}
              summary={<ThemeSummary mode="dark" />}
              defaultOpen={resolved === "dark"}
            >
              <ThemeGrid mode="dark" />
            </Panel>
            <Panel
              icon={Sun}
              title={t("settings.forLightMode")}
              summary={<ThemeSummary mode="light" />}
              defaultOpen={resolved === "light"}
            >
              <ThemeGrid mode="light" />
            </Panel>
          </div>
        </Panel>
      </div>
    </section>
  );
}
