import { Check, Download, Loader2, RefreshCw, RotateCw, Sparkles, TriangleAlert } from "lucide-react";
import { Button } from "../common/Button";
import { useUpdateStore } from "../../state/updateStore";
import { useLanguageStore, useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";

/**
 * Why a check could not answer, in the user's language.
 *
 * The store carries the failure as a message string, and for a check that ran but could not reach
 * an answer that string is a stable reason id from the updater bridge. Mapping it here is what
 * turns "no-credential" into a sentence that says what to do about it — the panel used to print one
 * fixed line for every failure, and that line was wrong.
 */
const REASONS: Record<string, TranslationKey> = {
  "no-credential": "update.reasonNoCredential",
  unauthorized: "update.reasonUnauthorized",
  "no-release": "update.reasonNoRelease",
  "no-asset": "update.reasonNoAsset",
  unreachable: "update.reasonUnreachable",
};

function reasonText(error: string, t: (key: TranslationKey) => string): string {
  const key = REASONS[error];
  return key ? t(key) : "";
}

/** Self-service updater: downloads the published GitHub release for a newer signed build and
 * installs it in place, so the user never has to uninstall/reinstall by hand. Only works in the
 * packaged app (in a plain dev server there's no installed binary to replace — `check()` errors).
 *
 * All of it — status, the pending release, download progress — lives in the update store, which
 * the hourly background check writes to as well. So this panel already knows about an update
 * found minutes ago instead of making the user press "Check for updates" to be told what the
 * title bar has been showing all along. */
export function UpdateSection() {
  const t = useT();
  const locale = useLanguageStore((s) => (s.language === "es" ? "es-ES" : "en-US"));
  const version = useUpdateStore((s) => s.currentVersion);
  const status = useUpdateStore((s) => s.status);
  const update = useUpdateStore((s) => s.update);
  const progress = useUpdateStore((s) => s.progress);
  const error = useUpdateStore((s) => s.error);
  const lastCheckedAt = useUpdateStore((s) => s.lastCheckedAt);
  const checkNow = useUpdateStore((s) => s.checkNow);
  const install = useUpdateStore((s) => s.install);
  const restart = useUpdateStore((s) => s.restart);
  const openNotes = useUpdateStore((s) => s.openNotes);

  return (
    <div className="mt-6 border-t border-[var(--cf-border)] pt-4">
      <h3 className="mb-1 text-title font-semibold">{t("settings.updatesTitle")}</h3>
      <p className="mb-3 text-relaxed text-[var(--cf-text-muted)]">
        {t("settings.updatesHint")} {t("update.autoHint")}
      </p>

      {version && (
        <p className="mb-3 text-body text-[var(--cf-text-muted)]">
          {t("settings.currentVersion")}: <span className="font-mono text-[var(--cf-text)]">v{version}</span>
          {lastCheckedAt !== null && (
            <>
              {" · "}
              {t("update.lastChecked", {
                time: new Date(lastCheckedAt).toLocaleTimeString(locale, {
                  hour: "2-digit",
                  minute: "2-digit",
                }),
              })}
            </>
          )}
        </p>
      )}

      {/* Idle / up-to-date / error → "Check for updates" */}
      {(status === "idle" || status === "checking" || status === "uptodate" || status === "error") && (
        <Button
          variant="secondary"
          icon={RefreshCw}
          pending={status === "checking"}
          onClick={() => void checkNow(true)}
        >
          {status === "checking" ? t("settings.checkingUpdates") : t("settings.checkForUpdates")}
        </Button>
      )}

      {status === "uptodate" && (
        <p className="mt-2 flex items-center gap-1.5 text-body text-[var(--cf-success)]">
          <Check size={13} />
          {t("settings.upToDate")}
        </p>
      )}

      {status === "available" && update && (
        <div className="flex flex-col gap-2">
          <p className="text-relaxed text-[var(--cf-text)]">
            {t("settings.updateAvailable", { version: `v${update.version}` })}
          </p>
          <div className="flex items-center gap-2">
            <Button variant="primary" icon={Download} onClick={() => void install()}>
              {t("settings.installUpdate", { version: `v${update.version}` })}
            </Button>
            {/* Reading first is a legitimate answer to "should I update?", so it sits next to
                the install button rather than behind it. */}
            <Button variant="secondary" icon={Sparkles} onClick={openNotes}>
              {t("update.seeWhatsNew")}
            </Button>
          </div>
        </div>
      )}

      {status === "downloading" && (
        <div className="flex flex-col gap-2">
          <p className="flex items-center gap-1.5 text-relaxed text-[var(--cf-text)]">
            <Loader2 size={14} className="animate-spin" />
            {t("settings.downloadingUpdate", { progress: progress })}
          </p>
          <div className="h-1.5 w-full max-w-xs overflow-hidden rounded-full bg-[var(--cf-border)]">
            <div className="h-full rounded-full bg-[var(--cf-accent)] transition-all" style={{ width: `${progress}%` }} />
          </div>
        </div>
      )}

      {status === "ready" && (
        <div className="flex flex-col gap-2">
          <p className="flex items-center gap-1.5 text-relaxed text-[var(--cf-success)]">
            <Check size={14} />
            {/* macOS cannot apply the update itself while the app is unsigned, so offering a
                restart there would promise something that does not happen. */}
            {update?.installKind === "manual" ? t("settings.updateReadyManual") : t("settings.updateReady")}
          </p>
          {update?.installKind !== "manual" && (
            <Button variant="primary" icon={RotateCw} className="self-start" onClick={() => void restart()}>
              {t("settings.restartNow")}
            </Button>
          )}
        </div>
      )}

      {status === "error" && (
        <p className="mt-2 flex items-start gap-1.5 text-body text-[var(--cf-danger)]">
          <TriangleAlert size={13} className="mt-0.5 shrink-0" />
          <span>
            {t("settings.updateError")} {reasonText(error, t)}
            {/* The raw message is only worth showing when it is not one of the known reasons —
                otherwise it is the reason id, repeated in a less readable form. */}
            {error && !REASONS[error] && (
              <span className="mt-0.5 block break-all font-mono text-badge opacity-80">{error}</span>
            )}
          </span>
        </p>
      )}
    </div>
  );
}
