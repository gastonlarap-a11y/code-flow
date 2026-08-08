import { useCallback, useEffect, useRef, useState } from "react";
import { Check, Copy, Download, Globe, Layers, Plus, RotateCcw, Save, Trash2, Wand2 } from "lucide-react";
import { Button } from "../common/Button";
import { Checkbox } from "../common/Checkbox";
import { IconButton } from "../common/IconButton";
import { RowActions, type RowAction } from "../common/RowActions";
import { Select } from "../common/Select";
import { Tabs, tabPanelProps, type TabOption } from "../common/Tabs";
import { Tooltip } from "../common/Tooltip";
import { ApiModal, Field } from "./ApiModal";
import { RevealToggle } from "./RevealToggle";
import { useApiEnvironmentStore } from "../../state/apiEnvironmentStore";
import { confirmAction } from "../../state/confirmStore";
import { pushErrorToast, useToastStore } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import { exportEnvironment } from "../../lib/api/exporters";
import { DYNAMIC_VARIABLES } from "../../lib/api/variables";
import { apiSaveFile } from "../../lib/ipc/apiCommands";
import type { ApiEnvironment, ApiVariable } from "../../types/api";

/** How long an edit sits in the draft before it reaches SQLite. */
const COMMIT_DEBOUNCE_MS = 400;

/** The last column carries the reveal toggle, which says "Show value" in words rather than in a
 *  glyph — so it is sized for that text plus the delete button, not for two 12px icons. */
const GRID = "24px minmax(0,1fr) 96px minmax(0,1.3fr) minmax(0,1.3fr) minmax(0,1fr) 158px";

const TABS: readonly TabOption<"variables" | "dynamic">[] = [
  { id: "variables", labelKey: "api.tab.variables" },
  { id: "dynamic", labelKey: "api.env.dynamicVariables" },
];

function parseVariables(json: string | undefined): ApiVariable[] {
  if (!json) return [];
  try {
    const parsed: unknown = JSON.parse(json);
    return Array.isArray(parsed) ? (parsed as ApiVariable[]) : [];
  } catch {
    return [];
  }
}

