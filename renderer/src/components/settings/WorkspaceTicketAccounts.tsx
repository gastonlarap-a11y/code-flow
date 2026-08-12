import { useEffect, useState } from "react";
import { Select } from "../common/Select";
import { Field } from "./Field";
import { Chip } from "../common/Chip";
import { loadAdoConnections } from "../../lib/adoConnections";
import { adoListProjects, updateWorkspaceTicketAccount } from "../../lib/ipc/commands";
import { useTicketStore } from "../../state/ticketStore";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { useT } from "../../state/languageStore";
import type { Workspace } from "../../types/domain";

/**
 * One row per workspace: which Azure organisation its tickets come from.
 *
 * <b>It exists because the board is not necessarily where the code is.</b> A repository's own link
 * already names an organisation, and inferring the tickets from it is right most of the time and
 * silently wrong exactly when someone has both a work account and a personal one — which is the
 * case this is for. Left unset the resolution falls through to the repository's link, so this only
 * has to be touched when the two differ.
 *
 * Shaped after `WorkspaceGitIdentities`, which answers the same kind of question for commits.
 */
export function WorkspaceTicketAccounts() {
  const t = useT();
  const workspaces = useWorkspaceStore((s) => s.workspaces);
  const [orgs, setOrgs] = useState<string[]>([]);

  useEffect(() => {
    let cancelled = false;
    void loadAdoConnections()
      .then((connections) => {
        if (!cancelled) setOrgs(connections.map((connection) => connection.org));
      })
      .catch(() => {
        // The Integrations section reports its own failures; an empty list here reads as
        // "nothing connected", which is the same thing the user has to fix either way.
      });
    return () => {
      cancelled = true;
    };
  }, []);

  if (workspaces.length === 0) return null;

  return (
    <div>
      <h4 className="mb-1 text-relaxed font-medium">{t("settings.ticketAccountTitle")}</h4>
      <p className="mb-3 text-body text-[var(--cf-text-muted)]">{t("settings.ticketAccountHint")}</p>

      <ul className="flex flex-col gap-3">
        {workspaces.map((workspace) => (
          <WorkspaceAccountRow key={workspace.id} workspace={workspace} orgs={orgs} />
        ))}
      </ul>
    </div>
  );
}

function WorkspaceAccountRow({ workspace, orgs }: { workspace: Workspace; orgs: string[] }) {
  const t = useT();
  const reload = useWorkspaceStore((s) => s.loadWorkspaces);
  const [org, setOrg] = useState(workspace.ado_org ?? "");
  const [project, setProject] = useState(workspace.ado_project ?? "");
  // The answer is stored with the organisation it was for, and "which projects to show" is derived
  // from that at render. Clearing it in the effect instead would be a synchronous setState in an
  // effect body — a cascading render, and one the React Compiler rejects.
  const [listed, setListed] = useState<{ forOrg: string; names: string[] } | null>(null);

  // The board's projects, listed from the chosen organisation. Read rather than typed by hand
  // because the name has to match Azure's exactly, and a typo surfaces later as an empty board.
  useEffect(() => {
    if (org.length === 0) return;

    let cancelled = false;
    void adoListProjects(org)
      .then((found) => {
        if (!cancelled) setListed({ forOrg: org, names: found.map((candidate) => candidate.name) });
      })
      .catch(() => {
        // A missing or expired token for this organisation is reported by the connection row
        // above; here it means the list cannot be offered, and the field stays as it was.
        if (!cancelled) setListed({ forOrg: org, names: [] });
      });
    return () => {
      cancelled = true;
    };
  }, [org]);

  const projects = listed?.forOrg === org ? listed.names : null;

  /**
   * Saves the pair and tells the tickets module to re-resolve.
   *
   * That last call is the fix for the defect this row had: the work-items view resolves its account
   * when it mounts, and the settings panel opens *over* it, so choosing an organisation here changed
   * the database and left the view showing "nothing decided this" — the question the user had just
   * answered.
   */
  const save = async (nextOrg: string, nextProject: string) => {
    setOrg(nextOrg);
    setProject(nextProject);
    // An empty choice clears the column, and clearing is a real answer: the resolution then falls
    // back to the repository's own link rather than to nothing.
    await updateWorkspaceTicketAccount(
      workspace.id,
      nextOrg.length === 0 ? null : nextOrg,
      nextProject.length === 0 ? null : nextProject,
    );
    await reload();
    await useTicketStore.getState().refreshAccount();
  };

  return (
    <li>
      <div className="mb-1 flex items-center gap-2">
        <span className="size-2 rounded-full" style={{ backgroundColor: workspace.color }} />
        <span className="text-relaxed font-medium">{workspace.name}</span>
        {workspace.ado_org && <Chip variant="outline">{workspace.ado_org}</Chip>}
        {workspace.ado_project && <Chip variant="outline">{workspace.ado_project}</Chip>}
      </div>

      <div className="flex flex-wrap gap-3">
        <Field label={t("settings.ticketAccountOrg")}>
          {(field) => (
            <Select
              {...field}
              value={org}
              // Changing the organisation drops the project: a project name belongs to the
              // organisation it was listed from, and carrying it over addresses a board that
              // usually does not exist.
              onChange={(next: string) => void save(next, "")}
              options={[
                { value: "", label: t("settings.ticketAccountInherit") },
                ...orgs.map((candidate) => ({ value: candidate, label: candidate })),
              ]}
            />
          )}
        </Field>

        <Field label={t("settings.ticketAccountProject")}>
          {(field) => (
            <Select
              {...field}
              value={project}
              disabled={org.length === 0 || projects === null}
              onChange={(next: string) => void save(org, next)}
              options={[
                { value: "", label: t("settings.ticketAccountProjectInherit") },
                ...(projects ?? []).map((candidate) => ({ value: candidate, label: candidate })),
              ]}
            />
          )}
        </Field>
      </div>

      <p className="mt-1 text-body text-[var(--cf-text-muted)]">
        {t("settings.ticketAccountProjectHint")}
      </p>
    </li>
  );
}
