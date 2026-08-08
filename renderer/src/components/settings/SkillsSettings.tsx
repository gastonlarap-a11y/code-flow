import { useEffect, useState } from "react";
import { open as openDialog } from "../../lib/bridge/dialog";
import { ChevronDown, FolderInput, PackagePlus, Plus, Trash2 } from "lucide-react";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import {
  createCustomSkill,
  deleteSkillFile,
  importSkillFromFolder,
  installWorkspaceSkill,
  listSkillFiles,
  listWorkspaceSkills,
  readSkillFile,
  removeWorkspaceSkill,
  setWorkspaceSkillEnabled,
  writeSkillFile,
} from "../../lib/ipc/commands";
import { onSkillsProgress } from "../../lib/ipc/events";
import { pushErrorToast } from "../../state/toastStore";
import { useWorkspaceStore } from "../../state/workspaceStore";
import type { WorkspaceSkill } from "../../types/domain";
import { confirmAction } from "../../state/confirmStore";
import { useT } from "../../state/languageStore";
import { Checkbox } from "../common/Checkbox";
import { Skeleton } from "../common/Skeleton";

const DEFAULT_SKILL_MD = "---\nname: my-skill\ndescription: What this skill does and when to use it.\n---\n\n# My skill\n\nInstructions for the model…\n";

