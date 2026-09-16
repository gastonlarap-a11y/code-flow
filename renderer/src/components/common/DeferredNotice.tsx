import { Construction } from "lucide-react";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";

/**
 * "This part has no backend yet", said where the feature is, before anything is clicked.
 *
 * Two features ship their whole frontend against a sidecar that was deferred out of v1 — the
 * debugger (`12-debugging.md`) and the API client's gRPC protocol (`08-api-client.md`). Both are
 * deliberate and both are documented; what was not was that **a person could not tell**. The panels
 * are mounted, gRPC is one of six protocols in the picker, and the only thing distinguishing a
 * deferred feature from a broken one was the raw `unknown command 'debug_start'` a button produced.
 *
 * Said up front rather than caught at the failure, because the panels know statically that their
 * backend is missing — waiting for the error means the user finds out by being told something went
 * wrong, which is the wrong word for a decision somebody made on purpose.
 *
 * It does not disable what still works: a gRPC request can be authored and saved into a collection
 * today, through commands that do exist, and will send the day the backend lands.
 */
export function DeferredNotice({ feature }: { feature: TranslationKey }) {
  const t = useT();

  return (
    <div
      role="status"
      className="flex shrink-0 items-start gap-2 border-b border-[var(--cf-border)] bg-[color-mix(in_oklab,var(--cf-warning)_8%,transparent)] px-3 py-2"
    >
      <Construction size={14} className="mt-0.5 shrink-0 text-[var(--cf-warning)]" aria-hidden />
      <p className="min-w-0 text-badge leading-relaxed text-[var(--cf-text-muted)]">
        <span className="font-medium text-[var(--cf-text)]">{t("deferred.title", { feature: t(feature) })}</span>{" "}
        {t("deferred.body")}
      </p>
    </div>
  );
}
