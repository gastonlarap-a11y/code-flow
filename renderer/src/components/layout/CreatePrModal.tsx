import { useEffect, useId, useMemo, useState } from "react";
import { GitPullRequest, Sparkles } from "lucide-react";
import { Modal } from "../common/Modal";
import { Button } from "../common/Button";
import { listBranches, generatePrDescription } from "../../lib/ipc/commands";
import { usePrStore } from "../../state/prStore";
import { pushErrorToast } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import { Select } from "../common/Select";
import type { BranchInfo, Project } from "../../types/domain";

const PREFERRED_TARGETS = ["main", "master", "develop", "development"];

interface CreatePrModalProps {
  project: Project;
  onClose: () => void;
  onCreated: () => void;
}

/**
 * Opens a pull request on the project's linked host. Branches are read straight from the repo on
 * disk (so it works for any linked project, not only the active one); "Generate with AI" drafts a
 * title + description from the diff between the chosen branches and prefills the form.
 */
export function CreatePrModal({ project, onClose, onCreated }: CreatePrModalProps) {
  const t = useT();
  const createPr = usePrStore((s) => s.createPr);

  const [branches, setBranches] = useState<BranchInfo[] | null>(null);
  const [source, setSource] = useState("");
  const [target, setTarget] = useState("");
  // A visible label that is merely *beside* an input names nothing — the same defect `settings/Field`
  // exists for. Two fields is not worth a component, but it is worth the ids.
  const titleFieldId = useId();
  const descriptionFieldId = useId();
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [draft, setDraft] = useState(false);
  const [generating, setGenerating] = useState(false);
  const [creating, setCreating] = useState(false);

  const localBranches = useMemo(() => (branches ?? []).filter((b) => !b.is_remote), [branches]);

  useEffect(() => {
    let cancelled = false;
    void listBranches(project.local_path)
      .then((list) => {
        if (cancelled) return;
        setBranches(list);
        const local = list.filter((b) => !b.is_remote);
        const head = local.find((b) => b.is_head);
        const src = head?.name ?? local[0]?.name ?? "";
        const tgt =
          local.find((b) => PREFERRED_TARGETS.includes(b.name) && b.name !== src)?.name ??
          local.find((b) => b.name !== src)?.name ??
          "";
        setSource(src);
        setTarget(tgt);
      })
      .catch((e) => {
        if (!cancelled) {
          setBranches([]);
          pushErrorToast(String(e));
        }
      });
    return () => {
      cancelled = true;
    };
  }, [project.local_path]);

  const sameBranch = source !== "" && source === target;
  const canSubmit = !creating && !generating && title.trim() !== "" && source !== "" && target !== "" && !sameBranch;
  const busy = creating || generating;
  const enoughBranches = branches === null || localBranches.length >= 2;

  const generate = async () => {
    if (!source || !target || sameBranch) return;
    setGenerating(true);
    try {
      const draftText = await generatePrDescription(project.id, source, target);
      if (draftText.title.trim()) setTitle(draftText.title.trim());
      setDescription(draftText.body);
    } catch (e) {
      pushErrorToast(String(e));
    } finally {
      setGenerating(false);
    }
  };

  const submit = async () => {
    if (!canSubmit) return;
    setCreating(true);
    try {
      await createPr(project.id, {
        title: title.trim(),
        description,
        sourceBranch: source,
        targetBranch: target,
        draft,
      });
      onCreated();
      onClose();
    } catch (e) {
      pushErrorToast(String(e));
    } finally {
      setCreating(false);
    }
  };



  // No footer at all in the "needs two branches" state: there is nothing to submit, and an action
  // row holding a single disabled button reads as something being wrong with the form.
  const footer = enoughBranches ? (
    <>
      <Button variant="ghost" disabled={busy} onClick={onClose}>
        {t("common.cancel")}
      </Button>
      <Button
        variant="primary"
        icon={GitPullRequest}
        pending={creating}
        disabled={!canSubmit}
        onClick={submit}
      >
        {creating ? t("createPr.creating") : t("createPr.create")}
      </Button>
    </>
  ) : undefined;

  return (
    <Modal
      title="createPr.title"
      icon={GitPullRequest}
      size="lg"
      onClose={onClose}
      dismissible={!busy}
      {...(footer ? { footer } : {})}
    >
      {!enoughBranches ? (
          <p className="py-4 text-center text-body text-[var(--cf-text-muted)]">{t("createPr.needTwoBranches")}</p>
        ) : (
          <>
            <div className="mb-3 flex items-center gap-2">
              <div className="flex-1">
                {/* `Select` is a custom widget, not an `<input>`, so `htmlFor` has nothing to point
                    at — its `ariaLabel` is what names it, and this caption is the visible echo. */}
                <label className="mb-1 block text-badge font-medium text-[var(--cf-text-muted)]">
                  {t("createPr.source")}
                </label>
                <Select
                  value={source}
                  onChange={setSource}
                  disabled={busy || branches === null}
                  ariaLabel={t("createPr.source")}
                  options={localBranches.map((b) => ({ value: b.name, label: b.name }))}
                />
              </div>
              <span className="mt-5 text-[var(--cf-text-muted)]" aria-hidden>→</span>
              <div className="flex-1">
                <label className="mb-1 block text-badge font-medium text-[var(--cf-text-muted)]">
                  {t("createPr.target")}
                </label>
                <Select
                  value={target}
                  onChange={setTarget}
                  disabled={busy || branches === null}
                  ariaLabel={t("createPr.target")}
                  options={localBranches.map((b) => ({ value: b.name, label: b.name }))}
                />
              </div>
            </div>
            {sameBranch && <p className="-mt-2 mb-2 text-badge text-[var(--cf-danger)]">{t("createPr.sameBranch")}</p>}

            <div className="mb-1 flex items-center justify-between">
              <label htmlFor={titleFieldId} className="text-badge font-medium text-[var(--cf-text-muted)]">
                {t("createPr.titleField")}
              </label>
              {/* Icon *and* text, per the icon dictionary: `Sparkles` alone is fourteen different
                  AI actions across the app, so it never travels without a label. */}
              <Button
                variant="ghost"
                size="sm"
                icon={Sparkles}
                pending={generating}
                disabled={busy || sameBranch || !source || !target}
                onClick={generate}
                className="!text-[var(--cf-accent)]"
              >
                {generating ? t("createPr.generating") : t("createPr.generate")}
              </Button>
            </div>
            <input
              id={titleFieldId}
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              placeholder={t("createPr.titlePlaceholder")}
              disabled={busy}
              className="mb-3 w-full rounded-md border border-[var(--cf-border)] bg-[var(--cf-surface)] px-2.5 py-1.5 text-body outline-none focus:border-[var(--cf-accent)] disabled:opacity-50"
            />

            <label htmlFor={descriptionFieldId} className="mb-1 block text-badge font-medium text-[var(--cf-text-muted)]">
              {t("createPr.description")}
            </label>
            <textarea
              id={descriptionFieldId}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder={t("createPr.descriptionPlaceholder")}
              rows={7}
              disabled={busy}
              className="mb-3 w-full resize-none rounded-md border border-[var(--cf-border)] bg-[var(--cf-surface)] px-2.5 py-1.5 font-mono text-ui outline-none focus:border-[var(--cf-accent)] disabled:opacity-50"
            />

            <label className="mb-4 flex items-center gap-2 text-ui text-[var(--cf-text-muted)]">
              <input type="checkbox" checked={draft} onChange={(e) => setDraft(e.target.checked)} disabled={busy} />
              {t("createPr.draft")}
            </label>
          </>
        )}
    </Modal>
  );
}
