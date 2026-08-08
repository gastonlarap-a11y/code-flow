import { Suspense, useState } from "react";
import { lazyRetry } from "../../../lib/lazyRetry";
import { Modal } from "../../common/Modal";
import { IconButton } from "../../common/IconButton";
import { RowActions } from "../../common/RowActions";
import { Button } from "../../common/Button";
import {
  Archive,
  Check,
  Cloud,
  CircleDot,
  Eye,
  GitBranchPlus,
  GitCommitHorizontal,
  Link2,
  Loader2,
  Pencil,
  Plus,
  Trash2,
  Undo2,
  Unlink,
} from "lucide-react";
import { useRepoStore } from "../../../state/repoStore";
import type { BranchInfo, CommitInfo, StashInfo } from "../../../types/domain";
import { CollapsibleSection } from "../../common/CollapsibleSection";
import { Select } from "../../common/Select";
// Both reach `DiffView`, and the Sidebar is mounted for the whole life of the app — a static import
// here is one of the paths that kept Monaco in the entry chunk.
const StashDiffModal = lazyRetry(() =>
  import("../StashDiffModal").then((m) => ({ default: m.StashDiffModal })),
);
const CommitDiffModal = lazyRetry(() =>
  import("../CommitDiffModal").then((m) => ({ default: m.CommitDiffModal })),
);
import { confirmAction } from "../../../state/confirmStore";
import { useT } from "../../../state/languageStore";

export function StashesSection() {
  const stashes = useRepoStore((s) => s.stashes);
  const stashSave = useRepoStore((s) => s.stashSave);
  const stashApply = useRepoStore((s) => s.stashApply);
  const stashPop = useRepoStore((s) => s.stashPop);
  const stashDrop = useRepoStore((s) => s.stashDrop);
  const renameStash = useRepoStore((s) => s.renameStash);
  const [showInput, setShowInput] = useState(false);
  const [message, setMessage] = useState("");
  const [viewingStash, setViewingStash] = useState<StashInfo | null>(null);
  const [renamingIndex, setRenamingIndex] = useState<number | null>(null);
  const [renameValue, setRenameValue] = useState("");
  const t = useT();

  const commitRename = async () => {
    if (renamingIndex === null) return;
    const value = renameValue.trim();
    setRenamingIndex(null);
    if (value) await renameStash(renamingIndex, value);
  };

  return (
    <CollapsibleSection
      icon={Archive}
      title={t("sidebar.stashes")}
      action={
        <IconButton
          label="sidebar.stashCurrentChanges"
          icon={Plus}
          onClick={() => setShowInput((v) => !v)}
          active={showInput}
        />
      }
    >
      {showInput && (
        <div className="mb-1.5 flex items-center gap-1">
          <input
            autoFocus
            value={message}
            onChange={(e) => setMessage(e.target.value)}
            onKeyDown={async (e) => {
              if (e.key === "Enter") {
                await stashSave(message || undefined, true);
                setMessage("");
                setShowInput(false);
              } else if (e.key === "Escape") {
                setShowInput(false);
              }
            }}
            placeholder={t("sidebar.stashMessage")}
            className="flex-1 rounded-md border border-[var(--cf-border)] bg-transparent px-1.5 py-0.5 text-ui outline-none focus:border-[var(--cf-accent)]"
          />
          <IconButton
            label="common.confirm"
            icon={Check}
            onClick={async () => {
              await stashSave(message || undefined, true);
              setMessage("");
              setShowInput(false);
            }}
            className="!text-[var(--cf-accent)]"
          />
        </div>
      )}

      <div className="space-y-0.5">
        {stashes.map((s) =>
          renamingIndex === s.index ? (
            <div key={s.index} className="flex items-center gap-1 px-1.5 py-0.5">
              <input
                autoFocus
                value={renameValue}
                onChange={(e) => setRenameValue(e.target.value)}
                onClick={(e) => e.stopPropagation()}
                onKeyDown={async (e) => {
                  if (e.key === "Enter") await commitRename();
                  else if (e.key === "Escape") setRenamingIndex(null);
                }}
                onBlur={commitRename}
                className="flex-1 rounded-md border border-[var(--cf-border)] bg-transparent px-1.5 py-0.5 text-body outline-none focus:border-[var(--cf-accent)]"
              />
            </div>
          ) : (
            <div
              key={s.index}
              onClick={() => setViewingStash(s)}
              className="group flex h-7 cursor-pointer items-center gap-1 rounded-[var(--radius-control)] px-1.5 text-body hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
            >
              <span className="flex-1 truncate text-[var(--cf-text-muted)]">{s.message}</span>
              {/* Apply stays on the row: it is the one you reach for, and burying the common case
                  one click deep is how a menu makes things worse. The rest go in the menu. */}
              <IconButton
                label="sidebar.apply"
                icon={Check}
                onClick={(e: React.MouseEvent) => {
                  e.stopPropagation();
                  void stashApply(s.index);
                }}
              />
              <RowActions
                actions={[
                  {
                    id: "view",
                    labelKey: "sidebar.viewStash",
                    icon: Eye,
                    onSelect: () => setViewingStash(s),
                  },
                  {
                    id: "rename",
                    labelKey: "sidebar.renameStash",
                    icon: Pencil,
                    onSelect: () => {
                      setRenameValue(s.message);
                      setRenamingIndex(s.index);
                    },
                  },
                  { id: "pop", labelKey: "sidebar.pop", icon: Undo2, onSelect: () => void stashPop(s.index) },
                  {
                    id: "drop",
                    labelKey: "sidebar.drop",
                    icon: Trash2,
                    danger: true,
                    onSelect: () => {
                      void confirmAction(t("sidebar.dropStashConfirm", { message: s.message })).then(
                        (ok) => ok && void stashDrop(s.index),
                      );
                    },
                  },
                ]}
              />
            </div>
          ),
        )}
        {stashes.length === 0 && !showInput && (
          <p className="px-1.5 text-ui text-[var(--cf-text-muted)]">{t("sidebar.noStashes")}</p>
        )}
      </div>
      {viewingStash && (
        <Suspense fallback={null}>
          <StashDiffModal stash={viewingStash} onClose={() => setViewingStash(null)} />
        </Suspense>
      )}
    </CollapsibleSection>
  );
}

