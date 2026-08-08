import { useState } from "react";
import { GitFork } from "lucide-react";
import { Modal } from "../common/Modal";
import { Button } from "../common/Button";
import { linkProjectGithub } from "../../lib/ipc/commands";
import { githubHostLabel } from "../../lib/githubConnections";
import { pushErrorToast } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import { Select } from "../common/Select";

interface ConnectGithubModalProps {
  projectId: string;
  /** Hosts the user has a token for — the manual link can only target one of these. */
  hosts: string[];
  onConnected: () => void;
  onClose: () => void;
}

// Manual fallback for a project whose GitHub remote couldn't be auto-detected (a repo with no
// recognized GitHub origin, or an unusual URL). Owner/repo are typed rather than picked — a
// token can see far too many repos to enumerate into a dropdown the way an Azure org's are.
export function ConnectGithubModal({ projectId, hosts, onConnected, onClose }: ConnectGithubModalProps) {
  const t = useT();
  const [host, setHost] = useState(hosts[0] ?? "github.com");
  const [owner, setOwner] = useState("");
  const [repo, setRepo] = useState("");
  const [saving, setSaving] = useState(false);

  const connect = async () => {
    if (!owner.trim() || !repo.trim() || !host) return;
    setSaving(true);
    try {
      await linkProjectGithub(projectId, owner.trim(), repo.trim(), host);
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
      title="sidebar.linkGithubTitle"
      icon={GitFork}
      onClose={onClose}
      dismissible={!saving}
      footer={
      <>
        <Button variant="ghost" disabled={saving} onClick={onClose}>
          {t("common.cancel")}
        </Button>
        <Button
          variant="primary"
          icon={GitFork}
          pending={saving}
          disabled={!owner.trim() || !repo.trim()}
          onClick={connect}
        >
          {t("sidebar.connect")}
        </Button>
      </>
      }
    >
      {hosts.length > 1 && (
        <>
          <label className="mb-1 block text-badge font-medium text-[var(--cf-text-muted)]">
            {t("settings.githubHostLabel")}
          </label>
          <Select
            value={host}
            onChange={setHost}
            className="mb-3"
            ariaLabel={t("settings.githubHostLabel")}
            options={hosts.map((h) => ({ value: h, label: githubHostLabel(h) }))}
          />
        </>
      )}

      <label className="mb-1 block text-badge font-medium text-[var(--cf-text-muted)]">
        {t("sidebar.githubOwner")}
      </label>
      <input
        value={owner}
        onChange={(e) => setOwner(e.target.value)}
        placeholder={t("sidebar.githubOwnerPlaceholder")}
        className="mb-3 w-full cf-interactive rounded-[var(--radius-control)] border border-[var(--cf-border)] bg-[var(--cf-surface)] px-2.5 py-1.5 text-body outline-none focus:border-[var(--cf-accent)]"
      />

      <label className="mb-1 block text-badge font-medium text-[var(--cf-text-muted)]">
        {t("sidebar.githubRepo")}
      </label>
      <input
        value={repo}
        onChange={(e) => setRepo(e.target.value)}
        placeholder={t("sidebar.githubRepoPlaceholder")}
        className="mb-4 w-full cf-interactive rounded-[var(--radius-control)] border border-[var(--cf-border)] bg-[var(--cf-surface)] px-2.5 py-1.5 text-body outline-none focus:border-[var(--cf-accent)]"
      />
    </Modal>
  );
}