function newVariableId(): string {
  return `var-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

/** What a variable is actually worth right now — the same rule `variables.ts` resolves by. */
function effectiveValue(variable: ApiVariable): string {
  return variable.currentValue !== "" ? variable.currentValue : variable.initialValue;
}

/** Globals first, then the rest in their stored order. */
function ordered(environments: ApiEnvironment[]): ApiEnvironment[] {
  return [...environments].sort((a, b) => {
    if (a.is_global !== b.is_global) return a.is_global ? -1 : 1;
    return a.sort_order - b.sort_order;
  });
}

export function EnvironmentModal({ onClose }: { onClose: () => void }) {
  const t = useT();
  const environments = useApiEnvironmentStore((s) => s.environments);
  const createEnvironment = useApiEnvironmentStore((s) => s.createEnvironment);
  const duplicateEnvironment = useApiEnvironmentStore((s) => s.duplicateEnvironment);
  const deleteEnvironment = useApiEnvironmentStore((s) => s.deleteEnvironment);
  const pushToast = useToastStore((s) => s.pushToast);

  const [tab, setTab] = useState<"variables" | "dynamic">("variables");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [rows, setRows] = useState<ApiVariable[]>([]);
  const [revealed, setRevealed] = useState<Set<string>>(new Set());
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState("");
  const [copied, setCopied] = useState<string | null>(null);

  const list = ordered(environments);
  const selected = list.find((e) => e.id === selectedId) ?? null;

  /**
   * Variable edits arrive per keystroke but each one rewrites the environment's whole JSON blob
   * through an IPC call, so the table is edited as a local draft and written on a trailing timer.
   * Everything that can lose the draft — switching environments, closing, unmounting — flushes it
   * first rather than hoping the timer wins the race.
   */
  const pendingRef = useRef<{ id: string; rows: ApiVariable[] } | null>(null);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const flush = useCallback(() => {
    if (timerRef.current !== null) {
      clearTimeout(timerRef.current);
      timerRef.current = null;
    }
    const pending = pendingRef.current;
    pendingRef.current = null;
    if (!pending) return;
    const store = useApiEnvironmentStore.getState();
    const environment = store.environments.find((e) => e.id === pending.id);
    if (!environment) return;
    void store.updateEnvironment({ ...environment, variables: JSON.stringify(pending.rows) });
  }, []);

  useEffect(() => flush, [flush]);

  const commit = useCallback(
    (next: ApiVariable[]) => {
      if (!selectedId) return;
      setRows(next);
      pendingRef.current = { id: selectedId, rows: next };
      if (timerRef.current !== null) clearTimeout(timerRef.current);
      timerRef.current = setTimeout(flush, COMMIT_DEBOUNCE_MS);
    },
    [flush, selectedId],
  );

  const select = useCallback(
    (id: string) => {
      if (id === selectedId) return;
      flush();
      setSelectedId(id);
      setRows(parseVariables(useApiEnvironmentStore.getState().environments.find((e) => e.id === id)?.variables));
      setRevealed(new Set());
    },
    [flush, selectedId],
  );

  // Picks the initial selection, and recovers if the selected environment is deleted underneath us.
  useEffect(() => {
    if (selectedId !== null && environments.some((e) => e.id === selectedId)) return;
    const fallback = ordered(environments).find((e) => e.is_global) ?? ordered(environments)[0];
    setSelectedId(fallback?.id ?? null);
    setRows(parseVariables(fallback?.variables));
    setRevealed(new Set());
  }, [environments, selectedId]);

  const updateRow = (id: string, patch: Partial<ApiVariable>) =>
    commit(rows.map((row) => (row.id === id ? { ...row, ...patch } : row)));

  const addRow = () =>
    commit([
      ...rows,
      {
        id: newVariableId(),
        key: "",
        initialValue: "",
        currentValue: "",
        secret: false,
        enabled: true,
        description: "",
      },
    ]);

  const toggleReveal = (id: string) =>
    setRevealed((previous) => {
      const next = new Set(previous);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const createNew = async () => {
    flush();
    const created = await createEnvironment(t("api.env.new"));
    if (!created) return;
    setSelectedId(created.id);
    setRows([]);
    setRenamingId(created.id);
    setRenameValue(created.name);
  };

  const commitRename = (environment: ApiEnvironment) => {
    const name = renameValue.trim();
    setRenamingId(null);
    if (!name || name === environment.name) return;
    void useApiEnvironmentStore.getState().updateEnvironment({ ...environment, name });
  };

  const remove = async (environment: ApiEnvironment) => {
    if (!(await confirmAction(t("api.env.deleteConfirm", { name: environment.name })))) return;
    await deleteEnvironment(environment.id);
  };

  /** Secrets never leave the app in a file that exists to be shared; the toggle lives in the
   * collection export, where the user is picking a destination on purpose. */
  const exportOne = async (environment: ApiEnvironment) => {
    flush();
    try {
      const json = exportEnvironment(environment, { includeSecrets: false });
      const path = await apiSaveFile(`${environment.name || "environment"}.postman_environment.json`, json);
      if (path) pushToast(t("api.export.done", { path }), "success");
    } catch (e) {
      pushErrorToast(t("api.toast.exportFailed", { error: String(e) }));
    }
  };

  const resetToInitial = () => commit(rows.map((row) => ({ ...row, currentValue: "" })));

  const persistCurrent = () =>
    commit(rows.map((row) => ({ ...row, initialValue: effectiveValue(row) })));

  const environmentActions = (environment: ApiEnvironment): RowAction[] => [
    {
      id: "export",
      labelKey: "api.export.environment",
      icon: Download,
      onSelect: () => void exportOne(environment),
    },
    ...(environment.is_global
      ? []
      : [
          {
            id: "duplicate",
            labelKey: "api.duplicate" as const,
            icon: Copy,
            onSelect: () => void duplicateEnvironment(environment.id),
          },
          {
            id: "delete",
            labelKey: "api.delete" as const,
            icon: Trash2,
            danger: true,
            onSelect: () => void remove(environment),
          },
        ]),
  ];

  const copyToken = (name: string) => {
    void navigator.clipboard.writeText(`{{${name}}}`);
    setCopied(name);
    window.setTimeout(() => setCopied((current) => (current === name ? null : current)), 1200);
  };

  return (
    <ApiModal
      icon={Layers}
      title={t("api.env.manage")}
      size="3xl"
      fill
      onClose={onClose}
    >
      <div className="flex min-h-0 flex-1">
        {/* Environment list */}
        <div className="flex w-[200px] shrink-0 flex-col border-r border-[var(--cf-border)]">
          <div className="flex shrink-0 items-center gap-1 border-b border-[var(--cf-border)] px-2 py-1.5">
            <span className="mr-auto text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
              {t("api.environments")}
            </span>
            <IconButton label="api.env.new" icon={Plus} onClick={() => void createNew()} />
          </div>

          <div className="min-h-0 flex-1 overflow-auto p-1">
            {list.length === 0 && (
              <p className="p-3 text-ui text-[var(--cf-text-muted)]">{t("api.env.noEnvironments")}</p>
            )}
            {list.map((environment) => {
              const active = environment.id === selectedId;
              return (
                <div
                  key={environment.id}
                  onClick={() => select(environment.id)}
                  onDoubleClick={() => {
                    if (environment.is_global) return;
                    setRenamingId(environment.id);
                    setRenameValue(environment.name);
                  }}
                  className={`group flex cursor-pointer items-center gap-1.5 rounded-md px-2 py-1.5 text-ui ${
                    active
                      ? "bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]"
                      : "text-[var(--cf-text)] hover:bg-black/[0.04] dark:hover:bg-white/[0.06]"
                  }`}
                >
                  <Globe size={12} className="shrink-0 opacity-70" />
                  {renamingId === environment.id ? (
                    <input
                      autoFocus
                      value={renameValue}
                      onChange={(e) => setRenameValue(e.target.value)}
                      onBlur={() => commitRename(environment)}
                      onKeyDown={(e) => {
                        if (e.key === "Enter") commitRename(environment);
                        if (e.key === "Escape") setRenamingId(null);
                      }}
                      onClick={(e) => e.stopPropagation()}
                      className="min-w-0 flex-1 rounded border border-[var(--cf-accent)] bg-[var(--cf-surface)] px-1 py-0.5 text-ui outline-none"
                    />
                  ) : (
                    <span className="min-w-0 flex-1 truncate">
                      {environment.is_global ? t("api.env.globals") : environment.name}
                    </span>
                  )}
                  {/* Three actions on a row is what `RowActions` is for; the globals environment
                      cannot be duplicated or deleted, so those two are absent rather than disabled. */}
                  <RowActions className="shrink-0" actions={environmentActions(environment)} />
                </div>
              );
            })}
          </div>
        </div>

        {/* Detail */}
        <div className="flex min-w-0 flex-1 flex-col">
          <div className="flex shrink-0 items-center gap-1 border-b border-[var(--cf-border)] px-2 py-1">
            {/* Automatic activation: both panels are already built and cost nothing to show, which
                is the APG's own condition for letting selection follow focus. */}
            <Tabs
              options={TABS}
              activeId={tab}
              onSelect={setTab}
              layoutId="cf-env-tab"
              label={t("api.env.manage")}
            />

            {tab === "variables" && selected && (
              <div className="ml-auto flex items-center gap-1">
                <Button variant="ghost" size="sm" icon={RotateCcw} onClick={resetToInitial}>
                  {t("api.env.reset")}
                </Button>
                <Button variant="ghost" size="sm" icon={Save} onClick={persistCurrent}>
                  {t("api.env.persist")}
                </Button>
              </div>
            )}
          </div>

          {tab === "dynamic" ? (
            <div {...tabPanelProps("cf-env-tab", tab)} className="min-h-0 flex-1 overflow-auto p-3">
              <p className="mb-2 flex items-center gap-1.5 text-badge text-[var(--cf-text-muted)]">
                <Wand2 size={12} />
                {t("api.env.dynamicHint")}
              </p>
              <div className="overflow-hidden rounded-md border border-[var(--cf-border)]">
                {DYNAMIC_VARIABLES.map((variable, index) => (
                  <Tooltip key={variable.name} label={t("api.snippet.copy")}>
                  <button
                    onClick={() => copyToken(variable.name)}
                    className={`flex w-full items-center gap-3 px-2.5 py-1.5 text-left hover:bg-black/[0.04] dark:hover:bg-white/[0.06] ${
                      index === 0 ? "" : "border-t border-[var(--cf-border)]"
                    }`}
                  >
                    <span className="w-[190px] shrink-0 truncate font-mono text-ui text-[var(--cf-accent)]">
                      {`{{${variable.name}}}`}
                    </span>
                    <span className="min-w-0 flex-1 truncate text-ui text-[var(--cf-text)]">
                      {variable.description}
                    </span>
                    <Tooltip label={variable.example}>
                      <span className="w-[220px] shrink-0 truncate font-mono text-badge text-[var(--cf-text-muted)]">
                        {variable.example}
                      </span>
                    </Tooltip>
                    <span className="w-[64px] shrink-0 text-right text-badge text-[var(--cf-text-muted)]">
                      {copied === variable.name ? (
                        <span className="inline-flex items-center gap-1 text-[var(--cf-success)]">
                          <Check size={11} />
                          {t("api.snippet.copied")}
                        </span>
                      ) : (
                        <Copy size={11} className="ml-auto inline" />
                      )}
                    </span>
                  </button>
                  </Tooltip>
                ))}
              </div>
            </div>
          ) : !selected ? (
            <div
              {...tabPanelProps("cf-env-tab", tab)}
              className="flex flex-1 items-center justify-center p-6 text-ui text-[var(--cf-text-muted)]"
            >
              {t("api.env.noEnvironments")}
            </div>
          ) : (
            <div {...tabPanelProps("cf-env-tab", tab)} className="min-h-0 flex-1 overflow-auto p-3">
              {selected.is_global && (
                <p className="mb-2 text-badge text-[var(--cf-text-muted)]">{t("api.env.globalsHint")}</p>
              )}

              <div
                className="grid items-center gap-2 border-b border-[var(--cf-border)] pb-1 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]"
                style={{ gridTemplateColumns: GRID }}
              >
                <span />
                <span>{t("api.env.variable")}</span>
                <span>{t("api.env.type")}</span>
                <span>{t("api.env.initialValue")}</span>
                <span>{t("api.env.currentValue")}</span>
                <span>{t("api.description")}</span>
                <span />
              </div>

              {rows.length === 0 && (
                <p className="py-4 text-ui text-[var(--cf-text-muted)]">{t("api.env.noVariables")}</p>
              )}

              {rows.map((row) => {
                const masked = row.secret && !revealed.has(row.id);
                return (
                  <div
                    key={row.id}
                    className="grid items-center gap-2 border-b border-[var(--cf-border)] py-1"
                    style={{ gridTemplateColumns: GRID }}
                  >
                    <Checkbox
                      checked={row.enabled}
                      onChange={(enabled) => updateRow(row.id, { enabled })}
                    />
                    <Field
                      mono
                      value={row.key}
                      placeholder={t("api.key")}
                      ariaLabel={t("api.env.variable")}
                      onChange={(key) => updateRow(row.id, { key })}
                    />
                    <Select
                      size="sm"
                      value={row.secret ? "secret" : "default"}
                      onChange={(type) => updateRow(row.id, { secret: type === "secret" })}
                      options={[
                        { value: "default", label: t("api.env.default") },
                        { value: "secret", label: t("api.env.secret") },
                      ]}
                      ariaLabel={t("api.env.type")}
                    />
                    <Field
                      mono
                      type={masked ? "password" : "text"}
                      value={row.initialValue}
                      placeholder={t("api.env.initialValue")}
                      ariaLabel={t("api.env.initialValue")}
                      onChange={(initialValue) => updateRow(row.id, { initialValue })}
                    />
                    <Field
                      mono
                      type={masked ? "password" : "text"}
                      value={row.currentValue}
                      placeholder={row.initialValue || t("api.env.currentValue")}
                      ariaLabel={t("api.env.currentValue")}
                      onChange={(currentValue) => updateRow(row.id, { currentValue })}
                    />
                    <Field
                      value={row.description}
                      placeholder={t("api.description")}
                      ariaLabel={t("api.description")}
                      onChange={(description) => updateRow(row.id, { description })}
                    />
                    <span className="flex items-center justify-end gap-0.5">
                      {row.secret && (
                        <RevealToggle revealed={!masked} onToggle={() => toggleReveal(row.id)} />
                      )}
                      {/* `Trash2`, not `X`: this removes a variable, and `X` is reserved for
                          dismissing a surface (icon dictionary, §II.3). */}
                      <IconButton
                        label="api.removeRow"
                        icon={Trash2}
                        variant="danger"
                        onClick={() => commit(rows.filter((r) => r.id !== row.id))}
                      />
                    </span>
                  </div>
                );
              })}

              <Button variant="ghost" size="sm" icon={Plus} className="mt-2" onClick={addRow}>
                {t("api.env.addVariable")}
              </Button>
            </div>
          )}
        </div>
      </div>
    </ApiModal>
  );
}