/**
 * The commits this branch has that its upstream does not — what a push would send.
 *
 * The Changes panel has its own list of these, aimed at undoing the last one; this is the reading
 * end of the same data, next to the branch it belongs to, where a row opens the commit's diff.
 */
export function UnpushedCommitsSection() {
  const unpushedCommits = useRepoStore((s) => s.unpushedCommits);
  const [viewingCommit, setViewingCommit] = useState<CommitInfo | null>(null);
  const t = useT();

  return (
    <CollapsibleSection
      icon={GitCommitHorizontal}
      title={t("sidebar.unpushedCommits", { n: unpushedCommits.length })}
    >
      <div className="space-y-0.5">
        {unpushedCommits.map((c) => (
          <button
            key={c.id}
            onClick={() => setViewingCommit(c)}
            className="cf-focusable flex h-7 w-full items-center gap-1.5 rounded-[var(--radius-control)] px-1.5 text-left text-body hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
          >
            <span className="min-w-0 flex-1 truncate text-[var(--cf-text-muted)]">{c.summary}</span>
            <span className="shrink-0 font-mono text-badge text-[var(--cf-text-muted)]">{c.short_id}</span>
          </button>
        ))}
        {unpushedCommits.length === 0 && (
          <p className="px-1.5 text-ui text-[var(--cf-text-muted)]">{t("sidebar.noUnpushedCommits")}</p>
        )}
      </div>
      {viewingCommit && (
        <Suspense fallback={null}>
          <CommitDiffModal commit={viewingCommit} onClose={() => setViewingCommit(null)} />
        </Suspense>
      )}
    </CollapsibleSection>
  );
}

export function RemoteBranchesSection({ branches }: { branches: BranchInfo[] }) {
  const checkoutRemoteBranch = useRepoStore((s) => s.checkoutRemoteBranch);
  const checkoutDetached = useRepoStore((s) => s.checkoutDetached);
  const checkingOutBranch = useRepoStore((s) => s.checkingOutBranch);
  const remoteBranches = branches.filter((b) => b.is_remote);
  const t = useT();
  if (remoteBranches.length === 0) return null;

  return (
    <CollapsibleSection icon={Cloud} title={t("sidebar.remoteBranches")}>
      <div className="space-y-0.5">
        {remoteBranches.map((b) => {
          const isCheckingOut = checkingOutBranch === b.name;
          return (
            <div
              key={b.name}
              className="group flex h-7 items-center gap-1.5 truncate rounded-[var(--radius-control)] px-1.5 text-body text-[var(--cf-text-muted)]"
            >
              {isCheckingOut ? (
                <Loader2 size={12} className="shrink-0 animate-spin" />
              ) : (
                <CircleDot size={12} className="shrink-0 opacity-20" />
              )}
              <span className="flex-1 min-w-0 truncate">{b.name}</span>
              {/* Checking a remote branch out locally is the reason you came to this row; the
                  detached variant is the deliberate, rarer one. */}
              <IconButton
                label="sidebar.checkoutLocally"
                icon={GitBranchPlus}
                disabled={checkingOutBranch !== null}
                onClick={() => checkoutRemoteBranch(b.name)}
              />
              <RowActions
                actions={[
                  {
                    id: "detached",
                    labelKey: "sidebar.checkoutDetached",
                    icon: Unlink,
                    disabled: checkingOutBranch !== null,
                    onSelect: () => checkoutDetached(b.name),
                  },
                ]}
              />
            </div>
          );
        })}
      </div>
    </CollapsibleSection>
  );
}

