import { useEffect, useMemo, useState } from "react";
import { open as openDialog } from "../../lib/bridge/dialog";
import {
  Ban,
  ChevronDown,
  CircleCheckBig,
  CircleDot,
  Database,
  Download,
  Eye,
  EyeOff,
  MessageSquare,
  Trash2,
  type LucideIcon,
} from "lucide-react";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import { Tooltip } from "../common/Tooltip";
import {
  deleteReviewRun,
  deleteReviewRunsForPr,
  exportReviewRuns,
  getReviewRun,
  listReviewRuns,
  markReviewFinding,
  purgeWorkspaceReviewRuns,
} from "../../lib/ipc/commands";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { confirmAction } from "../../state/confirmStore";
import { useToastStore } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import { renderMarkdown } from "../../lib/markdown";
import type { TranslationKey } from "../../lib/i18n/translations";
import type { ReviewRunDetail, ReviewRunSummary, SavedFinding } from "../../types/domain";
import { Skeleton } from "../common/Skeleton";
import { EmptyState } from "../common/EmptyState";
import { CopyAnswer } from "../common/CopyAnswer";

/** Lifecycle state of a saved finding, as an icon rather than an emoji so it matches the icon
 * language of the rest of the app (and renders at a predictable size across platforms). */
const ESTADOS: Record<string, { icon: LucideIcon; color: string; labelKey: TranslationKey }> = {
  abierto: { icon: CircleDot, color: "text-[var(--cf-warning)]", labelKey: "settings.memoryEstadoOpen" },
  posteado: { icon: MessageSquare, color: "text-[var(--cf-accent)]", labelKey: "settings.memoryEstadoPosted" },
  resuelto: { icon: CircleCheckBig, color: "text-[var(--cf-success)]", labelKey: "settings.memoryEstadoResolved" },
  falso_positivo: { icon: Ban, color: "text-[var(--cf-text-muted)]", labelKey: "settings.memoryEstadoFalse" },
  ignorado: { icon: EyeOff, color: "text-[var(--cf-text-muted)]", labelKey: "settings.memoryEstadoIgnored" },
};

/** Shared styling for the two header actions (export / purge) and the per-PR delete. */

/** One PR's group of saved runs (newest first). */
interface PrGroup {
  projectId: string;
  projectName: string;
  prId: number;
  prTitle: string;
  runs: ReviewRunSummary[];
}

/**
 * Manager for the workspace's saved review memory (the `review_runs` table). Lists runs grouped by
 * PR, lets you open one to read its saved review, delete a run or a whole PR's history, purge the
 * whole workspace, or export runs to disk as .md/.json.
 */
