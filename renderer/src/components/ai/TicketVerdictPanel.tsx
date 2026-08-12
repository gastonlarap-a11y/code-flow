import { useState } from "react";
import { CircleAlert, CircleCheck, CircleHelp, CircleSlash, Send } from "lucide-react";
import { Button } from "../common/Button";
import { Chip } from "../common/Chip";
import { useT } from "../../state/languageStore";
import { useTicketStore } from "../../state/ticketStore";
import type { CriterionVerdict, ParsedTicketVerdict } from "../../lib/parseTicketVerdict";
import type { TranslationKey } from "../../lib/i18n/translations";

/** How each verdict reads: its icon, its colour token and its label. */
const VERDICT_STYLE: Record<CriterionVerdict, { icon: typeof CircleCheck; token: string; labelKey: TranslationKey }> = {
  cumple: { icon: CircleCheck, token: "--cf-success", labelKey: "ticketReview.verdictMet" },
  "no cumple": { icon: CircleAlert, token: "--cf-danger", labelKey: "ticketReview.verdictUnmet" },
  parcial: { icon: CircleSlash, token: "--cf-warning", labelKey: "ticketReview.verdictPartial" },
  "no verificable": { icon: CircleHelp, token: "--cf-text-muted", labelKey: "ticketReview.verdictUnknown" },
};

const COVERAGE_LABEL: Record<string, TranslationKey> = {
  completa: "ticketReview.coverageComplete",
  incompleta: "ticketReview.coverageIncomplete",
  "no verificable": "ticketReview.coverageUnknown",
};

function CriterionRow({ criterion }: { criterion: ParsedTicketVerdict["criteria"][number] }) {
  const t = useT();
  const style = VERDICT_STYLE[criterion.verdict];
  const Icon = style.icon;

  return (
    <li className="rounded-lg border border-[var(--cf-border)] bg-[var(--cf-surface)] px-3 py-2">
      <div className="flex items-start gap-2">
        <Icon size={15} className="mt-0.5 shrink-0" style={{ color: `var(${style.token})` }} aria-hidden />
        <div className="min-w-0 flex-1">
          <p className="text-body font-medium">
            <span className="text-[var(--cf-text-muted)]">{criterion.id}</span> · {criterion.criterion}
          </p>
          <p className="mt-0.5 text-ui" style={{ color: `var(${style.token})` }}>
            {t(style.labelKey)}
            {criterion.confidence !== null && (
              <span className="ml-1.5 text-[var(--cf-text-muted)]">{criterion.confidence}/100</span>
            )}
          </p>
          {criterion.evidence && (
            <p className="mt-1 text-ui text-[var(--cf-text-muted)]">
              <span className="font-medium">{t("ticketReview.evidence")}</span> {criterion.evidence}
            </p>
          )}
        </div>
      </div>
    </li>
  );
}

/**
 * The acceptance-criteria half of a ticket review: the table, then the coverage block.
 *
 * A component of its own because it is the one genuinely separable piece of the review panel — the
 * findings below it are the same whether a ticket was judged or not, and only this appears when one
 * was. `XLANG-016` is what it renders.
 */
