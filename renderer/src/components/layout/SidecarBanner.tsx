import { Check, Copy, FolderOpen, PlugZap } from "lucide-react";
import { Button } from "../common/Button";
import { useT } from "../../state/languageStore";
import { useSidecarStore } from "../../state/sidecarStore";
import { host } from "../../lib/bridge/host";
import { useCopy } from "../../lib/ui/useCopy";

/**
 * Says that the .NET core stopped answering, which until now nothing did.
 *
 * Every command in the app goes through it, so when it is down the whole application is a window
 * whose buttons do nothing — which is exactly how it was reported from the first real Windows
 * install: a folder picker that never opened, a workspace that would not be created, a settings
 * field that would not save. Three symptoms, one cause, and no way to see it.
 *
 * A banner rather than a toast, and not dismissible. A toast is for something that happened; this is
 * a state the app stays in until it is restarted, and the reason has to still be on screen when the
 * user gets round to reporting it. The detail is selectable and has a copy button for the same
 * reason — the previous version of this information could not even be selected out of the UI.
 */
export function SidecarBanner() {
  const t = useT();
  const status = useSidecarStore((s) => s.status);
  const detail = useSidecarStore((s) => s.detail);
  const logsDirectory = useSidecarStore((s) => s.logsDirectory);
  const [copied, copy] = useCopy();

  // `starting` is not reported: the core takes a moment to bind its endpoint on every launch, and a
  // banner during the normal startup would cry wolf on every single run.
  if (status !== "down") return null;

  const report = [detail, logsDirectory].filter(Boolean).join("\n");

  return (
    <div
      role="alert"
      className="flex shrink-0 items-start gap-2.5 border-b border-[var(--cf-danger)]/40 bg-[var(--cf-danger)]/10 px-4 py-2.5"
    >
      <PlugZap size={16} aria-hidden className="mt-0.5 shrink-0 text-[var(--cf-danger)]" />

      <div className="min-w-0 flex-1">
        <p className="text-ui font-semibold text-[var(--cf-text)]">{t("sidecar.downTitle")}</p>
        <p className="mt-0.5 text-body text-[var(--cf-text-muted)]">{t("sidecar.downBody")}</p>

        {detail !== null && (
          // `select-text` explicitly: the surrounding chrome is not selectable, and this line is the
          // one piece of the app whose whole purpose is to be copied out of it.
          <p className="mt-1 select-text break-words font-mono text-badge text-[var(--cf-text-muted)]">
            {detail}
          </p>
        )}
        {logsDirectory !== null && (
          <p className="mt-1 select-text break-all text-badge text-[var(--cf-text-muted)]">
            {t("sidecar.downLogs", { path: logsDirectory })}
          </p>
        )}
      </div>

      <div className="flex shrink-0 items-center gap-1.5">
        <Button
          variant="ghost"
          size="sm"
          icon={copied ? Check : Copy}
          onClick={() => copy(report)}
          disabled={report === ""}
        >
          {copied ? t("common.copied") : t("common.copy")}
        </Button>
        {/* The way out when the clipboard is not available at all — which on Windows it can be, for
            reasons no code here controls. Attaching the log file beats retyping a banner. */}
        <Button variant="ghost" size="sm" icon={FolderOpen} onClick={() => void host.openLogs()}>
          {t("sidecar.openLogs")}
        </Button>
      </div>
    </div>
  );
}
