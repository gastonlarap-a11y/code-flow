import { useState } from "react";
import { Check } from "lucide-react";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { useT } from "../../state/languageStore";
import type { Workspace } from "../../types/domain";
import { Button } from "../common/Button";
import { Chip } from "../common/Chip";
import { Field, FIELD_INPUT } from "./Field";

/**
 * One row per workspace: its commit-identity override, or "use default" when it has none (WS-008).
 *
 * The list is flat rather than collapsed — workspace counts are small, unlike project lists — and
 * lives with the default identity in `GitSettings` so every identity the app can sign with is
 * visible in one place.
 */
export function WorkspaceGitIdentities() {
  const t = useT();
  const workspaces = useWorkspaceStore((s) => s.workspaces);

  if (workspaces.length === 0) return null;

  return (
    <div>
      <p className="mb-2 mt-4 text-relaxed text-[var(--cf-text-muted)]">
        {t("settings.gitWorkspaceIdentitiesHint")}
      </p>
      <ul className="flex flex-col gap-3">
        {workspaces.map((workspace) => (
          // Keyed by id so a row's draft state never survives onto another workspace.
          <WorkspaceIdentityRow key={workspace.id} workspace={workspace} />
        ))}
      </ul>
    </div>
  );
}

function WorkspaceIdentityRow({ workspace }: { workspace: Workspace }) {
  const t = useT();
  const setWorkspaceGitIdentity = useWorkspaceStore((s) => s.setWorkspaceGitIdentity);

  const [name, setName] = useState(workspace.git_name ?? "");
  const [email, setEmail] = useState(workspace.git_email ?? "");
  const [saved, setSaved] = useState(false);

  const hasOverride = workspace.git_name !== null && workspace.git_email !== null;
  const dirty = name.trim() !== (workspace.git_name ?? "") || email.trim() !== (workspace.git_email ?? "");

  const save = async () => {
    await setWorkspaceGitIdentity(workspace.id, name.trim(), email.trim());
    setSaved(true);
    setTimeout(() => setSaved(false), 1500);
  };

  const clear = async () => {
    await setWorkspaceGitIdentity(workspace.id, null, null);
    setName("");
    setEmail("");
  };

  return (
    <li>
      <div className="mb-1 flex items-center gap-2">
        <span className="size-2 rounded-full" style={{ backgroundColor: workspace.color }} />
        <span className="text-relaxed font-medium">{workspace.name}</span>
        {/* Outline rather than a filled chip: this annotates the workspace ("it has an override")
            and is not a status anyone needs to react to. */}
        {hasOverride && <Chip variant="outline">{t("settings.gitWorkspaceOverrideActive")}</Chip>}
      </div>
      <div className="flex items-end gap-2">
        <Field label={t("settings.name")}>
          {(field) => (
            <input
              {...field}
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder={t("settings.gitWorkspaceUseDefault")}
              className={FIELD_INPUT}
            />
          )}
        </Field>
        <Field label={t("settings.email")}>
          {(field) => (
            <input
              {...field}
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder={t("settings.gitWorkspaceUseDefault")}
              className={FIELD_INPUT}
            />
          )}
        </Field>
        <Button
          variant="primary"
          {...(saved ? { icon: Check } : {})}
          disabled={!name.trim() || !email.trim() || !dirty}
          onClick={save}
        >
          {saved ? t("settings.saved") : t("common.save")}
        </Button>
        {hasOverride && (
          <Button variant="ghost" onClick={clear}>
            {t("settings.gitWorkspaceUseDefault")}
          </Button>
        )}
      </div>
    </li>
  );
}