export function TicketVerdictPanel({ verdict }: { verdict: ParsedTicketVerdict }) {
  const t = useT();

  // A ticket that does not describe this change makes every verdict below it meaningless, so it is
  // said first and loudly. It is a linking mistake, not a code one — the fix is in Work items.
  if (verdict.coverage && !verdict.coverage.relevant) {
    return (
      <section className="rounded-lg border border-[var(--cf-warning)] bg-[var(--cf-surface)] px-3.5 py-3">
        <h4 className="mb-1 text-ui font-semibold text-[var(--cf-warning)]">
          {t("ticketReview.notRelevant")}
        </h4>
        {verdict.coverage.relevance && <p className="text-ui">{verdict.coverage.relevance}</p>}
        {verdict.coverage.summary && (
          <p className="mt-1 text-ui text-[var(--cf-text-muted)]">{verdict.coverage.summary}</p>
        )}
        <p className="mt-2 text-badge text-[var(--cf-text-muted)]">{t("ticketReview.notRelevantHint")}</p>
      </section>
    );
  }

  return (
    <>
      <section>
        <h4 className="mb-2 text-ui font-semibold">{t("ticketReview.criteriaTitle")}</h4>
        {verdict.criteria.length > 0 ? (
          <ul className="space-y-2">
            {verdict.criteria.map((criterion) => (
              <CriterionRow key={criterion.id} criterion={criterion} />
            ))}
          </ul>
        ) : (
          // The model was asked for this section and did not produce it, or the ticket declares
          // nothing verifiable. Both are worth saying; neither loses the findings.
          <p className="text-ui text-[var(--cf-text-muted)]">{t("ticketReview.noCriteria")}</p>
        )}
      </section>

      {verdict.coverage && (
        <section className="rounded-lg border border-[var(--cf-border)] bg-[var(--cf-surface)] px-3.5 py-2.5">
          <h4 className="mb-1 text-ui font-semibold">
            {t("ticketReview.coverageTitle")} ·{" "}
            <span className="font-normal">
              {t(COVERAGE_LABEL[verdict.coverage.coverage] ?? "ticketReview.coverageUnknown")}
            </span>
          </h4>
          {verdict.coverage.summary && <p className="text-ui">{verdict.coverage.summary}</p>}
          {verdict.coverage.missing && (
            <p className="mt-1 text-ui text-[var(--cf-text-muted)]">
              <span className="font-medium">{t("ticketReview.missing")}</span> {verdict.coverage.missing}
            </p>
          )}
          {verdict.coverage.outOfScope && (
            <p className="mt-1 text-ui text-[var(--cf-text-muted)]">
              <span className="font-medium">{t("ticketReview.outOfScope")}</span> {verdict.coverage.outOfScope}
            </p>
          )}
        </section>
      )}
    </>
  );
}

/**
 * The one control in this app that writes to somebody's board.
 *
 * A button, never automatic. A review is run repeatedly while work is in progress, so publishing
 * every one of them would fill the work item with drafts of an answer — and the text is already on
 * screen above this, which is what makes an explicit press an informed one (`WI-022`).
 *
 * It publishes `body` verbatim: the same markdown the panel above rendered, converted to HTML by
 * the sidecar. Rebuilding it from the stored run would let what was approved and what is posted
 * drift apart.
 */
export function PublishVerdict({ body }: { body: string }) {
  const t = useT();
  const linked = useTicketStore((s) => s.linked);
  const commenting = useTicketStore((s) => s.commenting);
  const comment = useTicketStore((s) => s.comment);
  const [posted, setPosted] = useState(false);

  // Nothing to publish onto. The panel can render a stored verdict for a branch whose link was
  // removed since, and a button that could only fail is worse than no button.
  if (!linked) return null;

  return (
    <div className="flex items-center gap-2">
      <Button
        variant="secondary"
        size="sm"
        disabled={commenting || posted}
        onClick={() => {
          void comment(body).then((ok) => setPosted(ok));
        }}
      >
        <Send size={14} aria-hidden />
        {commenting ? t("ticketReview.publishing") : t("ticketReview.publish", { id: linked.external_id })}
      </Button>

      {/* Latched rather than a toast: the answer to "did I already send this?" has to still be
          there a minute later, and posting the same verdict twice is the mistake to prevent. */}
      {posted && (
        <span className="text-badge text-[var(--cf-success)]">{t("ticketReview.published")}</span>
      )}
    </div>
  );
}

/** A one-line count for the header: how many criteria are met, out of how many. */
export function VerdictSummary({ verdict }: { verdict: ParsedTicketVerdict }) {
  const t = useT();
  const met = verdict.criteria.filter((c) => c.verdict === "cumple").length;

  if (verdict.criteria.length === 0) return null;

  return (
    <Chip tone="accent">
      {t("ticketReview.countsSummary", { met: String(met), total: String(verdict.criteria.length) })}
    </Chip>
  );
}