export function ReviewMemoriesSettings() {
  const t = useT();
  const workspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);
  const pushToast = useToastStore((s) => s.pushToast);

  const [runs, setRuns] = useState<ReviewRunSummary[] | null>(null);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [detail, setDetail] = useState<ReviewRunDetail | null>(null);

  const reload = async (id: string) => setRuns(await listReviewRuns(id));

  useEffect(() => {
    setRuns(null);
    setExpandedId(null);
    setDetail(null);
    if (workspaceId) void reload(workspaceId);
  }, [workspaceId]);

  const groups = useMemo<PrGroup[]>(() => {
    if (!runs) return [];
    const byPr = new Map<string, PrGroup>();
    for (const run of runs) {
      const key = `${run.project_id}:${run.pr_id}`;
      const existing = byPr.get(key);
      if (existing) existing.runs.push(run);
      else
        byPr.set(key, {
          projectId: run.project_id,
          projectName: run.project_name,
          prId: run.pr_id,
          prTitle: run.pr_title,
          runs: [run],
        });
    }
    return [...byPr.values()];
  }, [runs]);

  if (!workspaceId) {
    return <p className="text-relaxed text-[var(--cf-text-muted)]">{t("settings.reviewSelectWorkspace")}</p>;
  }
  if (runs === null) {
    return (
      <div className="space-y-2">
        <Skeleton className="h-8 w-full" />
        <Skeleton className="h-16 w-full" />
      </div>
    );
  }

  const toggle = async (run: ReviewRunSummary) => {
    if (expandedId === run.id) {
      setExpandedId(null);
      setDetail(null);
      return;
    }
    setExpandedId(run.id);
    setDetail(null);
    setDetail(await getReviewRun(run.id));
  };

  const refreshDetail = async (id: string) => setDetail(await getReviewRun(id));

  const removeRun = async (run: ReviewRunSummary) => {
    if (!(await confirmAction(t("settings.memoryDeleteRunConfirm", { pr: run.pr_id })))) return;
    await deleteReviewRun(run.id);
    if (expandedId === run.id) setExpandedId(null);
    await reload(workspaceId);
  };

  const removePr = async (group: PrGroup) => {
    if (!(await confirmAction(t("settings.memoryDeletePrConfirm", { pr: group.prId, n: group.runs.length }), true))) return;
    await deleteReviewRunsForPr(group.projectId, group.prId);
    await reload(workspaceId);
  };

  const purge = async () => {
    if (!(await confirmAction(t("settings.memoryPurgeConfirm", { n: runs.length }), true))) return;
    await purgeWorkspaceReviewRuns(workspaceId);
    await reload(workspaceId);
  };

  const exportRuns = async (id?: string) => {
    const dir = await openDialog({ directory: true, multiple: false, title: t("settings.memoryExportTitle") });
    if (typeof dir !== "string") return;
    try {
      const n = await exportReviewRuns(workspaceId, id, dir);
      pushToast(t("settings.memoryExported", { n }), "success");
    } catch (e) {
      pushToast(String(e), "error");
    }
  };

  if (runs.length === 0) {
    return <EmptyState icon={Database} title={t("settings.memoryEmpty")} subtitle={t("settings.memoryEmptyHint")} />;
  }

  return (
    <div className="space-y-3">
      {/* Actions are real bordered buttons pinned to the top of the row: as bare icon+label pairs
          centred against a two-line paragraph they wrapped ("Exportar / todo") and read as loose
          floating icons rather than controls. */}
      <div className="flex items-start justify-between gap-3">
        <p className="min-w-0 text-body leading-relaxed text-[var(--cf-text-muted)]">
          {t("settings.memoryHint", { n: runs.length })}
        </p>
        <div className="flex shrink-0 items-center gap-1.5">
          <Button variant="secondary" size="sm" icon={Download} onClick={() => void exportRuns()}>
            {t("settings.memoryExportAll")}
          </Button>
          {/* Purging every saved review is this screen's destructive action, so it says so in
              text rather than in a tooltip (§II.6 row 6). */}
          <Button variant="danger" size="sm" icon={Trash2} onClick={() => void purge()}>
            {t("settings.memoryPurge")}
          </Button>
        </div>
      </div>

      {groups.map((group) => (
        <div key={`${group.projectId}:${group.prId}`} className="overflow-hidden rounded-lg border border-[var(--cf-border)]">
          {/* Project name moved onto its own muted line: inline after the title it collided with the
              PR subject, and both got clipped by the same truncate. */}
          {/* A literal tint rather than --cf-surface-raised: that var equals --cf-surface in the light
              theme, so the header band would only be visible in dark mode. */}
          <div className="flex items-start justify-between gap-2 border-b border-[var(--cf-border)] bg-black/[0.02] px-3 py-2 dark:bg-white/[0.03]">
            <div className="min-w-0">
              <p className="truncate text-body font-medium">
                <span className="text-[var(--cf-text-muted)]">#{group.prId}</span> {group.prTitle || group.projectName}
              </p>
              <p className="mt-0.5 truncate text-badge text-[var(--cf-text-muted)]">
                {group.projectName} · {t("settings.memoryRunsCount", { n: group.runs.length })}
              </p>
            </div>
            <IconButton
              label="settings.memoryDeletePr"
              icon={Trash2}
              variant="danger"
              className="-mr-1 shrink-0"
              onClick={() => void removePr(group)}
            />
          </div>
          <div className="divide-y divide-[var(--cf-border)]">
            {group.runs.map((run) => (
              <div key={run.id}>
                <div className="flex items-center gap-2 px-3 py-2 text-body">
                  {/* Minutes, not seconds — the exact second is noise next to the level and counts. */}
                  <span className="shrink-0 tabular-nums text-[var(--cf-text-muted)]">
                    {run.created_at.slice(0, 16).replace("T", " ")}
                  </span>
                  <span className="shrink-0 rounded bg-black/[0.05] px-1.5 py-0.5 text-badge capitalize text-[var(--cf-text-muted)] dark:bg-white/[0.08]">
                    {run.level}
                  </span>
                  <span className="min-w-0 truncate text-badge text-[var(--cf-text-muted)]">
                    {t("settings.memoryRunMeta", { iter: run.iter, n: run.findings_count })}
                  </span>
                  <div className="ml-auto flex shrink-0 items-center gap-0.5">
                    <IconButton
                      label="settings.memoryView"
                      icon={expandedId === run.id ? ChevronDown : Eye}
                      onClick={() => void toggle(run)}
                    />
                    <IconButton
                      label="settings.memoryExportOne"
                      icon={Download}
                      onClick={() => void exportRuns(run.id)}
                    />
                    <IconButton
                      label="settings.memoryDeleteRun"
                      icon={Trash2}
                      variant="danger"
                      onClick={() => void removeRun(run)}
                    />
                  </div>
                </div>
                {expandedId === run.id && (
                  <div className="space-y-3 border-t border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-3">
                    {detail === null ? (
                      <Skeleton className="h-24 w-full" />
                    ) : (
                      <>
                        <RunFindings detail={detail} onMarked={() => void refreshDetail(detail.id)} />
                        <div
                          className="cf-markdown-preview max-h-80 overflow-auto border-t border-[var(--cf-border)] pt-3 text-body"
                          dangerouslySetInnerHTML={{ __html: renderMarkdown(detail.review_md) }}
                        />
                        {/* A saved review is the same answer read later, so it ends the same way.
                            Export writes a file; this is for the far commoner case of pasting it
                            into a ticket or a chat. */}
                        <CopyAnswer text={detail.review_md} />
                      </>
                    )}
                  </div>
                )}
              </div>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

/** The saved findings of one run, each with its lifecycle state and mark actions (false-positive /
 * ignored / clear). Marking updates the run's stored findings and carries forward on re-review. */
function RunFindings({ detail, onMarked }: { detail: ReviewRunDetail; onMarked: () => void }) {
  const t = useT();
  const findings = useMemo<SavedFinding[]>(() => {
    try {
      return JSON.parse(detail.findings) as SavedFinding[];
    } catch {
      return [];
    }
  }, [detail.findings]);

  if (findings.length === 0) {
    return <p className="text-badge text-[var(--cf-text-muted)]">{t("settings.memoryNoFindings")}</p>;
  }

  const mark = async (f: SavedFinding, estado: string) => {
    await markReviewFinding(detail.id, f.id, estado);
    onMarked();
  };

  return (
    <div className="space-y-0.5">
      {findings.map((f) => {
        const discarded = f.estado === "falso_positivo" || f.estado === "ignorado";
        const estado = ESTADOS[f.estado];
        const EstadoIcon = estado?.icon;
        return (
          // Discarded findings are dimmed so the state icon isn't the only cue that they're out.
          <div key={f.id} className={`flex items-center gap-2 text-badge ${discarded ? "opacity-55" : ""}`}>
            <Tooltip label={estado ? t(estado.labelKey) : f.estado}>
              <span className={`shrink-0 ${estado?.color ?? ""}`}>
                {EstadoIcon ? <EstadoIcon size={12} /> : "•"}
              </span>
            </Tooltip>
            <span className="shrink-0 font-mono text-[var(--cf-text-muted)]">{f.id}</span>
            <span className="min-w-0 truncate">
              {f.categoria}
              {f.archivo ? <span className="text-[var(--cf-text-muted)]"> · {f.archivo}</span> : null}
            </span>
            <div className="ml-auto flex shrink-0 items-center gap-0.5">
              {discarded ? (
                <Button variant="ghost" size="sm" onClick={() => void mark(f, "abierto")}>{t("settings.memoryUnmark")}</Button>
              ) : (
                f.estado !== "resuelto" && (
                  <>
                    <Button variant="ghost" size="sm" onClick={() => void mark(f, "ignorado")}>{t("settings.memoryMarkIgnored")}</Button>
                    <Button variant="ghost" size="sm" onClick={() => void mark(f, "falso_positivo")}>{t("settings.memoryMarkFalse")}</Button>
                  </>
                )
              )}
            </div>
          </div>
        );
      })}
    </div>
  );
}