export function SkillsSettings() {
  const t = useT();
  const workspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);
  const [skills, setSkills] = useState<WorkspaceSkill[]>([]);
  const [repo, setRepo] = useState("");
  const [skillName, setSkillName] = useState("");
  const [installing, setInstalling] = useState(false);
  const [lines, setLines] = useState<string[]>([]);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState("");

  const reload = async (id: string) => setSkills(await listWorkspaceSkills(id));

  useEffect(() => {
    if (workspaceId) void reload(workspaceId);
    else setSkills([]);
  }, [workspaceId]);

  if (!workspaceId) {
    return (
      <section>
        <h3 className="mb-1 text-title font-semibold">{t("settings.skillsTitle")}</h3>
        <p className="text-relaxed text-[var(--cf-text-muted)]">{t("settings.skillsSelectWorkspace")}</p>
      </section>
    );
  }

  const install = async () => {
    if (!repo.trim() || !skillName.trim()) return;
    setInstalling(true);
    setLines([]);
    const unlisten = await onSkillsProgress((e) => setLines((prev) => [...prev.slice(-200), e.line]));
    try {
      await installWorkspaceSkill(workspaceId, repo.trim(), skillName.trim());
      setRepo("");
      setSkillName("");
      await reload(workspaceId);
    } catch (e) {
      pushErrorToast(String(e));
    } finally {
      setInstalling(false);
      void unlisten();
    }
  };

  const createSkill = async () => {
    if (!newName.trim()) return;
    try {
      const created = await createCustomSkill(workspaceId, newName.trim(), DEFAULT_SKILL_MD);
      setNewName("");
      setCreating(false);
      await reload(workspaceId);
      setExpandedId(created.id);
    } catch (e) {
      pushErrorToast(String(e));
    }
  };

  const importFolder = async () => {
    const dir = await openDialog({ directory: true, multiple: false, title: t("settings.skillImportTitle") });
    if (typeof dir !== "string") return;
    try {
      await importSkillFromFolder(workspaceId, dir);
      await reload(workspaceId);
    } catch (e) {
      pushErrorToast(String(e));
    }
  };

  const toggle = async (skill: WorkspaceSkill, enabled: boolean) => {
    setSkills((prev) => prev.map((s) => (s.id === skill.id ? { ...s, enabled } : s)));
    try {
      await setWorkspaceSkillEnabled(skill.id, enabled);
    } catch (e) {
      pushErrorToast(String(e));
      await reload(workspaceId);
    }
  };

  const remove = async (skill: WorkspaceSkill) => {
    if (!(await confirmAction(t("settings.removeSkillConfirm", { name: skill.skill_name })))) return;
    try {
      await removeWorkspaceSkill(skill.id);
      if (expandedId === skill.id) setExpandedId(null);
      await reload(workspaceId);
    } catch (e) {
      pushErrorToast(String(e));
    }
  };

  return (
    <section>
      <h3 className="mb-1 text-title font-semibold">{t("settings.skillsTitle")}</h3>
      <p className="mb-3 text-relaxed text-[var(--cf-text-muted)]">
        {t("settings.skillsHintPrefix")}{" "}
        <a href="https://www.skills.sh/" target="_blank" rel="noreferrer" className="text-[var(--cf-accent)] underline">
          skills.sh
        </a>{" "}
        {t("settings.skillsHintSuffix")} {t("settings.skillsOnlyClaude")}
      </p>

      <div className="mb-3 space-y-2 rounded-lg border border-[var(--cf-border)] p-3">
        <p className="text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
          {t("settings.skillFromRegistry")}
        </p>
        <div className="flex gap-1.5">
          <input
            value={repo}
            aria-label={t("settings.skillRepoPlaceholder")}
            onChange={(e) => setRepo(e.target.value)}
            disabled={installing}
            placeholder={t("settings.skillRepoPlaceholder")}
            className="flex-1 rounded-md border border-[var(--cf-border)] bg-transparent px-2.5 py-1.5 font-mono text-body outline-none focus:border-[var(--cf-accent)] disabled:opacity-50"
          />
          <input
            value={skillName}
            aria-label={t("settings.skillNamePlaceholder")}
            onChange={(e) => setSkillName(e.target.value)}
            disabled={installing}
            placeholder={t("settings.skillNamePlaceholder")}
            className="w-40 rounded-md border border-[var(--cf-border)] bg-transparent px-2.5 py-1.5 font-mono text-body outline-none focus:border-[var(--cf-accent)] disabled:opacity-50"
          />
          <Button
            variant="primary"
            icon={PackagePlus}
            pending={installing}
            disabled={!repo.trim() || !skillName.trim()}
            className="shrink-0"
            onClick={install}
          >
            {installing ? t("settings.installingSkill") : t("settings.installSkill")}
          </Button>
        </div>
        {lines.length > 0 && (
          <div className="max-h-28 overflow-auto rounded-md bg-black/[0.04] p-2 font-mono text-badge text-[var(--cf-text-muted)] dark:bg-white/[0.06]">
            {lines.map((line, i) => (
              <div key={i} className="whitespace-pre-wrap break-all">
                {line}
              </div>
            ))}
          </div>
        )}

        <div className="flex items-center gap-2 pt-1">
          {creating ? (
            <div className="flex flex-1 gap-1.5">
              <input
                value={newName}
                aria-label={t("settings.skillNamePlaceholder")}
                onChange={(e) => setNewName(e.target.value)}
                autoFocus
                placeholder={t("settings.skillNamePlaceholder")}
                onKeyDown={(e) => e.key === "Enter" && void createSkill()}
                className="flex-1 rounded-md border border-[var(--cf-border)] bg-transparent px-2.5 py-1.5 font-mono text-body outline-none focus:border-[var(--cf-accent)]"
              />
              <Button variant="primary" disabled={!newName.trim()} onClick={() => void createSkill()}>
                {t("common.create")}
              </Button>
              <Button variant="ghost" onClick={() => setCreating(false)}>
                {t("common.cancel")}
              </Button>
            </div>
          ) : (
            <>
              <Button variant="ghost" size="sm" icon={Plus} onClick={() => setCreating(true)}>
                {t("settings.skillCreateCustom")}
              </Button>
              <Button variant="ghost" size="sm" icon={FolderInput} onClick={() => void importFolder()}>
                {t("settings.skillImportFolder")}
              </Button>
            </>
          )}
        </div>
      </div>

      <div className="space-y-1">
        {skills.map((s) => (
          <div key={s.id} className="rounded-md border border-[var(--cf-border)]">
            <div className="flex items-center gap-2 px-2.5 py-1.5 text-body">
              <Checkbox checked={s.enabled} onChange={(enabled) => void toggle(s, enabled)} />
              <span className={`font-medium ${s.enabled ? "" : "text-[var(--cf-text-muted)] line-through"}`}>{s.skill_name}</span>
              <span className="rounded bg-black/[0.05] px-1.5 py-0.5 text-badge text-[var(--cf-text-muted)] dark:bg-white/[0.08]">
                {s.source_repo === "custom" ? t("settings.skillBadgeCustom") : s.source_repo === "local" ? t("settings.skillBadgeLocal") : s.source_repo}
              </span>
              <div className="ml-auto flex items-center gap-2">
                <Button
                  variant="ghost"
                  size="sm"
                  icon={ChevronDown}
                  onClick={() => setExpandedId((id) => (id === s.id ? null : s.id))}
                >
                  {t("settings.skillEdit")}
                </Button>
                <IconButton
                  label="settings.removeSkill"
                  icon={Trash2}
                  variant="danger"
                  onClick={() => void remove(s)}
                />
              </div>
            </div>
            {expandedId === s.id && (
              <div className="border-t border-[var(--cf-border)] p-2.5">
                <SkillFilesEditor workspaceId={workspaceId} skillName={s.skill_name} />
              </div>
            )}
          </div>
        ))}
        {skills.length === 0 && <p className="text-body text-[var(--cf-text-muted)]">{t("settings.noSkills")}</p>}
      </div>
    </section>
  );
}

