import { useEffect, useState } from "react";
import { Cloud } from "lucide-react";
import { Modal } from "../common/Modal";
import { Button } from "../common/Button";
import { adoListProjects, adoListRepos, linkProjectAdo } from "../../lib/ipc/commands";
import { pushErrorToast } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import { Select } from "../common/Select";
import type { AdoProject, AdoRepo } from "../../types/domain";

interface ConnectAdoModalProps {
  projectId: string;
  /** Orgs the user has a PAT for — the manual link picks one of these. */
  orgs: string[];
  onConnected: () => void;
  onClose: () => void;
}

export function ConnectAdoModal({ projectId, orgs, onConnected, onClose }: ConnectAdoModalProps) {
  const t = useT();
  const [org, setOrg] = useState(orgs[0] ?? "");
  const [adoProjects, setAdoProjects] = useState<AdoProject[]>([]);
  const [repos, setRepos] = useState<AdoRepo[]>([]);
  const [adoProjectId, setAdoProjectId] = useState("");
  const [repoId, setRepoId] = useState("");
  const [loadingProjects, setLoadingProjects] = useState(true);
  const [loadingRepos, setLoadingRepos] = useState(false);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (!org) return;
    setAdoProjectId("");
    setAdoProjects([]);
    setLoadingProjects(true);
    adoListProjects(org)
      .then(setAdoProjects)
      .catch((e) => pushErrorToast(String(e)))
      .finally(() => setLoadingProjects(false));
  }, [org]);

  useEffect(() => {
    setRepoId("");
    setRepos([]);
    if (!adoProjectId) return;
    setLoadingRepos(true);
    adoListRepos(org, adoProjectId)
      .then(setRepos)
      .catch((e) => pushErrorToast(String(e)))
      .finally(() => setLoadingRepos(false));
  }, [org, adoProjectId]);

  const adoProjectName = adoProjects.find((p) => p.id === adoProjectId)?.name ?? "";

  const connect = async () => {
    if (!org || !adoProjectId || !repoId) return;
    setSaving(true);
    try {
      await linkProjectAdo(projectId, org, adoProjectName, repoId);
      onConnected();
      onClose();
    } catch (e) {
      pushErrorToast(String(e));
    } finally {
      setSaving(false);
    }
  };



  return (
    <Modal
      title="sidebar.linkAdoTitle"
      icon={Cloud}
      onClose={onClose}
      dismissible={!saving}
      footer={
      <>
        <Button variant="ghost" disabled={saving} onClick={onClose}>
          {t("common.cancel")}
        </Button>
        <Button
          variant="primary"
          icon={Cloud}
          pending={saving}
          disabled={!adoProjectId || !repoId}
          onClick={connect}
        >
          {t("sidebar.connect")}
        </Button>
      </>
      }
    >
      <label className="mb-1 block text-badge font-medium text-[var(--cf-text-muted)]">
        {t("settings.organization")}
      </label>
      {orgs.length > 1 ? (
        <Select
          value={org}
          onChange={setOrg}
          className="mb-3"
          ariaLabel={t("settings.organization")}
          options={orgs.map((o) => ({ value: o, label: o }))}
        />
      ) : (
        <p className="mb-3 rounded-md border border-[var(--cf-border)] bg-black/[0.02] px-2.5 py-1.5 text-body dark:bg-white/[0.03]">
          {org}
        </p>
      )}

      <label className="mb-1 block text-badge font-medium text-[var(--cf-text-muted)]">
        {t("sidebar.adoProject")}
      </label>
      <Select
        disabled={loadingProjects}
        value={adoProjectId}
        onChange={setAdoProjectId}
        className="mb-3"
        ariaLabel={t("sidebar.adoProject")}
        options={[
          { value: "", label: loadingProjects ? t("editor.loading") : t("sidebar.selectAdoProject") },
          ...adoProjects.map((p) => ({ value: p.id, label: p.name })),
        ]}
      />

      <label className="mb-1 block text-badge font-medium text-[var(--cf-text-muted)]">
        {t("sidebar.adoRepo")}
      </label>
      <Select
        disabled={!adoProjectId || loadingRepos}
        value={repoId}
        onChange={setRepoId}
        className="mb-4"
        ariaLabel={t("sidebar.adoRepo")}
        options={[
          { value: "", label: loadingRepos ? t("editor.loading") : t("sidebar.selectAdoRepo") },
          ...repos.map((r) => ({ value: r.id, label: r.name })),
        ]}
      />
    </Modal>
  );
}
