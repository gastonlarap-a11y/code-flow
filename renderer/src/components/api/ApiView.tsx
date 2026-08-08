import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Cookie,
  Download,
  Eye,
  Play,
  Plus,
  Settings,
  Settings2,
  Zap,
  type LucideIcon,
} from "lucide-react";
import { RequestTabs } from "./RequestTabs";
import { RequestBuilder } from "./RequestBuilder";
import { CodeSnippetPanel } from "./CodeSnippetPanel";
import { EnvironmentModal } from "./EnvironmentModal";
import { ImportModal } from "./ImportModal";
import { ExportModal } from "./ExportModal";
import { RunnerModal } from "./RunnerModal";
import { ApiSettingsModal } from "./ApiSettingsModal";
import { CookieModal } from "./CookieModal";
import { tabActions } from "./tabActions";
import { CARD } from "../common/panelChrome";
import { EmptyState } from "../common/EmptyState";
import { Select } from "../common/Select";
import { Button } from "../common/Button";
import { Tooltip } from "../common/Tooltip";
import { IconButton } from "../common/IconButton";
import { ensureApiStoreLoaded, getVariableContext } from "../../state/apiStore";
import { useApiEnvironmentStore } from "../../state/apiEnvironmentStore";
import { useApiTabsStore } from "../../state/apiTabsStore";
import { useApiTreeStore } from "../../state/apiTreeStore";
import { useApiModalStore } from "../../state/apiModalStore";
import { useUiStore } from "../../state/uiStore";
import { useToastStore } from "../../state/toastStore";
import { confirmAction } from "../../state/confirmStore";
import { useT } from "../../state/languageStore";
import { lookupVariable } from "../../lib/api/variables";
import type { VariableContext } from "../../lib/api/variables";
import { VARIABLE_SCOPE_ORDER } from "../../types/api";
import type { VariableScope } from "../../types/api";
import type { TranslationKey } from "../../lib/i18n/translations";

/**
 * The API client's shell: toolbar, sidebar, tab strip, request builder, response pane and the
 * code-snippet panel. Every panel below reads `apiStore`/`apiRuntimeStore` on its own — this file
 * only decides what is on screen, owns the modals, and binds the three keyboard shortcuts.
 */

const NO_ENVIRONMENT = "";

// ---------------------------------------------------------------------------
// Variable quick look
// ---------------------------------------------------------------------------

const SCOPE_LABEL: Record<VariableScope, TranslationKey> = {
  local: "api.scope.local",
  data: "api.scope.data",
  environment: "api.scope.environment",
  collection: "api.scope.collection",
  global: "api.scope.global",
};

interface QuickLookRow {
  key: string;
  value: string;
  secret: boolean;
  /** A lower-precedence scope also defines this name, so this row is not what a send would use. */
  shadowed: boolean;
}

function rowsForScope(scope: VariableScope, ctx: VariableContext): QuickLookRow[] {
  const shadowed = (key: string) => lookupVariable(key, ctx)?.scope !== scope;
  if (scope === "local" || scope === "data") {
    return Object.entries(ctx[scope]).map(([key, value]) => ({
      key,
      value,
      secret: false,
      shadowed: shadowed(key),
    }));
  }
  return ctx[scope]
    .filter((variable) => variable.enabled && variable.key.trim() !== "")
    .map((variable) => ({
      key: variable.key,
      value: variable.currentValue !== "" ? variable.currentValue : variable.initialValue,
      secret: variable.secret,
      shadowed: shadowed(variable.key),
    }));
}

/**
 * Postman's eye icon: every variable currently in scope, in the precedence order a send resolves
 * them, so "why is `{{baseUrl}}` still the staging host" is one click to answer.
 */
