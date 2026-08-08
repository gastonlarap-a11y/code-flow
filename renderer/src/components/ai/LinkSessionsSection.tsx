import { Link2, X } from "lucide-react";
import { IconButton } from "../common/IconButton";
import { useT } from "../../state/languageStore";
import { usePrStore } from "../../state/prStore";
import { useJobsStore, EMPTY_JOBS } from "../../state/jobsStore";

/**
 * The link reviews opened this session, so one can be brought back.
 *
 * A PR reviewed from a link belongs to no project, so it's in no sidebar and no PR list: without
 * this list, the panel showing it was the only way to reach it, and closing that — or pressing
 * "New chat" — left its review, its comments and its approval stranded in memory with no route
 * back. Everything is still there; this is the door.
 *
 * Rendered only when no link session is on screen, since the one on screen doesn't need a way
 * back to itself.
 */
export function LinkSessionsSection() {
  const t = useT();
  const sessions = usePrStore((s) => s.linkPrHistory);
  const openLinkPr = usePrStore((s) => s.openLinkPr);
  const forgetLinkPr = usePrStore((s) => s.forgetLinkPr);
  const jobsByBucket = useJobsStore((s) => s.byProject);

  if (sessions.length === 0) return null;

  return (
    <div className="shrink-0 border-b border-[var(--cf-border)]">
      <p className="flex items-center gap-1.5 px-3 py-2 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
        <Link2 size={11} />
        {t("prLink.sessionsTitle")}
      </p>
      <div className="px-1.5 pb-1.5">
        {sessions.map((session) => {
          const runs = (jobsByBucket[`pr-link:${session.url}`] ?? EMPTY_JOBS).length;
          return (
            <div key={session.url} className="group flex items-center gap-1">
              <button
                onClick={() => {
                  openLinkPr(session);
                }}
                className="min-w-0 flex-1 rounded-md px-1.5 py-1 text-left hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
              >
                <span className="block truncate text-ui text-[var(--cf-text)]">
                  #{session.pr.id} {session.pr.title}
                </span>
                <span className="block truncate text-badge text-[var(--cf-text-muted)]">
                  {session.repoLabel}
                  {runs > 0 && ` · ${t("prLink.sessionEntries", { n: runs })}`}
                </span>
              </button>
              {/* `X` is right: forgetting a link session dismisses it from the panel, it does not
                  delete anything stored on the host. */}
              <IconButton
                label="prLink.forgetSession"
                icon={X}
                className="shrink-0 opacity-55 group-hover:opacity-100 group-focus-within:opacity-100"
                onClick={() => forgetLinkPr(session.url)}
              />
            </div>
          );
        })}
      </div>
    </div>
  );
}
