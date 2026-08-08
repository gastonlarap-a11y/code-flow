import { LogOut, Trash2 } from "lucide-react";
import { ActivePill } from "../common/ActivePill";
import { Button } from "../common/Button";
import { useLanguageStore, useT } from "../../state/languageStore";
import type { Language } from "../../lib/i18n/translations";
import { quitApp, resetAppData } from "../../lib/ipc/commands";
import { confirmAction } from "../../state/confirmStore";
import { usePlatform } from "../../lib/platform";
import { UpdateSection } from "./UpdateSection";

// Language names stay in their own language (endonyms) — "English"/"Español" don't change
// depending on the currently selected UI language, same as any language picker.
const OPTIONS: { id: Language; label: string }[] = [
  { id: "en", label: "English" },
  { id: "es", label: "Español" },
];

export function GeneralSettings() {
  const t = useT();
  const language = useLanguageStore((s) => s.language);
  const setLanguage = useLanguageStore((s) => s.setLanguage);
  const platform = usePlatform();
  const dataPath = platform === "windows" ? "C:\\CodeFlow" : "~/CodeFlow";

  return (
    <section>
      <h3 className="mb-1 text-title font-semibold">{t("settings.general")}</h3>
      <p className="mb-3 text-relaxed text-[var(--cf-text-muted)]">{t("settings.languageHint")}</p>
      {/* A choice, not a tab strip: these govern no panel, so they stay buttons and report their
          state with `aria-pressed` — which is what none of them did before. */}
      <div className="flex gap-2">
        {OPTIONS.map((opt) => (
          <button
            key={opt.id}
            onClick={() => setLanguage(opt.id)}
            aria-pressed={language === opt.id}
            className={`cf-focusable relative flex-1 rounded-lg border px-3 py-2.5 text-relaxed font-medium ${
              language === opt.id
                ? "border-transparent text-[var(--cf-accent)]"
                : "border-[var(--cf-border)] text-[var(--cf-text-muted)] hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
            }`}
          >
            {language === opt.id && <ActivePill layoutId="cf-language-pill" inset="-inset-px" radius="rounded-lg" />}
            <span className="relative">{opt.label}</span>
          </button>
        ))}
      </div>
      <p className="mt-2 text-body text-[var(--cf-text-muted)]">{t("settings.translationNote")}</p>

      <UpdateSection />

      <div className="mt-6 border-t border-[var(--cf-border)] pt-4">
        <h3 className="mb-1 text-title font-semibold">{t("settings.appLifecycle")}</h3>
        <p className="mb-3 text-relaxed text-[var(--cf-text-muted)]">{t("settings.appLifecycleHint")}</p>
        <div className="flex flex-wrap gap-2">
          <Button
            variant="danger"
            icon={LogOut}
            onClick={async () => {
              if (await confirmAction(t("settings.quitConfirm"))) void quitApp();
            }}
          >
            {t("settings.quitApp")}
          </Button>
        </div>
      </div>

      <div className="mt-6 border-t border-[var(--cf-border)] pt-4">
        <h3 className="mb-1 text-title font-semibold">{t("settings.resetData")}</h3>
        <p className="mb-3 text-relaxed text-[var(--cf-text-muted)]">
          {t("settings.resetDataHint", { path: dataPath })}
        </p>
        <Button
          variant="danger"
          icon={Trash2}
          onClick={async () => {
            if (await confirmAction(t("settings.resetDataConfirm", { path: dataPath }))) void resetAppData();
          }}
        >
          {t("settings.resetDataButton")}
        </Button>
      </div>
    </section>
  );
}
