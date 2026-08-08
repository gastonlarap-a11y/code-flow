import { runStatSegments } from "../../lib/ui/runStats";
import { useT } from "../../state/languageStore";
import { Tooltip } from "./Tooltip";

/**
 * What a finished AI run cost and how much of the change it saw.
 *
 * Local to the app and never published: the sidecar stamps this line onto the text it stores, and
 * every path that composes a comment for the pull-request host builds its own text from the
 * findings instead. Two reviews of the same PR, one twice as slow as the other, used to be
 * indistinguishable here — the numbers existed, and the review tab dropped them on the floor.
 *
 * The tooltip exists for one segment in particular. The Claude CLI reports `total_cost_usd` whatever
 * the account is, computed from the token counts against the model's list price, so a subscriber on
 * a flat plan is shown money nobody will charge them. The figure is worth keeping — it is the
 * quickest way to compare two runs — but a number that means something other than it appears to
 * needs saying out loud, and "equiv. API" on its own was not enough to stop the question.
 */
export function RunStats({ footer, className = "" }: { footer: string | null; className?: string }) {
  const t = useT();
  const segments = runStatSegments(footer);
  if (segments.length === 0) return null;

  return (
    <Tooltip label={t("ai.runStatsHint")} placement="top">
      <div className={`flex flex-wrap items-center gap-1.5 text-badge text-[var(--cf-text-muted)] ${className}`}>
        {segments.map((segment, index) => (
          <span
            key={`${index}:${segment}`}
            className="select-text rounded border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] px-1.5 py-0.5"
          >
            {segment}
          </span>
        ))}
      </div>
    </Tooltip>
  );
}
