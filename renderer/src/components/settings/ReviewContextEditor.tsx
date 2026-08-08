import { useEffect, useState } from "react";
import { Plus, Trash2 } from "lucide-react";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import { Tooltip } from "../common/Tooltip";
import { deleteReviewContext, listReviewContexts, upsertReviewContext } from "../../lib/ipc/commands";
import { useWorkspaceStore } from "../../state/workspaceStore";
import type { ReviewContext } from "../../types/domain";
import { confirmAction } from "../../state/confirmStore";
import { useT } from "../../state/languageStore";
import { Checkbox } from "../common/Checkbox";

/**
 * The workspace's PR review context — a list of named, toggleable text blocks fed to the reviewer
 * (any model) alongside the diff. This is the single home for what used to be split across
 * "Context" and "Instructions (.md)": both were the same thing (named blocks folded into the
 * prompt), so they're merged here. A block can be a one-line rule or a long document; nothing is
 * ever written as a file, and none of it is Claude-specific.
 *
 * Chips select a block; the selected block gets a full-height editor below (title + content +
 * enable + delete) — good for both short rules and long instructions.
 */
export function ReviewContextEditor() {
  const t = useT();
  const workspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);
  const [contexts, setContexts] = useState<ReviewContext[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const reload = async (id: string, keepSelection = true) => {
    const list = await listReviewContexts(id);
    setContexts(list);
    setSelectedId((prev) => (keepSelection && prev && list.some((c) => c.id === prev) ? prev : list[0]?.id ?? null));
  };

  useEffect(() => {
    if (workspaceId) void reload(workspaceId, false);
    else {
      setContexts([]);
      setSelectedId(null);
    }
  }, [workspaceId]);

  if (!workspaceId) {
    return <p className="text-relaxed text-[var(--cf-text-muted)]">{t("settings.contextSelectWorkspace")}</p>;
  }

  const addContext = async () => {
    const created = await upsertReviewContext(undefined, workspaceId, t("settings.newContextName"), "", true);
    setContexts((prev) => [...prev, created]);
    setSelectedId(created.id);
  };

  const update = async (ctx: ReviewContext, patch: Partial<ReviewContext>) => {
    const next = { ...ctx, ...patch };
    setContexts((prev) => prev.map((c) => (c.id === ctx.id ? next : c)));
    await upsertReviewContext(ctx.id, workspaceId, next.name, next.content, next.enabled);
  };

  const remove = async (ctx: ReviewContext) => {
    if (!(await confirmAction(t("settings.removeContextConfirm", { name: ctx.name || t("settings.untitledContext") })))) return;
    await deleteReviewContext(ctx.id);
    await reload(workspaceId, false);
  };

  const selected = contexts.find((c) => c.id === selectedId) ?? null;

  return (
    <div>
      <div className="mb-1 flex items-center justify-between">
        <p className="text-relaxed text-[var(--cf-text-muted)]">{t("settings.contextHint")}</p>
        <Button variant="ghost" size="sm" icon={Plus} className="shrink-0" onClick={addContext}>
          {t("settings.addContext")}
        </Button>
      </div>

      {contexts.length === 0 ? (
        <p className="mt-3 text-body text-[var(--cf-text-muted)]">{t("settings.noContexts")}</p>
      ) : (
        <div className="mt-3 space-y-3">
          <div className="flex flex-wrap gap-1.5">
            {contexts.map((c) => (
              <button
                key={c.id}
                onClick={() => setSelectedId(c.id)}
                className={`flex items-center gap-1.5 rounded-md px-2.5 py-1 text-body ${
                  c.id === selectedId
                    ? "bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]"
                    : "text-[var(--cf-text-muted)] hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
                }`}
              >
                {!c.enabled && (
                  <Tooltip label={t("settings.disabled")}>
                    <span className="h-1.5 w-1.5 rounded-full bg-[var(--cf-text-muted)]" />
                  </Tooltip>
                )}
                {c.name || t("settings.untitledContext")}
              </button>
            ))}
          </div>

          {selected && (
            <div className="rounded-lg border border-[var(--cf-border)] p-3">
              <div className="mb-2 flex items-center gap-2">
                <input
                  value={selected.name}
                  onChange={(e) => update(selected, { name: e.target.value })}
                  placeholder={t("settings.untitledContext")}
                  aria-label={t("settings.contextName")}
                  className="flex-1 rounded-md border border-transparent bg-transparent px-1 text-body font-medium outline-none focus:border-[var(--cf-accent)]"
                />
                <label className="flex items-center gap-1.5 text-body text-[var(--cf-text-muted)]">
                  <Checkbox checked={selected.enabled} onChange={(checked) => update(selected, { enabled: checked })} />
                  {t("settings.enabled")}
                </label>
                <IconButton
                  label="settings.removeContext"
                  icon={Trash2}
                  variant="danger"
                  onClick={() => remove(selected)}
                />
              </div>
              <textarea
                value={selected.content}
                onChange={(e) => update(selected, { content: e.target.value })}
                rows={14}
                placeholder={t("settings.contextPlaceholder")}
                aria-label={t("settings.contextContent")}
                className="w-full resize-y rounded-md border border-[var(--cf-border)] bg-transparent px-2.5 py-1.5 font-mono text-body leading-relaxed outline-none focus:border-[var(--cf-accent)]"
              />
            </div>
          )}
        </div>
      )}
    </div>
  );
}
