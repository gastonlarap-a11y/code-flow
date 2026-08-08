import { useEffect, useRef, useState } from "react";
import { RotateCcw } from "lucide-react";
import { Button } from "../common/Button";
import { defaultWorkspacePrompt, getWorkspacePrompt, setWorkspacePrompt } from "../../lib/ipc/commands";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { confirmAction } from "../../state/confirmStore";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";
import { Skeleton } from "../common/Skeleton";

/**
 * A per-workspace, provider-independent prompt override (the review standard or the PR-description
 * template), seeded with the built-in default and editable here. Autosaves on blur like the rest
 * of Settings; "restore default" blanks the override so the backend falls back to the built-in
 * text. Reused across tabs by passing a different `kind` + label keys — the same text applies to
 * whatever engine each task routes to, so it works with every model, not just one.
 */
export function WorkspacePromptEditor({
  kind,
  hintKey,
  placeholderKey,
  resetConfirmKey,
  rows = 22,
}: {
  kind: string;
  hintKey: TranslationKey;
  placeholderKey: TranslationKey;
  resetConfirmKey: TranslationKey;
  rows?: number;
}) {
  const t = useT();
  const workspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);

  const [value, setValue] = useState<string | null>(null);
  const [fallback, setFallback] = useState("");
  const [savedFlash, setSavedFlash] = useState(false);
  // Mirrors `value` for the unmount flush — reading state in cleanup would capture a stale render.
  const latest = useRef<string | null>(null);
  const persisted = useRef<string>("");

  useEffect(() => {
    let cancelled = false;
    if (!workspaceId) {
      setValue(null);
      return;
    }
    setValue(null);
    void (async () => {
      const [content, def] = await Promise.all([
        getWorkspacePrompt(workspaceId, kind).catch(() => null),
        defaultWorkspacePrompt(kind).catch(() => ""),
      ]);
      if (cancelled) return;
      const resolved = content ?? def;
      setFallback(def);
      setValue(resolved);
      latest.current = resolved;
      persisted.current = resolved;
    })();
    return () => {
      cancelled = true;
    };
  }, [workspaceId, kind]);

  // Closing Settings right after typing wouldn't always fire a blur — flush anything unsaved.
  useEffect(
    () => () => {
      if (!workspaceId) return;
      const current = latest.current;
      if (current !== null && current.trim() !== persisted.current.trim()) {
        void setWorkspacePrompt(workspaceId, kind, current.trim());
      }
    },
    [workspaceId, kind],
  );

  if (!workspaceId) {
    return <p className="text-relaxed text-[var(--cf-text-muted)]">{t("settings.reviewSelectWorkspace")}</p>;
  }

  const update = (next: string) => {
    setValue(next);
    latest.current = next;
  };

  const persist = async () => {
    const current = latest.current;
    if (current === null || current.trim() === persisted.current.trim()) return;
    await setWorkspacePrompt(workspaceId, kind, current.trim());
    persisted.current = current.trim();
    setSavedFlash(true);
    setTimeout(() => setSavedFlash(false), 1400);
  };

  const reset = async () => {
    if (!(await confirmAction(t(resetConfirmKey)))) return;
    update(fallback);
    await setWorkspacePrompt(workspaceId, kind, "");
    persisted.current = fallback.trim();
  };

  if (value === null) return <Skeleton className="h-64 w-full" />;

  const isCustom = value.trim() !== fallback.trim();

  return (
    <div>
      <div className="mb-2 flex items-center justify-between gap-2">
        <p className="text-relaxed text-[var(--cf-text-muted)]">{t(hintKey)}</p>
        {savedFlash ? (
          <span className="shrink-0 text-badge font-medium text-[var(--cf-success)]">{t("settings.saved")}</span>
        ) : (
          <span
            className={`shrink-0 rounded-full px-2 py-0.5 text-badge font-medium ${
              isCustom
                ? "bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]"
                : "bg-black/[0.05] text-[var(--cf-text-muted)] dark:bg-white/[0.08]"
            }`}
          >
            {isCustom ? t("settings.templateCustom") : t("settings.templateDefault")}
          </span>
        )}
      </div>

      <textarea
        aria-label={t(hintKey)}
        value={value}
        onChange={(e) => update(e.target.value)}
        onBlur={() => void persist()}
        rows={rows}
        spellCheck={false}
        placeholder={t(placeholderKey)}
        className="w-full resize-y rounded-md border border-[var(--cf-border)] bg-transparent px-2.5 py-1.5 font-mono text-body leading-relaxed outline-none focus:border-[var(--cf-accent)]"
      />
      <div className="mt-1.5 flex items-center justify-between">
        <span className="text-badge text-[var(--cf-text-muted)]">{t("settings.templateAutosave")}</span>
        {isCustom && (
          <Button variant="ghost" size="sm" icon={RotateCcw} onClick={() => void reset()}>
            {t("settings.templateReset")}
          </Button>
        )}
      </div>
    </div>
  );
}
