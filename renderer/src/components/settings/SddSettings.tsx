import { useEffect, useState } from "react";
import { BookOpen, ChevronDown, Plus, Trash2, Users, Workflow, type LucideIcon } from "lucide-react";
import { deleteWorkspaceAgent, listWorkspaceAgents, upsertWorkspaceAgent } from "../../lib/ipc/commands";
import { WorkspacePromptEditor } from "./WorkspacePromptEditor";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { confirmAction } from "../../state/confirmStore";
import { useLanguageStore, useT } from "../../state/languageStore";
import { renderMarkdown } from "../../lib/markdown";
import type { TranslationKey } from "../../lib/i18n/translations";
import type { WorkspaceAgent } from "../../types/domain";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import { Tabs, tabPanelProps } from "../common/Tabs";
import { Checkbox } from "../common/Checkbox";
import { Select } from "../common/Select";
import { Skeleton } from "../common/Skeleton";
import { AI_PROVIDERS } from "../../lib/aiProviders";
import { SDD_GUIDE_EN, SDD_GUIDE_ES } from "./sddGuide";

type TabId = "guide" | "agents" | "stages";

const TABS: { id: TabId; labelKey: TranslationKey; icon: LucideIcon }[] = [
  { id: "guide", labelKey: "settings.sddTabGuide", icon: BookOpen },
  { id: "agents", labelKey: "settings.sddTabAgents", icon: Users },
  { id: "stages", labelKey: "settings.sddTabStages", icon: Workflow },
];

/**
 * SDD / Harness workspace section. Everything is user-defined (no presets): a customizable roster
 * of agents (roles + models), the pipeline stages, and an editable best-practices guide. The guide
 * and stages piggyback on the per-workspace prompt store (kinds `sdd_guide` / `sdd_stages`).
 */
export function SddSettings() {
  const t = useT();
  const workspaceName = useWorkspaceStore((s) => {
    const id = s.activeWorkspaceId;
    return s.workspaces.find((w) => w.id === id)?.name ?? "";
  });
  const workspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);
  const [tab, setTab] = useState<TabId>("guide");

  if (!workspaceId) {
    return (
      <section>
        <h3 className="mb-1 text-title font-semibold">{t("settings.sdd")}</h3>
        <p className="text-relaxed text-[var(--cf-text-muted)]">{t("settings.sddSelectWorkspace")}</p>
      </section>
    );
  }

  return (
    <section>
      <h3 className="mb-3 text-title font-semibold">
        {workspaceName ? t("settings.sddTitleForProject", { name: workspaceName }) : t("settings.sdd")}
      </h3>

      {/* Automatic activation: all three panels are plain content and cost nothing to show. */}
      <Tabs
        options={TABS}
        activeId={tab}
        onSelect={setTab}
        layoutId="cf-sdd-tab"
        label={t("settings.sdd")}
        className="mb-4 flex-wrap border-b border-[var(--cf-border)]"
      />

      <div {...tabPanelProps("cf-sdd-tab", tab)}>
      {tab === "guide" && <GuideTab />}
      {tab === "agents" && <AgentsTab workspaceId={workspaceId} />}
      {tab === "stages" && (
        <WorkspacePromptEditor
          kind="sdd_stages"
          hintKey="settings.sddStagesHint"
          placeholderKey="settings.sddStagesPlaceholder"
          resetConfirmKey="settings.sddStagesResetConfirm"
          rows={8}
        />
      )}
      </div>
    </section>
  );
}

/** A static, read-only manual (a wiki) explaining SDD + harness and how to configure this section.
 * Picked by the app's language; not editable and not stored per workspace. */
function GuideTab() {
  const language = useLanguageStore((s) => s.language);
  const content = language === "es" ? SDD_GUIDE_ES : SDD_GUIDE_EN;
  return (
    <div
      className="cf-markdown-preview max-h-[460px] overflow-auto rounded-lg border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] px-4 py-3 text-body"
      dangerouslySetInnerHTML={{ __html: renderMarkdown(content) }}
    />
  );
}

/** The user's SDD/Harness agent roster — empty by default. Collapsible rows (name/model summary),
 * each expanding to edit role, model and an optional prompt. */
