import { useEffect, useState } from "react";
import { Check, KeyRound, Trash2 } from "lucide-react";
import { Button } from "../common/Button";
import { Field, FIELD_INPUT } from "./Field";
import { deleteAdoPat, setAdoPat } from "../../lib/ipc/commands";
import { loadAdoConnections, normalizeAdoOrg, saveAdoConnections } from "../../lib/adoConnections";
import { pushErrorToast, useToastStore } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import type { AdoConnection } from "../../types/domain";

export function AzureDevOpsSettings() {
  const t = useT();
  const [connections, setConnections] = useState<AdoConnection[]>([]);
  const [org, setOrg] = useState("");
  const [pat, setPat] = useState("");
  const [saving, setSaving] = useState(false);
  const [loaded, setLoaded] = useState(false);

  useEffect(() => {
    void (async () => {
      setConnections(await loadAdoConnections());
      setLoaded(true);
    })();
  }, []);

  const handleSave = async () => {
    const cleanOrg = normalizeAdoOrg(org);
    if (!cleanOrg || !pat.trim()) return;
    setSaving(true);
    try {
      await setAdoPat(cleanOrg, pat.trim());
      const next = [...connections.filter((c) => c.org.toLowerCase() !== cleanOrg.toLowerCase()), { org: cleanOrg }];
      await saveAdoConnections(next);
      setConnections(next);
      setOrg("");
      setPat("");
      useToastStore.getState().pushToast(t("toast.adoConnected", { org: cleanOrg }), "success");
    } catch (e) {
      pushErrorToast(t("toast.adoSaveFailed", { error: String(e) }));
    } finally {
      setSaving(false);
    }
  };

  const handleRemove = async (removeOrg: string) => {
    try {
      await deleteAdoPat(removeOrg);
      const next = connections.filter((c) => c.org.toLowerCase() !== removeOrg.toLowerCase());
      await saveAdoConnections(next);
      setConnections(next);
      useToastStore.getState().pushToast(t("toast.adoRemoved"), "info");
    } catch (e) {
      pushErrorToast(t("toast.adoRemoveFailed", { error: String(e) }));
    }
  };

  if (!loaded) return null;

  return (
    <section>
      {/* No heading of its own: the integrations row above already names the provider, and printing
          "Azure DevOps" twice, one line apart, reads as a rendering bug. The hint stays — it is the
          part that says what connecting actually does and where the token goes. */}
      <p className="mb-3 text-relaxed text-[var(--cf-text-muted)]">{t("settings.azureHint")}</p>

      {connections.length > 0 && (
        <div className="mb-3 space-y-2">
          {connections.map((conn) => (
            <div key={conn.org} className="flex items-center gap-3 rounded-lg border border-[var(--cf-border)] p-3">
              <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-md bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]">
                <KeyRound size={15} />
              </span>
              <div className="min-w-0 flex-1">
                <p className="truncate text-body font-medium">{conn.org}</p>
                <p className="font-mono text-ui tracking-widest text-[var(--cf-text-muted)]">••••••••••••</p>
              </div>
              <span className="flex shrink-0 items-center gap-1 rounded-full bg-[color-mix(in_oklab,var(--cf-success)_16%,transparent)] px-2 py-0.5 text-badge font-medium text-[var(--cf-success)]">
                <Check size={11} /> {t("settings.connected")}
              </span>
              {/* Disconnecting the account is this card's destructive action, so it carries its
                  name in text rather than in a tooltip (§II.6 row 6). */}
              <Button
                variant="danger"
                size="sm"
                icon={Trash2}
                className="shrink-0"
                onClick={() => handleRemove(conn.org)}
              >
                {t("settings.remove")}
              </Button>
            </div>
          ))}
        </div>
      )}

      <div className="space-y-2">
        <Field label={t("settings.organization")}>
          {(field) => (
            <input {...field} value={org} onChange={(e) => setOrg(e.target.value)} className={FIELD_INPUT} />
          )}
        </Field>
        <Field label={t("settings.personalAccessToken")}>
          {(field) => (
            <input
              {...field}
              type="password"
              value={pat}
              onChange={(e) => setPat(e.target.value)}
              className={FIELD_INPUT}
            />
          )}
        </Field>

        <div className="pt-1">
          <Button
            variant="primary"
            icon={KeyRound}
            pending={saving}
            disabled={!org.trim() || !pat.trim()}
            onClick={handleSave}
          >
            {saving ? t("settings.savingToken") : t("settings.saveToken")}
          </Button>
        </div>
      </div>
    </section>
  );
}