function VariableQuickLook({ collectionId }: { collectionId: string | null }) {
  const t = useT();
  const openModal = useApiModalStore((s) => s.openApiModal);
  const collections = useApiTreeStore((s) => s.collections);
  const environments = useApiEnvironmentStore((s) => s.environments);
  const activeEnvironmentId = useApiEnvironmentStore((s) => s.activeEnvironmentId);
  const [open, setOpen] = useState(false);
  const wrapperRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!wrapperRef.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", onDown);
    return () => document.removeEventListener("mousedown", onDown);
  }, [open]);

  // `getVariableContext()` returns a fresh object per call, so it can never be a selector — it
  // is rebuilt only when one of the things it reads changes.
  const context = useMemo(
    () => getVariableContext(collectionId),
    [collectionId, collections, environments, activeEnvironmentId],
  );

  const sections = VARIABLE_SCOPE_ORDER.map((scope) => ({ scope, rows: rowsForScope(scope, context) }))
    .filter((section) => section.rows.length > 0);

  return (
    <div ref={wrapperRef} className="relative shrink-0">
      <IconButton
        label="api.env.quickLook"
        icon={Eye}
        active={open}
        onClick={() => setOpen((v) => !v)}
        className="shrink-0"
      />
      {open && (
        <div className="absolute left-0 top-full z-50 mt-1 flex max-h-[420px] w-[340px] flex-col overflow-hidden rounded-md border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] shadow-[var(--cf-shadow)]">
          <div className="flex shrink-0 items-center gap-1.5 border-b border-[var(--cf-border)] px-2.5 py-1.5">
            <span className="flex-1 truncate text-ui font-medium text-[var(--cf-text)]">
              {t("api.env.quickLook")}
            </span>
            <IconButton
              label="api.env.manage"
              icon={Settings2}
              onClick={() => {
                setOpen(false);
                openModal({ kind: "environments" });
              }}
            />
          </div>

          <div className="min-h-0 flex-1 overflow-auto p-1.5">
            {sections.length === 0 ? (
              <p className="px-1.5 py-3 text-center text-ui text-[var(--cf-text-muted)]">
                {t("api.env.noVariables")}
              </p>
            ) : (
              sections.map(({ scope, rows }) => (
                <div key={scope} className="mb-2 last:mb-0">
                  <p className="px-1.5 pb-0.5 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
                    {t(SCOPE_LABEL[scope])}
                  </p>
                  {rows.map((row) => (
                    <Tooltip
                      key={`${scope}:${row.key}`}
                      label={t("api.env.shadowed")}
                      disabled={!row.shadowed}
                    >
                      <div
                        className={`flex items-baseline gap-2 rounded px-1.5 py-0.5 ${
                          row.shadowed ? "opacity-45 line-through" : ""
                        }`}
                      >
                        <span className="min-w-0 max-w-[45%] shrink-0 truncate font-mono text-badge text-[var(--cf-accent)]">
                          {row.key}
                        </span>
                        <span className="min-w-0 flex-1 truncate text-right font-mono text-badge text-[var(--cf-text)]">
                          {row.secret ? "••••••••" : row.value}
                        </span>
                      </div>
                    </Tooltip>
                  ))}
                </div>
              ))
            )}
          </div>
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Empty state
// ---------------------------------------------------------------------------

function ApiEmptyState() {
  const t = useT();
  const openModal = useApiModalStore((s) => s.openApiModal);
  const collections = useApiTreeStore((s) => s.collections);
  const pushToast = useToastStore((s) => s.pushToast);

  const newCollection = async () => {
    const created = await useApiTreeStore.getState().createCollection(t("api.untitledCollection"));
    if (created) pushToast(t("api.toast.collectionCreated", { name: created.name }), "success");
  };

  const action = (label: string, icon: LucideIcon, onClick: () => void, primary = false) => (
    <Button variant={primary ? "primary" : "secondary"} icon={icon} onClick={onClick}>
      {label}
    </Button>
  );

  return (
    <div className="flex h-full min-h-0 flex-col items-center justify-center gap-3">
      {/* `EmptyState` is `h-full`, so it needs a box with a resolved height to centre itself in;
          without one it would either collapse or eat the row the buttons live in. */}
      <div className="h-[150px] w-full">
        {/* The subtitle says "this workspace" rather than just "no collections": an empty API view
            straight after a workspace switch otherwise reads as "my collections are gone". */}
        <EmptyState
          icon={Zap}
          title={t("api.title")}
          subtitle={collections.length === 0 ? t("api.noCollectionsInWorkspace") : undefined}
        />
      </div>
      <div className="flex flex-wrap items-center justify-center gap-2">
        {action(t("api.newRequest"), Plus, () => useApiTabsStore.getState().openScratchTab(), true)}
        {action(t("api.newCollection"), Plus, () => void newCollection())}
        {action(t("api.import.title"), Download, () => openModal({ kind: "import" }))}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------

export function ApiView() {
  const t = useT();
  const collections = useApiTreeStore((s) => s.collections);
  const environments = useApiEnvironmentStore((s) => s.environments);
  const activeEnvironmentId = useApiEnvironmentStore((s) => s.activeEnvironmentId);
  const setActiveEnvironment = useApiEnvironmentStore((s) => s.setActiveEnvironment);
  const openTabs = useApiTabsStore((s) => s.openTabs);
  const activeTabId = useApiTabsStore((s) => s.activeTabId);
  const activeView = useUiStore((s) => s.activeView);
  const modal = useApiModalStore((s) => s.modal);
  const openModal = useApiModalStore((s) => s.openApiModal);
  const closeModal = useApiModalStore((s) => s.closeApiModal);

  useEffect(() => {
    void ensureApiStoreLoaded();
  }, []);

  const activeTab = openTabs.find((tab) => tab.id === activeTabId) ?? null;

  const closeActiveTab = useCallback(async () => {
    const store = useApiTabsStore.getState();
    const tab = store.openTabs.find((candidate) => candidate.id === store.activeTabId);
    if (!tab) return;
    if (tab.dirty) {
      const name = tab.name || t("api.untitledRequest");
      if (!(await confirmAction(t("editor.closeDirtyConfirm", { name })))) return;
    }
    useApiTabsStore.getState().closeTab(tab.id);
  }, [t]);

  // Scoped to the view: it stays mounted once opened, so an unscoped ⌘S would save an API request
  // while the user is looking at the diff of a commit.
  useEffect(() => {
    if (activeView !== "api") return;
    const handler = (e: KeyboardEvent) => {
      if (e.defaultPrevented || !(e.metaKey || e.ctrlKey) || e.altKey) return;
      // A modal covers the builder, so ⌘W there would close a tab the user can't even see.
      if (useApiModalStore.getState().modal !== null) return;
      const tabId = useApiTabsStore.getState().activeTabId;
      if (!tabId) return;
      if (e.key === "s") {
        e.preventDefault();
        tabActions(tabId)?.save();
      } else if (e.key === "Enter") {
        e.preventDefault();
        tabActions(tabId)?.send();
      } else if (e.key === "w") {
        e.preventDefault();
        void closeActiveTab();
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [activeView, closeActiveTab]);

  const environmentOptions = useMemo(
    () => [
      { value: NO_ENVIRONMENT, label: t("api.env.noEnvironment") },
      ...environments
        .filter((environment) => !environment.is_global)
        .map((environment) => ({ value: environment.id, label: environment.name })),
    ],
    [environments, t],
  );

  // The runner always runs *something*: whatever collection the open request belongs to, or the
  // first one, so the toolbar button doesn't need a picker of its own.
  const runnerCollectionId = activeTab?.collectionId ?? collections[0]?.id ?? null;

  return (
    <>
      {/* No background of its own any more: the view is a set of islands over the app's ambient
          canvas, and an opaque box here would paint it out for the whole width of the module. */}
      <div className="flex h-full min-h-0 flex-col gap-1.5 overflow-hidden">
        <div className={`flex shrink-0 items-center gap-1 px-2 py-1 ${CARD}`}>
          <div className="w-[220px] shrink-0">
            <Select
              size="sm"
              value={activeEnvironmentId ?? NO_ENVIRONMENT}
              onChange={(value) => setActiveEnvironment(value === NO_ENVIRONMENT ? null : value)}
              options={environmentOptions}
              ariaLabel={t("api.env.select")}
            />
          </div>
          <VariableQuickLook collectionId={activeTab?.collectionId ?? null} />

          <div className="flex-1" />

          <IconButton
            label="api.runner.title"
            icon={Play}
            disabled={runnerCollectionId === null}
            onClick={() =>
              runnerCollectionId &&
              openModal({ kind: "runner", collectionId: runnerCollectionId, folderId: null })
            }
          />
          <IconButton
            label="api.import.title"
            icon={Download}
            onClick={() => openModal({ kind: "import" })}
          />
          <IconButton
            label="api.cookies"
            icon={Cookie}
            onClick={() => openModal({ kind: "cookies" })}
          />
          <IconButton
            label="api.settings.title"
            icon={Settings}
            onClick={() => openModal({ kind: "settings" })}
          />
        </div>

        {/* Gapped so each column reads as its own card. The collections sidebar used to be the
            first of them; it is the API module's entry in the context panel now, one column
            further left, which is where every other module keeps its tree. */}
        <div className="flex min-h-0 flex-1 gap-1.5 overflow-hidden">
          <div className={`flex min-w-0 flex-1 flex-col overflow-hidden ${CARD}`}>
            {openTabs.length > 0 && <RequestTabs />}
            <div className="min-h-0 flex-1">
              {activeTab ? <RequestBuilder tabId={activeTab.id} /> : <ApiEmptyState />}
            </div>
          </div>

          {/* The snippet mirrors one request, so it has nothing to show without an open tab. */}
          {activeTab && <CodeSnippetPanel tabId={activeTab.id} />}
        </div>
      </div>

      {modal?.kind === "environments" && <EnvironmentModal onClose={closeModal} />}
      {modal?.kind === "import" && <ImportModal onClose={closeModal} />}
      {modal?.kind === "cookies" && <CookieModal onClose={closeModal} />}
      {modal?.kind === "settings" && <ApiSettingsModal onClose={closeModal} />}
      {modal?.kind === "export" && (
        <ExportModal collectionId={modal.collectionId} onClose={closeModal} />
      )}
      {modal?.kind === "runner" && (
        <RunnerModal collectionId={modal.collectionId} folderId={modal.folderId} onClose={closeModal} />
      )}
    </>
  );
}