function RemoteUrlEditModal({
  name,
  currentUrl,
  onClose,
}: {
  name: string;
  currentUrl: string;
  onClose: () => void;
}) {
  const setRemoteUrl = useRepoStore((s) => s.setRemoteUrl);
  const [draft, setDraft] = useState(currentUrl);
  const [saving, setSaving] = useState(false);
  const t = useT();

  const confirm = async () => {
    if (!draft.trim() || draft.trim() === currentUrl) {
      onClose();
      return;
    }
    setSaving(true);
    try {
      await setRemoteUrl(name, draft.trim());
      onClose();
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal
      title="sidebar.changeRemoteUrl"
      subtitle={name}
      icon={Link2}
      onClose={onClose}
      dismissible={!saving}
      footer={
        <>
          <Button variant="ghost" disabled={saving} onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button
            variant="primary"
            icon={Check}
            pending={saving}
            disabled={!draft.trim()}
            onClick={confirm}
          >
            {t("common.confirm")}
          </Button>
        </>
      }
    >
      <label className="mb-1 block text-badge font-medium text-[var(--cf-text-muted)]">
        {t("sidebar.current")}
      </label>
      <div className="mb-3 overflow-x-auto rounded-[var(--radius-control)] bg-black/[0.04] px-2 py-1.5 dark:bg-white/[0.06]">
        <p className="whitespace-nowrap font-mono text-ui text-[var(--cf-text-muted)]">{currentUrl}</p>
      </div>

      <label className="mb-1 block text-badge font-medium text-[var(--cf-text-muted)]">
        {t("sidebar.newUrl")}
      </label>
      <input
        autoFocus
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter") void confirm();
        }}
        className="cf-interactive w-full overflow-x-auto rounded-[var(--radius-control)] border border-[var(--cf-border)] bg-transparent px-2 py-1.5 font-mono text-ui outline-none focus:border-[var(--cf-accent)]"
      />
    </Modal>
  );
}

export function RemoteUrlSection() {
  const remotes = useRepoStore((s) => s.remotes);
  const [editing, setEditing] = useState<string | null>(null);
  const t = useT();

  if (remotes.length === 0) return null;

  const editingRemote = remotes.find((r) => r.name === editing);

  return (
    <CollapsibleSection icon={Cloud} title={t("sidebar.remoteUrl")}>
      <div className="space-y-0.5">
        {remotes.map((r) => (
          <div
            key={r.name}
            className="group flex h-7 items-center gap-1.5 rounded-[var(--radius-control)] px-1.5 text-body hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
          >
            <span className="shrink-0 font-medium text-[var(--cf-text-muted)]">{r.name}</span>
            <span className="flex-1 truncate font-mono text-ui text-[var(--cf-text-muted)]">{r.url}</span>
            {/* One action, so a menu would be a worse version of a button. Dimmed until the row is
                hovered or focused, but always there. */}
            <IconButton
              label="sidebar.changeRemoteUrl"
              icon={Pencil}
              onClick={() => setEditing(r.name)}
              className="shrink-0 opacity-55 group-hover:opacity-100 group-focus-within:opacity-100"
            />
          </div>
        ))}
      </div>

      {editingRemote && (
        <RemoteUrlEditModal name={editingRemote.name} currentUrl={editingRemote.url} onClose={() => setEditing(null)} />
      )}
    </CollapsibleSection>
  );
}

export function CreateBranchForm({ branches, onDone }: { branches: BranchInfo[]; onDone: () => void }) {
  const createBranch = useRepoStore((s) => s.createBranch);
  const [name, setName] = useState("");
  const [startPoint, setStartPoint] = useState("");
  const t = useT();

  return (
    <div className="mb-1.5 space-y-1 rounded-md border border-[var(--cf-border)] p-1.5">
      <input
        autoFocus
        value={name}
        onChange={(e) => setName(e.target.value)}
        onKeyDown={(e) => e.key === "Escape" && onDone()}
        placeholder={t("sidebar.newBranchName")}
        className="w-full rounded-md border border-[var(--cf-border)] bg-transparent px-1.5 py-0.5 text-ui outline-none focus:border-[var(--cf-accent)]"
      />
      <Select
        value={startPoint}
        onChange={setStartPoint}
        size="sm"
        ariaLabel={t("sidebar.fromCurrentHead")}
        options={[
          { value: "", label: t("sidebar.fromCurrentHead") },
          { label: t("sidebar.local"), options: branches.filter((b) => !b.is_remote).map((b) => ({ value: b.name, label: b.name })) },
          { label: t("sidebar.remote"), options: branches.filter((b) => b.is_remote).map((b) => ({ value: b.name, label: b.name })) },
        ]}
      />
      <div className="flex justify-end gap-2 pt-0.5">
        <Button variant="ghost" size="sm" onClick={onDone}>
          {t("common.cancel")}
        </Button>
        <Button
          variant="primary"
          size="sm"
          disabled={!name.trim()}
          onClick={async () => {
            await createBranch(name.trim(), startPoint || undefined);
            onDone();
          }}
        >
          {t("sidebar.create")}
        </Button>
      </div>
    </div>
  );
}
