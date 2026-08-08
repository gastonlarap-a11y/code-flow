import { useEffect } from "react";
import { Download, Loader2, RotateCw, Sparkles, TriangleAlert } from "lucide-react";
import { Button } from "../common/Button";
import { Modal } from "../common/Modal";
import { openUrl } from "../../lib/bridge/shell";
import { renderMarkdown } from "../../lib/markdown";
import { useUpdateStore } from "../../state/updateStore";
import { useLanguageStore, useT } from "../../state/languageStore";

/** The `date` the updater reports comes straight from the release manifest and isn't always a
 * shape `Date` can read (It is written as `2026-07-28 09:14:02.000 +00:00:00`). A release
 * without a legible date simply doesn't show one. */
function releaseDate(raw: string | undefined, locale: string): string | null {
  if (!raw) return null;
  const parsed = new Date(raw.replace(" ", "T").replace(/ \+00:00:00$/, "Z"));
  if (Number.isNaN(parsed.getTime())) return null;
  return parsed.toLocaleDateString(locale, { day: "numeric", month: "long", year: "numeric" });
}

/**
 * Sends links inside the notes to the system browser.
 *
 * These are markdown links rendered by `marked`, which doesn't add `target="_blank"` — so
 * without this a click would navigate the app's own webview to GitHub and leave the user
 * staring at a website where CodeFlow used to be, with no way back. The notes are written by
 * whoever cut the release, so only http(s) is followed; anything else is simply swallowed.
 */
function openLinkExternally(e: React.MouseEvent<HTMLDivElement>) {
  const anchor = (e.target as HTMLElement).closest("a");
  if (!anchor) return;
  e.preventDefault();
  const href = anchor.getAttribute("href") ?? "";
  if (/^https?:\/\//i.test(href)) void openUrl(href).catch(() => {});
}

/**
 * What's new in the release that's waiting — the release notes themselves, not just "an update
 * is available", so the user can decide whether to restart their work for it now or later.
 *
 * Also where the install happens: it's the one surface reached from the title bar badge, so
 * everything the user needs (read, install, restart) is in one place instead of sending them to
 * Settings to finish what they started here.
 */
export function UpdateNotesModal() {
  const t = useT();
  const locale = useLanguageStore((s) => (s.language === "es" ? "es-ES" : "en-US"));
  const open = useUpdateStore((s) => s.notesOpen);
  const closeNotes = useUpdateStore((s) => s.closeNotes);
  const update = useUpdateStore((s) => s.update);
  const status = useUpdateStore((s) => s.status);
  const progress = useUpdateStore((s) => s.progress);
  const error = useUpdateStore((s) => s.error);
  const install = useUpdateStore((s) => s.install);
  const restart = useUpdateStore((s) => s.restart);

  // The dialog hooks used to be called here, before the early return below, with an `active` flag
  // to keep the focus trap dormant until there was an update — a hook after that return would run
  // on some renders and not others. `Modal` owns them now and only mounts when there is something
  // to show, so the ordering hazard is gone rather than worked around.

  useEffect(() => {
    if (!open) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") closeNotes();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [open, closeNotes]);

  if (!open || !update) return null;

  const notes = update.body?.trim();
  const date = releaseDate(update.date, locale);
  const busy = status === "downloading";

  return (
    <Modal
      title="update.whatsNew"
      titleParams={{ version: `v${update.version}` }}
      icon={Sparkles}
      size="lg"
      scroll
      onClose={closeNotes}
      footer={
        // A full-width wrapper because this footer is not an action row: it stacks the download
        // progress and any error above the buttons, and all three have to stay pinned below the
        // release notes rather than scroll away with them.
        <div className="w-full">
          {error && status === "error" && (
            <p className="mb-2 flex items-start gap-1.5 text-body text-[var(--cf-danger)]">
              <TriangleAlert size={14} className="mt-0.5 shrink-0" aria-hidden />
              <span className="min-w-0 flex-1 break-all">{error}</span>
            </p>
          )}

          {busy && (
            <div className="mb-3">
              <p className="mb-1.5 flex items-center gap-1.5 text-ui text-[var(--cf-text)]">
                <Loader2 size={14} className="animate-spin" aria-hidden />
                {t("settings.downloadingUpdate", { progress })}
              </p>
              <div className="h-1.5 w-full overflow-hidden rounded-full bg-[var(--cf-border)]">
                <div
                  className="h-full rounded-full bg-[var(--cf-accent)] transition-all"
                  style={{ width: `${progress}%` }}
                />
              </div>
            </div>
          )}

          <div className="flex items-center justify-end gap-2">
            {status === "ready" ? (
              <>
                <p className="mr-auto flex items-center gap-1.5 text-ui text-[var(--cf-success)]">
                  {t("settings.updateReady")}
                </p>
                <Button variant="primary" icon={RotateCw} onClick={() => void restart()}>
                  {t("settings.restartNow")}
                </Button>
              </>
            ) : (
              <>
                {/* Deliberately just closes: an update is never forced, and the badge stays in
                    the title bar so this window is one click away again. */}
                <Button variant="ghost" disabled={busy} onClick={closeNotes}>
                  {t("update.later")}
                </Button>
                <Button variant="primary" icon={Download} pending={busy} onClick={() => void install()}>
                  {t("settings.installUpdate", { version: `v${update.version}` })}
                </Button>
              </>
            )}
          </div>
        </div>
      }
    >
      <p className="mb-3 flex flex-wrap items-center gap-1.5 text-badge text-[var(--cf-text-muted)]">
        <span className="font-mono">v{update.currentVersion}</span>
        <span aria-hidden>→</span>
        <span className="font-mono text-[var(--cf-accent)]">v{update.version}</span>
        {date && <span>· {date}</span>}
      </p>
      {notes ? (
        <div
          className="cf-markdown-preview text-body"
          onClick={openLinkExternally}
          dangerouslySetInnerHTML={{ __html: renderMarkdown(notes) }}
        />
      ) : (
        <p className="text-ui text-[var(--cf-text-muted)]">{t("update.noNotes")}</p>
      )}
    </Modal>
  );
}
