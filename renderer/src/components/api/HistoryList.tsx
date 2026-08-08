import { useMemo } from "react";
import { History, Trash2 } from "lucide-react";
import { EmptyState } from "../common/EmptyState";
import { IconButton } from "../common/IconButton";
import { Tooltip } from "../common/Tooltip";
import { MethodBadge } from "./CollectionTree";
import { useApiTabsStore } from "../../state/apiTabsStore";
import { useApiHistoryStore } from "../../state/apiHistoryStore";
import { useApiRuntimeStore } from "../../state/apiRuntimeStore";
import { confirmAction } from "../../state/confirmStore";
import { useLanguageStore, useT } from "../../state/languageStore";
import type { ApiHistoryEntry, ApiRequestSpec, ApiResponse } from "../../types/api";

/** What `api_history.snapshot` holds — enough to put the request *and* what came back on screen. */
interface HistorySnapshot {
  request: ApiRequestSpec;
  response: ApiResponse | null;
}

function parseSnapshot(raw: string): HistorySnapshot | null {
  try {
    const parsed = JSON.parse(raw) as Partial<HistorySnapshot>;
    return parsed.request ? { request: parsed.request, response: parsed.response ?? null } : null;
  } catch {
    return null;
  }
}

function statusColor(status: number | null): string {
  if (status === null) return "var(--cf-danger)";
  if (status < 300) return "var(--cf-success)";
  if (status < 400) return "var(--cf-warning)";
  return "var(--cf-danger)";
}

function formatDuration(ms: number | null): string {
  if (ms === null) return "";
  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(2)} s`;
}

/** Newest first, split into runs of one calendar day. An unparseable timestamp keeps its entry
 * rather than dropping it — it just lands in a group of its own, labelled with the raw value. */
function groupByDay(entries: ApiHistoryEntry[]): { key: string; when: Date | null; items: ApiHistoryEntry[] }[] {
  const groups: { key: string; when: Date | null; items: ApiHistoryEntry[] }[] = [];
  for (const entry of entries) {
    const parsed = new Date(entry.created_at);
    const valid = !Number.isNaN(parsed.getTime());
    const key = valid ? parsed.toDateString() : entry.created_at;
    const last = groups[groups.length - 1];
    if (last?.key === key) last.items.push(entry);
    else groups.push({ key, when: valid ? parsed : null, items: [entry] });
  }
  return groups;
}

export function HistoryList() {
  const t = useT();
  const locale = useLanguageStore((s) => (s.language === "es" ? "es-ES" : "en-US"));
  const history = useApiHistoryStore((s) => s.history);
  const deleteHistory = useApiHistoryStore((s) => s.deleteHistory);
  const clearHistory = useApiHistoryStore((s) => s.clearHistory);

  const groups = useMemo(() => groupByDay(history), [history]);

  const dayLabel = (key: string, when: Date | null): string => {
    if (!when) return key;
    const today = new Date();
    const yesterday = new Date(today);
    yesterday.setDate(today.getDate() - 1);
    if (when.toDateString() === today.toDateString()) return t("api.history.today");
    if (when.toDateString() === yesterday.toDateString()) return t("api.history.yesterday");
    return when.toLocaleDateString(locale, { day: "numeric", month: "long", year: "numeric" });
  };

  /**
   * Re-opens an entry as a scratch tab. It deliberately does *not* re-point at the saved request
   * the send came from: the history row is a record of what was sent then, and reopening it must
   * not become a way to overwrite what that request says now.
   */
  const restore = (entry: ApiHistoryEntry) => {
    const snapshot = parseSnapshot(entry.snapshot);
    const state = useApiTabsStore.getState();
    const tabId = state.openScratchTab(entry.protocol);
    if (snapshot) state.updateDraft(tabId, snapshot.request);
    state.renameTab(tabId, entry.name || entry.url);
    if (snapshot?.response) useApiRuntimeStore.getState().setResponse(tabId, snapshot.response);
  };

  const clearAll = async () => {
    if (!(await confirmAction(t("api.history.clearConfirm"), true, t("api.settings.clearHistory")))) return;
    await clearHistory();
  };

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex shrink-0 items-center gap-1 border-b border-[var(--cf-border)] px-2 py-1">
        <span className="mr-auto truncate text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
          {t("api.history")}
        </span>
        {/* `Trash2` earns its place here: history rows are stored, and clearing them is a delete. */}
        <IconButton
          label="api.settings.clearHistory"
          icon={Trash2}
          variant="danger"
          disabled={history.length === 0}
          onClick={() => void clearAll()}
        />
      </div>

      {history.length === 0 ? (
        <EmptyState icon={History} title={t("api.noHistory")} />
      ) : (
        <div className="min-h-0 flex-1 overflow-auto pb-1">
          {groups.map((group) => (
            <div key={group.key}>
              <div className="sticky top-0 z-10 bg-[var(--cf-surface)] px-2 py-1 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
                {dayLabel(group.key, group.when)}
              </div>
              {group.items.map((entry) => (
                /* One line, not two: the bubble does not keep newlines, and the name plus the URL
                   reads the same with a dash between them. */
                <Tooltip key={entry.id} label={entry.name ? `${entry.name} — ${entry.url}` : entry.url}>
                <div
                  onClick={() => restore(entry)}
                  className="group flex cursor-pointer items-center gap-1.5 rounded-md px-1.5 py-1 text-ui hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
                >
                  <MethodBadge protocol={entry.protocol} method={entry.method} />
                  <span className="min-w-0 flex-1 truncate text-[var(--cf-text)]">{entry.url}</span>
                  <span
                    className="shrink-0 font-mono text-badge font-bold"
                    style={{ color: statusColor(entry.status) }}
                  >
                    {entry.status ?? "ERR"}
                  </span>
                  <span className="w-12 shrink-0 truncate text-right font-mono text-badge text-[var(--cf-text-muted)]">
                    {formatDuration(entry.duration_ms)}
                  </span>
                  <IconButton
                    label="api.delete"
                    icon={Trash2}
                    variant="danger"
                    className="shrink-0 opacity-55 group-hover:opacity-100 group-focus-within:opacity-100"
                    onClick={(e: React.MouseEvent) => {
                      e.stopPropagation();
                      void deleteHistory(entry.id);
                    }}
                  />
                </div>
                </Tooltip>
              ))}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
