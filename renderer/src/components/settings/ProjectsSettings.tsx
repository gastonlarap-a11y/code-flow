import { useEffect, useState } from "react";
import { Briefcase, Check, ChevronDown, ChevronRight, Pencil, Plus, Trash2 } from "lucide-react";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import { Tooltip } from "../common/Tooltip";
import { Field, FIELD_INPUT } from "./Field";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { ColorSwatchPicker } from "../common/ColorSwatchPicker";
import { pushErrorToast, useToastStore } from "../../state/toastStore";
import { confirmAction } from "../../state/confirmStore";
import { useT } from "../../state/languageStore";

/**
 * The inline name field, for the one thing a workspace could never do: be renamed.
 *
 * Same contract as every other inline edit in the app — Enter commits, Escape abandons, blur
 * abandons — so a half-typed name that loses focus does not get written.
 */
function RenameField({
  initial,
  onSubmit,
  onCancel,
}: {
  initial: string;
  onSubmit: (name: string) => void;
  onCancel: () => void;
}) {
  const t = useT();
  const [value, setValue] = useState(initial);

  return (
    <input
      autoFocus
      value={value}
      onChange={(e) => setValue(e.target.value)}
      onKeyDown={(e) => {
        if (e.key === "Enter") {
          e.preventDefault();
          if (value.trim()) onSubmit(value);
        } else if (e.key === "Escape") {
          e.preventDefault();
          onCancel();
        }
      }}
      onBlur={onCancel}
      aria-label={t("sidebar.workspaceName")}
      className="w-full min-w-0 rounded-md border border-[var(--cf-accent)] bg-transparent px-1.5 py-0.5 text-body outline-none"
    />
  );
}

