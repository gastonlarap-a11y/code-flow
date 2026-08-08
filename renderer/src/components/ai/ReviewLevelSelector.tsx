import { Tooltip } from "../common/Tooltip";
import { useT } from "../../state/languageStore";
import type { ReviewLevel } from "../../state/prStore";

/** Compact segmented control for the review depth (básico / completo / ultra). The choice is
 * shared through `prStore`, so wherever a review is launched from — the AI panel, the title-bar
 * shortcut, the "review a PR from its link" modal — it runs at the same level. */
export function ReviewLevelSelector({
  value,
  onChange,
  disabled,
}: {
  value: ReviewLevel;
  onChange: (level: ReviewLevel) => void;
  disabled: boolean;
}) {
  const t = useT();
  const levels: ReviewLevel[] = ["basico", "completo", "ultra"];
  return (
    // A choice, not a tab strip — it governs no panel. `aria-pressed` is the state it never
    // reported; the group keeps one tooltip explaining what "level" means.
    <Tooltip label={t("pr.levelHint")}>
      <div className="flex items-center rounded-md border border-[var(--cf-border)] p-0.5">
      {levels.map((level) => (
        <button
          key={level}
          onClick={() => onChange(level)}
          disabled={disabled}
          aria-pressed={value === level}
          className={`cf-focusable rounded px-2 py-1 text-badge font-medium capitalize transition-colors disabled:opacity-50 ${
            value === level
              ? "bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]"
              : "text-[var(--cf-text-muted)] hover:text-[var(--cf-text)]"
          }`}
        >
          {t(`pr.level.${level}` as never)}
        </button>
      ))}
      </div>
    </Tooltip>
  );
}
