import { useEffect, useState } from "react";
import { GitBranchPlus } from "lucide-react";
import { Modal } from "../common/Modal";
import { Button } from "../common/Button";
import { Tooltip } from "../common/Tooltip";
import { defaultCloneDir, gitClone } from "../../lib/ipc/commands";
import { onGitProgress } from "../../lib/ipc/events";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { pushErrorToast } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import type { Project } from "../../types/domain";

function deriveName(url: string): string {
  const trimmed = url.trim().replace(/\/+$/, "");
  const last = trimmed.split(/[\\/]/).pop() ?? "repo";
  return last.replace(/\.git$/i, "") || "repo";
}

export function CloneRepoModal({
  workspaceId,
  initialUrl,
  onClose,
  onCloned,
}: {
  workspaceId: string;
  /** Prefills the URL field — used when the clone was offered for a specific repository (the
   * "this pull request's repo isn't in CodeFlow yet" path). */
  initialUrl?: string;
  onClose: () => void;
  /** Fires with the freshly-added project, before `onClose`, so the caller can continue whatever
   * it needed the repository for. */
  onCloned?: (project: Project) => void;
}) {
  const addProject = useWorkspaceStore((s) => s.addProject);
  const t = useT();
  const [baseDir, setBaseDir] = useState("");
  const [url, setUrl] = useState(initialUrl ?? "");
  const [name, setName] = useState("");
  const [nameEdited, setNameEdited] = useState(false);
  const [cloning, setCloning] = useState(false);
  const [lines, setLines] = useState<string[]>([]);

  useEffect(() => {
    void defaultCloneDir().then(setBaseDir);
  }, []);

  useEffect(() => {
    if (!nameEdited && url.trim()) setName(deriveName(url));
  }, [url, nameEdited]);

  // Falls back to the repo's own name whenever the field is left blank — whether the
  // user never touched it, or cleared it out on purpose — rather than blocking Clone.
  const effectiveName = name.trim() || deriveName(url);
  const dest = baseDir && effectiveName ? `${baseDir}/${effectiveName}` : "";

  const clone = async () => {
    if (!url.trim() || !dest) return;
    setCloning(true);
    setLines([]);
    const unlistenProgress = await onGitProgress((e) => {
      if (e.op === "clone") setLines((prev) => [...prev.slice(-200), e.line]);
    });
    try {
      await gitClone(url.trim(), dest);
      const project = await addProject({
        workspace_id: workspaceId,
        name: effectiveName,
        local_path: dest,
        remote_url: url.trim(),
        // No colour: `addProject` picks the least-used one.
        icon: "git-branch",
        ado_org: null,
        ado_project: null,
        ado_repo_id: null,
        github_owner: null,
        github_repo: null,
        github_host: null,
      });
      onCloned?.(project);
      onClose();
    } catch (e) {
      pushErrorToast(String(e));
    } finally {
      setCloning(false);
      void unlistenProgress();
    }
  };

  return (
    <Modal
      title="clone.title"
      icon={GitBranchPlus}
      onClose={onClose}
      // A clone in flight cannot be dismissed: Escape, the scrim and the close button all go away
      // rather than abandon a half-written working copy on disk.
      dismissible={!cloning}
      footer={
        <>
          <Button variant="ghost" disabled={cloning} onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button
            variant="primary"
            icon={GitBranchPlus}
            pending={cloning}
            disabled={!url.trim()}
            onClick={clone}
          >
            {cloning ? t("clone.cloning") : t("clone.clone")}
          </Button>
        </>
      }
    >
      <label className="mb-1 block text-badge font-medium text-[var(--cf-text-muted)]">
        {t("clone.url")}
      </label>
      <input
        autoFocus
        disabled={cloning}
        value={url}
        onChange={(e) => setUrl(e.target.value)}
        placeholder="https://github.com/user/repo.git"
        className="cf-interactive mb-3 w-full overflow-x-auto rounded-[var(--radius-control)] border border-[var(--cf-border)] bg-transparent px-2 py-1.5 font-mono text-ui outline-none focus:border-[var(--cf-accent)] disabled:opacity-50"
      />

      <label className="mb-1 block text-badge font-medium text-[var(--cf-text-muted)]">
        {t("clone.folderName")}
      </label>
      <input
        disabled={cloning}
        value={name}
        onChange={(e) => {
          setName(e.target.value);
          setNameEdited(true);
        }}
        placeholder={deriveName(url) || "repo"}
        className="cf-interactive mb-1 w-full rounded-[var(--radius-control)] border border-[var(--cf-border)] bg-transparent px-2 py-1.5 text-ui outline-none focus:border-[var(--cf-accent)] disabled:opacity-50"
      />
      {/* The destination path is truncated, so the full one has to be reachable somehow — through the
          app's own tooltip rather than the native one, which no other surface here uses. */}
      <Tooltip label={dest}>
        <p className="truncate font-mono text-badge text-[var(--cf-text-muted)]">{dest || "…"}</p>
      </Tooltip>

      {lines.length > 0 && (
        <div className="mt-3 max-h-32 overflow-auto rounded-[var(--radius-control)] bg-black/[0.04] p-2 font-mono text-badge text-[var(--cf-text-muted)] dark:bg-white/[0.06]">
          {lines.map((line, i) => (
            <div key={i} className="whitespace-pre-wrap break-all">
              {line}
            </div>
          ))}
        </div>
      )}
    </Modal>
  );
}