export function ProjectsSettings() {
  const t = useT();
  const workspaces = useWorkspaceStore((s) => s.workspaces);
  const projectsByWorkspace = useWorkspaceStore((s) => s.projectsByWorkspace);
  const loadProjects = useWorkspaceStore((s) => s.loadProjects);
  const removeProject = useWorkspaceStore((s) => s.removeProject);
  const removeWorkspace = useWorkspaceStore((s) => s.removeWorkspace);
  const addWorkspace = useWorkspaceStore((s) => s.addWorkspace);
  const setWorkspaceColor = useWorkspaceStore((s) => s.setWorkspaceColor);
  const setProjectColor = useWorkspaceStore((s) => s.setProjectColor);
  const renameWorkspace = useWorkspaceStore((s) => s.renameWorkspace);
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [newName, setNewName] = useState("");
  const [copiedPath, setCopiedPath] = useState<string | null>(null);

  // `projectsByWorkspace` is normally only populated for whichever workspace is/was active
  // (the sidebar only ever needs that one) — this overview lists every workspace's projects
  // at once, so it has to fetch the ones nobody's switched into yet itself.
  useEffect(() => {
    for (const ws of workspaces) {
      if (!projectsByWorkspace[ws.id]) void loadProjects(ws.id);
    }
  }, [workspaces, projectsByWorkspace, loadProjects]);
  // Collapsed by default — a workspace with dozens of repos would otherwise dump all of
  // them on screen the moment Settings opens. Membership means "expanded", so any workspace
  // not yet toggled (including newly added ones) starts collapsed.
  const [expandedIds, setExpandedIds] = useState<Set<string>>(new Set());
  const toggleExpanded = (id: string) =>
    setExpandedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const copyPath = async (path: string) => {
    await navigator.clipboard.writeText(path);
    setCopiedPath(path);
    useToastStore.getState().pushToast(t("settings.pathCopied"), "success");
    setTimeout(() => setCopiedPath((prev) => (prev === path ? null : prev)), 1500);
  };

  return (
    <section>
      <div className="mb-1 flex items-center justify-between">
        <h3 className="text-title font-semibold">{t("settings.projectsTitle")}</h3>
      </div>
      <p className="mb-3 text-relaxed text-[var(--cf-text-muted)]">{t("settings.projectsHint")}</p>
      <div className="space-y-4">
        {workspaces.map((ws) => {
          const projects = projectsByWorkspace[ws.id] ?? [];
          const isOnlyWorkspace = workspaces.length <= 1;
          const hasProjects = projects.length > 0;
          const disableRemoveWorkspace = isOnlyWorkspace || hasProjects;
          const removeWorkspaceTitle = isOnlyWorkspace
            ? t("settings.onlyWorkspace")
            : hasProjects
              ? t("settings.removeWorkspaceHasProjects")
              : t("settings.removeWorkspace");

          const expanded = expandedIds.has(ws.id);

          return (
            <div key={ws.id} className="rounded-lg border border-[var(--cf-border)] p-2.5">
              <div className={`flex items-center gap-2 text-body font-medium ${expanded ? "mb-2" : ""}`}>
                <button
                  onClick={() => toggleExpanded(ws.id)}
                  aria-expanded={expanded}
                  className="cf-focusable flex h-6 min-w-0 flex-1 items-center gap-2 text-left"
                >
                  {expanded ? (
                    <ChevronDown size={13} className="shrink-0 text-[var(--cf-text-muted)]" />
                  ) : (
                    <ChevronRight size={13} className="shrink-0 text-[var(--cf-text-muted)]" />
                  )}
                  <Briefcase size={13} style={{ color: ws.color }} className="shrink-0" />
                  {renamingId === ws.id ? (
                    // Stops the row's expand/collapse from firing underneath the field.
                    <span className="flex-1" onClick={(e) => e.stopPropagation()}>
                      <RenameField
                        initial={ws.name}
                        onCancel={() => setRenamingId(null)}
                        onSubmit={async (name) => {
                          try {
                            await renameWorkspace(ws.id, name);
                            setRenamingId(null);
                          } catch (e) {
                            pushErrorToast(t("toast.renameWorkspaceFailed", { error: String(e) }));
                          }
                        }}
                      />
                    </span>
                  ) : (
                    <span className="flex-1 truncate">{ws.name}</span>
                  )}
                  {!expanded && (
                    <span className="shrink-0 rounded-full bg-black/[0.05] px-1.5 py-0.5 text-badge font-normal text-[var(--cf-text-muted)] dark:bg-white/[0.08]">
                      {projects.length}
                    </span>
                  )}
                </button>
                <div className="flex items-center gap-1.5">
                  {renamingId !== ws.id && (
                    <IconButton
                      label="settings.renameWorkspace"
                      icon={Pencil}
                      onClick={() => setRenamingId(ws.id)}
                    />
                  )}
                  <ColorSwatchPicker value={ws.color} onChange={(color) => setWorkspaceColor(ws.id, color)} />
                  {/* The tooltip carries the *reason* it is unavailable ("this workspace still has
                      projects"), which the label alone cannot say — and a disabled button fires no
                      pointer events, so `Button` anchors it on a wrapping span. */}
                  <Button
                    variant="danger"
                    size="sm"
                    icon={Trash2}
                    disabled={disableRemoveWorkspace}
                    tooltip={removeWorkspaceTitle}
                    onClick={async () => {
                      if (await confirmAction(t("settings.removeWorkspaceConfirm", { name: ws.name }))) {
                        void removeWorkspace(ws.id);
                      }
                    }}
                  >
                    {t("settings.removeWorkspace")}
                  </Button>
                </div>
              </div>

              {expanded && (
                <div className="space-y-1.5">
                  {projects.map((p) => (
                    <div key={p.id} className="rounded-md border border-[var(--cf-border)] px-2.5 py-1.5">
                      <div className="flex items-center gap-2 text-body">
                        <ColorSwatchPicker value={p.color} onChange={(color) => setProjectColor(p.id, ws.id, color)} />
                        <span className="flex-1 truncate font-medium">{p.name}</span>
                        <IconButton
                          label="settings.removeProject"
                          icon={Trash2}
                          variant="danger"
                          className="shrink-0"
                          onClick={async () => {
                            if (await confirmAction(t("settings.removeProjectConfirm", { name: p.name }))) {
                              void removeProject(p.id, ws.id);
                            }
                          }}
                        />
                      </div>
                      <Tooltip label={t("settings.copyPath")}>
                      <button
                        onClick={() => copyPath(p.local_path)}
                        className="mt-1.5 flex w-full min-w-0 items-center gap-1 truncate text-left text-badge text-[var(--cf-text-muted)] hover:text-[var(--cf-accent)]"
                      >
                        {copiedPath === p.local_path && <Check size={11} className="shrink-0 text-[var(--cf-success)]" />}
                        <span className="truncate">{copiedPath === p.local_path ? t("settings.pathCopied") : p.local_path}</span>
                      </button>
                      </Tooltip>
                    </div>
                  ))}
                  {projects.length === 0 && (
                    <p className="text-body text-[var(--cf-text-muted)]">{t("settings.noProjectsInWorkspace")}</p>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>

      <div className="mt-4 flex items-end gap-1.5 border-t border-[var(--cf-border)] pt-3">
        <Field label={t("settings.addWorkspace")}>
          {(field) => (
            <input
              {...field}
              value={newName}
              onChange={(e) => setNewName(e.target.value)}
              placeholder={t("settings.newWorkspaceNamePlaceholder")}
              className={FIELD_INPUT}
            />
          )}
        </Field>
        <Button
          variant="secondary"
          icon={Plus}
          className="self-end"
          disabled={!newName.trim()}
          onClick={async () => {
            await addWorkspace(newName.trim(), "briefcase", "#6366f1");
            setNewName("");
          }}
        >
          {t("settings.addWorkspace")}
        </Button>
      </div>
    </section>
  );
}
