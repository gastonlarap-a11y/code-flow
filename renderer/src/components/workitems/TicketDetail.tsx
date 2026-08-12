import { useEffect, useMemo } from "react";
import { Chip } from "../common/Chip";
import { renderMarkdown } from "../../lib/markdown";
import { useTicketStore } from "../../state/ticketStore";
import { useT } from "../../state/languageStore";
import type { Ticket, TicketLink } from "../../types/domain";

/**
 * One ticket, rendered as the thing a review will be judged against.
 *
 * <b>The criteria lead, and their provenance is on screen.</b> Which field they came from decides
 * whether a review is worth running: on a real board the field literally named "acceptance
 * criteria" held a hyphen while the requirements sat in the description. Showing `mode` and
 * `field` is what stops someone linking a ticket whose specification is empty and only finding out
 * when the review reports that nothing is met.
 *
 * <b>So is the link.</b> This pane used to show a work item with no indication of which repository
 * or branch it belonged to — and since the list it is opened from spans the whole workspace, that
 * made a ticket from another repository indistinguishable from this branch's own. Someone asked, in
 * as many words, how they were supposed to tell (`WI-021`).
 */
export function TicketDetail({
  ticket,
  links,
  currentProjectId,
  currentBranch,
}: {
  ticket: Ticket;
  links: TicketLink[];
  currentProjectId: string;
  currentBranch: string | null;
}) {
  const t = useT();
  const here = links.find(
    (link) => link.project_id === currentProjectId && link.branch === currentBranch,
  );
  const criteria = useTicketStore((s) => s.criteria[ticket.id]);
  const criteriaFor = useTicketStore((s) => s.criteriaFor);

  useEffect(() => {
    void criteriaFor(ticket.id);
  }, [criteriaFor, ticket.id]);

  // Read out before memoising: depending on `criteria?.markdown` while the compiler infers
  // `criteria` makes the two disagree, and it then declines to optimise the component at all.
  const markdown = criteria?.markdown ?? "";
  const body = useMemo(() => (markdown ? renderMarkdown(markdown) : ""), [markdown]);

  return (
    <div className="min-h-0 flex-1 overflow-y-auto p-4">
      <div className="flex flex-wrap items-center gap-1.5">
        <Chip>{ticket.work_item_type}</Chip>
        <Chip tone="accent">{ticket.state}</Chip>
        <span className="text-badge text-[var(--cf-text-muted)]">
          {ticket.assigned_to ?? t("tickets.unassigned")}
        </span>
      </div>

      <h3 className="mt-3 text-title font-semibold">{ticket.title}</h3>

      {/* Which branch's work this is, said before anything else about it. The accent case is the
          one the pre-commit review will judge against; the muted case is a ticket the user opened
          from the workspace-wide list on purpose, so it names where it does belong rather than
          shouting about where it does not. */}
      {here ? (
        <p className="mt-1 text-ui text-[var(--cf-accent)]">
          {t("tickets.linkedHere", { repo: here.project_name, branch: here.branch })}
        </p>
      ) : (
        links.length > 0 && (
          <p className="mt-1 text-ui text-[var(--cf-text-muted)]">
            {t("tickets.linkedElsewhere", {
              where: links.map((link) => `${link.project_name} · ${link.branch}`).join(", "),
            })}
          </p>
        )
      )}

      <section className="mt-5">
        <div className="flex flex-wrap items-baseline gap-2">
          <h4 className="text-relaxed font-semibold">{t("tickets.criteria")}</h4>
          {criteria?.field && (
            <span className="text-badge text-[var(--cf-text-muted)]">
              {t("tickets.criteriaFrom", { field: criteria.field })}
            </span>
          )}
        </div>

        {criteria?.mode === "none" && (
          // Stated rather than left blank. That no field carried requirements is the fact the
          // person needs before they run a review that will report everything as unmet.
          <p className="mt-2 rounded-card border border-dashed border-[var(--cf-border)] p-3 text-body text-[var(--cf-text-muted)]">
            {t("tickets.criteriaNone")}
          </p>
        )}

        {criteria?.mode === "prose" && (
          <p className="mt-1 text-ui text-[var(--cf-text-muted)]">{t("tickets.criteriaProse")}</p>
        )}

        {criteria?.mode === "list" && (
          <ol className="mt-2 flex flex-col gap-1.5">
            {criteria.items.map((item, index) => (
              <li key={item} className="flex gap-2 text-body">
                <span className="shrink-0 font-mono text-ui text-[var(--cf-text-muted)]">
                  AC-{index + 1}
                </span>
                <span>{item}</span>
              </li>
            ))}
          </ol>
        )}

        {criteria?.mode === "prose" && body && (
          // Sanitised by `lib/markdown.ts` — this text comes from a work item anybody on the board
          // can edit, so it is never trusted as markup.
          <div className="cf-prose mt-3 text-body" dangerouslySetInnerHTML={{ __html: body }} />
        )}
      </section>

      <p className="mt-6 text-badge text-[var(--cf-text-muted)]">
        {t("tickets.syncedAt", { when: new Date(ticket.synced_at).toLocaleString() })}
      </p>
    </div>
  );
}