/** In-app editor for every file inside a skill's folder — a file list plus a per-file editor
 * (save on blur), with add/delete. Path-safe on the backend (no traversal outside the skill). */
function SkillFilesEditor({ workspaceId, skillName }: { workspaceId: string; skillName: string }) {
  const t = useT();
  const [files, setFiles] = useState<string[] | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [content, setContent] = useState("");
  const [addingName, setAddingName] = useState("");
  const [savedFlash, setSavedFlash] = useState(false);

  const load = async (keep?: string) => {
    const list = await listSkillFiles(workspaceId, skillName);
    setFiles(list);
    const pick = keep && list.includes(keep) ? keep : list.find((f) => f === "SKILL.md") ?? list[0] ?? null;
    setSelected(pick);
    setContent(pick ? await readSkillFile(workspaceId, skillName, pick) : "");
  };

  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workspaceId, skillName]);

  const openFile = async (rel: string) => {
    setSelected(rel);
    setContent(await readSkillFile(workspaceId, skillName, rel));
  };

  const save = async () => {
    if (!selected) return;
    await writeSkillFile(workspaceId, skillName, selected, content);
    setSavedFlash(true);
    setTimeout(() => setSavedFlash(false), 1400);
  };

  const addFile = async () => {
    const rel = addingName.trim();
    if (!rel) return;
    await writeSkillFile(workspaceId, skillName, rel, "");
    setAddingName("");
    await load(rel);
  };

  const removeFile = async (rel: string) => {
    if (!(await confirmAction(t("settings.skillDeleteFileConfirm", { name: rel })))) return;
    await deleteSkillFile(workspaceId, skillName, rel);
    await load();
  };

  if (files === null) return <Skeleton className="h-32 w-full" />;

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-1.5">
        {files.map((f) => (
          <button
            key={f}
            onClick={() => void openFile(f)}
            className={`rounded px-2 py-0.5 font-mono text-badge ${
              f === selected ? "bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]" : "text-[var(--cf-text-muted)] hover:bg-black/[0.04] dark:hover:bg-white/[0.06]"
            }`}
          >
            {f}
          </button>
        ))}
        <input
          value={addingName}
          aria-label={t("settings.skillNewFile")}
          onChange={(e) => setAddingName(e.target.value)}
          onKeyDown={(e) => e.key === "Enter" && void addFile()}
          placeholder={t("settings.skillNewFile")}
          className="w-32 rounded border border-[var(--cf-border)] bg-transparent px-1.5 py-0.5 font-mono text-badge outline-none focus:border-[var(--cf-accent)]"
        />
      </div>

      {selected && (
        <>
          <textarea
            value={content}
            aria-label={selected}
            onChange={(e) => setContent(e.target.value)}
            onBlur={() => void save()}
            rows={12}
            spellCheck={false}
            className="w-full resize-y rounded-md border border-[var(--cf-border)] bg-transparent px-2.5 py-1.5 font-mono text-body leading-relaxed outline-none focus:border-[var(--cf-accent)]"
          />
          <div className="flex items-center justify-between">
            <span className="text-badge text-[var(--cf-text-muted)]">
              {savedFlash ? t("settings.saved") : t("settings.templateAutosave")}
            </span>
            <Button variant="danger" size="sm" icon={Trash2} onClick={() => void removeFile(selected)}>
              {t("settings.skillDeleteFile")}
            </Button>
          </div>
        </>
      )}
    </div>
  );
}
