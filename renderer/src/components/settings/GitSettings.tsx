import { useEffect, useState } from "react";
import { Check } from "lucide-react";
import { MIN_AUTO_FETCH_SECONDS, usePreferencesStore } from "../../state/preferencesStore";
import { getGitIdentity, setGitIdentity } from "../../lib/ipc/commands";
import { useT } from "../../state/languageStore";
import { pushErrorToast } from "../../state/toastStore";
import { Button } from "../common/Button";
import { Checkbox } from "../common/Checkbox";
import { Field, FIELD_INPUT } from "./Field";
import { WorkspaceGitIdentities } from "./WorkspaceGitIdentities";

export function GitSettings() {
  const t = useT();
  const autoFetchSeconds = usePreferencesStore((s) => s.autoFetchSeconds);
  const setAutoFetchSeconds = usePreferencesStore((s) => s.setAutoFetchSeconds);
  const secretScanEnabled = usePreferencesStore((s) => s.secretScanEnabled);
  const setSecretScanEnabled = usePreferencesStore((s) => s.setSecretScanEnabled);
  const [draft, setDraft] = useState(autoFetchSeconds || 30);

  const [name, setName] = useState("");
  const [email, setEmail] = useState("");
  const [savedName, setSavedName] = useState("");
  const [savedEmail, setSavedEmail] = useState("");
  const [savedIdentity, setSavedIdentity] = useState(false);

  // The `.catch` is the point. Without it a failed read left both fields empty and said nothing,
  // which looks exactly like "CodeFlow did not pick up the git identity I already have configured" —
  // and is indistinguishable from a machine that genuinely has none.
  useEffect(() => {
    void getGitIdentity().then(
      (identity) => {
        setName(identity.name ?? "");
        setEmail(identity.email ?? "");
        setSavedName(identity.name ?? "");
        setSavedEmail(identity.email ?? "");
      },
      (e: unknown) => pushErrorToast(t("toast.gitIdentityLoadFailed", { error: String(e) })),
    );
    // `t` is deliberately not a dependency: re-running this on a language switch would overwrite
    // whatever the user has typed since.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const enabled = autoFetchSeconds > 0;
  const identityDirty = name.trim() !== savedName || email.trim() !== savedEmail;

  const saveIdentity = async () => {
    try {
      await setGitIdentity(name.trim(), email.trim());
      setSavedName(name.trim());
      setSavedEmail(email.trim());
      setSavedIdentity(true);
      setTimeout(() => setSavedIdentity(false), 1500);
    } catch (e) {
      // The checkmark only appears once the write landed. Reporting success unconditionally is how
      // a save that never happened looked like one that did.
      pushErrorToast(t("toast.gitIdentitySaveFailed", { error: String(e) }));
    }
  };

  return (
    <section>
      <h3 className="mb-1 text-title font-semibold">{t("settings.gitTitle")}</h3>

      <p className="mb-2 text-relaxed text-[var(--cf-text-muted)]">{t("settings.gitIdentityHint")}</p>
      <div className="mb-1.5 flex items-end gap-2">
        <Field label={t("settings.name")}>
          {(field) => (
            <input
              {...field}
              value={name}
              onChange={(e) => setName(e.target.value)}
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
              className={FIELD_INPUT}
            />
          )}
        </Field>
        <Button
          variant="primary"
          {...(savedIdentity ? { icon: Check } : {})}
          disabled={!name.trim() || !email.trim() || !identityDirty}
          onClick={saveIdentity}
        >
          {savedIdentity ? t("settings.saved") : t("common.save")}
        </Button>
      </div>

      <h4 className="mt-5 text-relaxed font-semibold">{t("settings.gitWorkspaceIdentitiesTitle")}</h4>
      <WorkspaceGitIdentities />

      <p className="mb-4 mt-4 text-relaxed text-[var(--cf-text-muted)]">{t("settings.autoFetchDescription")}</p>

      <label className="mb-2 flex items-center gap-2 text-relaxed">
        <Checkbox checked={enabled} onChange={(checked) => setAutoFetchSeconds(checked ? draft : 0)} />
        {t("settings.autoFetchLabel")}
        {/* Named explicitly: a `<label>` wrapping two controls associates with the first one, which
            here is the checkbox — leaving this input unnamed. */}
        <input
          type="number"
          aria-label={t("settings.autoFetchLabel")}
          min={MIN_AUTO_FETCH_SECONDS}
          disabled={!enabled}
          value={draft}
          onChange={(e) => {
            const next = Number(e.target.value) || MIN_AUTO_FETCH_SECONDS;
            setDraft(next);
            if (enabled) void setAutoFetchSeconds(next);
          }}
          onBlur={() => enabled && setAutoFetchSeconds(draft)}
          className="w-20 rounded-md border border-[var(--cf-border)] bg-transparent px-2 py-1 text-body outline-none focus:border-[var(--cf-accent)] disabled:opacity-40"
        />
        {t("settings.seconds")}
      </label>
      <p className="text-body text-[var(--cf-text-muted)]">
        {t("settings.autoFetchHint", { n: MIN_AUTO_FETCH_SECONDS })}
      </p>

      <p className="mb-2 mt-4 text-relaxed text-[var(--cf-text-muted)]">{t("settings.secretScanDescription")}</p>
      <label className="mb-1 flex items-center gap-2 text-relaxed">
        <Checkbox checked={secretScanEnabled} onChange={(checked) => setSecretScanEnabled(checked)} />
        {t("settings.secretScanLabel")}
      </label>
      <p className="text-body text-[var(--cf-text-muted)]">{t("settings.secretScanHint")}</p>
    </section>
  );
}
