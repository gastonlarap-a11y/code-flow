import { type ReactNode } from "react";
import { Select } from "../common/Select";
import { FIELD_INPUT } from "./Field";
import { useT } from "../../state/languageStore";
import { PROVIDER_MODELS, type AiModelOption } from "../../lib/aiProviders";

/** Sentinel choice meaning "the id is in the free-text field", not a listed model. */
export const CUSTOM_MODEL = "__custom__";

/** Splits a stored model id into the dropdown choice + custom-text buffer: an id present in
 * `knownIds` selects its option, a blank means "default", and anything else is a custom id the
 * user typed. `knownIds` is the effective option set (the CLI's live list when available, else the
 * curated fallback), so a model that really exists doesn't get mislabelled "custom". */
export function parseModel(
  raw: string | null | undefined,
  knownIds: string[],
): { choice: string; custom: string } {
  const trimmed = (raw ?? "").trim();
  if (!trimmed) return { choice: "", custom: "" };
  if (knownIds.includes(trimmed)) return { choice: trimmed, custom: "" };
  return { choice: CUSTOM_MODEL, custom: trimmed };
}

/** Providers whose model list is maintained by hand — their CLI can't enumerate models, so the
 * curated catalog is the source of truth and must always be shown (with any hand-typed ids merged
 * in), never replaced by a "live" list that is really just the ids the user happened to pick
 * before. Without this, selecting one Claude model collapsed the dropdown to only that one. */
const CURATED_MODEL_PROVIDERS = new Set(["claude"]);

/** The option set to show for a provider: the fetched models when we have them, else the curated
 * fallback list. Fetched ids (e.g. `opencode/claude-sonnet-5`) are shown verbatim — they're already
 * the exact string the CLI expects. `undefined` means the list hasn't arrived yet, which is not an
 * error: the caller renders the fallback until it does. For curated providers the catalog is always
 * kept, with any extra (hand-typed) ids appended. */
export function modelOptionsFor(providerId: string, dynamicModels?: string[]): AiModelOption[] {
  const curated = PROVIDER_MODELS[providerId] ?? [];
  if (CURATED_MODEL_PROVIDERS.has(providerId)) {
    const known = new Set(curated.map((o) => o.id));
    const extra = (dynamicModels ?? []).filter((id) => !known.has(id)).map((id) => ({ id, label: id }));
    return [...curated, ...extra];
  }
  if (dynamicModels && dynamicModels.length > 0) return dynamicModels.map((id) => ({ id, label: id }));
  return curated;
}

/** Where to find a valid model ID for the "Custom" field, per provider — a listing command for the
 * CLIs that have one, or how the provider names its models. */
export function customModelPlaceholder(providerId: string, fallback: string): string {
  switch (providerId) {
    case "gemini":
      return "e.g. gemini-3.6-flash-high";
    case "opencode":
      return "e.g. anthropic/claude-opus-5";
    case "claude":
      return "e.g. opus — or claude-opus-5";
    case "ollama":
      return "e.g. qwen2.5-coder";
    default:
      return fallback;
  }
}

/** A model picker: default option → option list → custom id. Fully controlled; the parent owns
 * the choice + custom-text buffer (so provider switches reload cleanly) and the `options` list
 * (the CLI's live models when available, else the curated fallback). */
export function ModelField({
  options,
  choice,
  custom,
  defaultLabel,
  customHint,
  customPlaceholder,
  size,
  onChoice,
  onCustom,
}: {
  options: AiModelOption[];
  choice: string;
  custom: string;
  defaultLabel: string;
  /** Per-provider "where to find the ID" note, shown under the custom input. */
  customHint?: ReactNode;
  customPlaceholder?: string;
  size?: "sm" | "md";
  onChoice: (v: string) => void;
  onCustom: (v: string) => void;
}) {
  const t = useT();
  return (
    <>
      <Select
        value={choice}
        size={size}
        onChange={onChoice}
        options={[
          { value: "", label: defaultLabel },
          ...options.map((opt) => ({ value: opt.id, label: opt.labelKey ? t(opt.labelKey) : (opt.label ?? opt.id) })),
          { value: CUSTOM_MODEL, label: t("settings.modelCustom") },
        ]}
      />
      {choice === CUSTOM_MODEL && (
        <>
          <input
            value={custom}
            onChange={(e) => onCustom(e.target.value)}
            placeholder={customPlaceholder ?? "model ID"}
            className={`${FIELD_INPUT} mt-1.5 font-mono`}
          />
          {customHint && <p className="mt-1 text-body leading-snug text-[var(--cf-text-muted)]">{customHint}</p>}
        </>
      )}
    </>
  );
}