function AgentsTab({ workspaceId }: { workspaceId: string }) {
  const t = useT();
  const [agents, setAgents] = useState<WorkspaceAgent[] | null>(null);
  const [expandedId, setExpandedId] = useState<string | null>(null);

  const reload = async () => setAgents(await listWorkspaceAgents(workspaceId));

  useEffect(() => {
    setAgents(null);
    void reload();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workspaceId]);

  const add = async () => {
    const created = await upsertWorkspaceAgent(undefined, workspaceId, t("settings.sddNewAgent"), "", "", "", "", true);
    await reload();
    setExpandedId(created.id);
  };

  const update = async (agent: WorkspaceAgent, patch: Partial<WorkspaceAgent>) => {
    const next = { ...agent, ...patch };
    setAgents((prev) => (prev ? prev.map((a) => (a.id === agent.id ? next : a)) : prev));
    await upsertWorkspaceAgent(agent.id, workspaceId, next.name, next.role, next.provider, next.model, next.prompt, next.enabled);
  };

  const remove = async (agent: WorkspaceAgent) => {
    if (!(await confirmAction(t("settings.sddRemoveAgentConfirm", { name: agent.name || t("settings.sddNewAgent") })))) return;
    await deleteWorkspaceAgent(agent.id);
    await reload();
  };

  if (agents === null) return <Skeleton className="h-24 w-full" />;

  return (
    <div>
      <div className="mb-2 flex items-center justify-between">
        <p className="text-relaxed text-[var(--cf-text-muted)]">{t("settings.sddAgentsHint")}</p>
        <Button variant="ghost" size="sm" icon={Plus} className="shrink-0" onClick={() => void add()}>
          {t("settings.sddAddAgent")}
        </Button>
      </div>

      <div className="space-y-2">
        {agents.map((agent) => {
          const isOpen = expandedId === agent.id;
          return (
            <div key={agent.id} className="rounded-lg border border-[var(--cf-border)]">
              <div className="flex items-center gap-2 p-2.5">
                <button type="button" onClick={() => setExpandedId(isOpen ? null : agent.id)} className="flex min-w-0 flex-1 items-center gap-2 text-left">
                  <ChevronDown size={14} className={`shrink-0 text-[var(--cf-text-muted)] transition-transform ${isOpen ? "" : "-rotate-90"}`} />
                  <span className={`truncate text-body font-medium ${agent.enabled ? "" : "text-[var(--cf-text-muted)]"}`}>
                    {agent.name || t("settings.sddNewAgent")}
                  </span>
                  {agent.model && !isOpen && <span className="truncate font-mono text-badge text-[var(--cf-text-muted)]">{agent.model}</span>}
                </button>
                <label className="flex shrink-0 items-center gap-1.5 text-body text-[var(--cf-text-muted)]">
                  <Checkbox checked={agent.enabled} onChange={(checked) => void update(agent, { enabled: checked })} />
                  {t("settings.enabled")}
                </label>
                <IconButton
                  label="settings.removeAgent"
                  icon={Trash2}
                  variant="danger"
                  className="shrink-0"
                  onClick={() => void remove(agent)}
                />
              </div>

              {isOpen && (
                <div className="space-y-1.5 border-t border-[var(--cf-border)] p-3">
                  <input
                    value={agent.name}
                    aria-label={t("settings.sddAgentNamePlaceholder")}
                    onChange={(e) => void update(agent, { name: e.target.value })}
                    placeholder={t("settings.sddAgentNamePlaceholder")}
                    className="w-full rounded-md border border-[var(--cf-border)] bg-transparent px-2 py-1 text-body font-medium outline-none focus:border-[var(--cf-accent)]"
                  />
                  <div className="flex gap-1.5">
                    <div className="w-44 shrink-0">
                      <Select
                        size="sm"
                        value={agent.provider}
                        onChange={(v) => void update(agent, { provider: v })}
                        options={[
                          { value: "", label: t("settings.sddAgentProviderDefault") },
                          ...AI_PROVIDERS.filter((p) => p.available).map((p) => ({
                            value: p.id,
                            label: p.label ?? (p.labelKey ? t(p.labelKey) : p.id),
                          })),
                        ]}
                      />
                    </div>
                    <input
                      value={agent.model}
                      aria-label={t("settings.sddAgentModelPlaceholder")}
                      onChange={(e) => void update(agent, { model: e.target.value })}
                      placeholder={t("settings.sddAgentModelPlaceholder")}
                      className="flex-1 rounded-md border border-[var(--cf-border)] bg-transparent px-2 py-1 font-mono text-body outline-none focus:border-[var(--cf-accent)]"
                    />
                  </div>
                  <input
                    value={agent.role}
                    aria-label={t("settings.sddAgentRolePlaceholder")}
                    onChange={(e) => void update(agent, { role: e.target.value })}
                    placeholder={t("settings.sddAgentRolePlaceholder")}
                    className="w-full rounded-md border border-[var(--cf-border)] bg-transparent px-2 py-1 text-body outline-none focus:border-[var(--cf-accent)]"
                  />
                  <textarea
                    value={agent.prompt}
                    aria-label={t("settings.sddAgentPromptPlaceholder")}
                    onChange={(e) => void update(agent, { prompt: e.target.value })}
                    rows={5}
                    placeholder={t("settings.sddAgentPromptPlaceholder")}
                    className="w-full resize-y rounded-md border border-[var(--cf-border)] bg-transparent px-2 py-1 font-mono text-body leading-relaxed outline-none focus:border-[var(--cf-accent)]"
                  />
                </div>
              )}
            </div>
          );
        })}
        {agents.length === 0 && <p className="text-body text-[var(--cf-text-muted)]">{t("settings.sddNoAgents")}</p>}
      </div>
    </div>
  );
}
