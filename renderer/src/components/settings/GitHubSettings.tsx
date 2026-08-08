import { useEffect, useState } from "react";
import { Check, KeyRound, Trash2 } from "lucide-react";
import { Button } from "../common/Button";
import { Field, FIELD_INPUT } from "./Field";
import { deleteGithubToken, githubAuthenticatedUser, setGithubToken } from "../../lib/ipc/commands";
import {
  GITHUB_COM,
  githubHostLabel,
  loadGithubConnections,
  normalizeGithubHost,
  saveGithubConnections,
} from "../../lib/githubConnections";
import { pushErrorToast, useToastStore } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import type { GithubConnection } from "../../types/domain";

export function GitHubSettings() {
  const t = useT();
  const [connections, setConnections] = useState<GithubConnection[]>([]);
  const [host, setHost] = useState(GITHUB_COM);
  const [token, setToken] = useState("");
  const [saving, setSaving] = useState(false);
  const [loaded, setLoaded] = useState(false);

  useEffect(() => {
    void (async () => {
      setConnections(await loadGithubConnections());
      setLoaded(true);
    })();
  }, []);

  const handleSave = async () => {
    const cleanHost = normalizeGithubHost(host);
    if (!cleanHost || !token.trim()) return;
    setSaving(true);
    try {
      await setGithubToken(cleanHost, token.trim());
      // Validates the token against this host and surfaces a bad token (or wrong Enterprise
      // host) immediately, rather than silently saving something that only fails later.
      const login = await githubAuthenticatedUser(cleanHost);
      const next = [
        ...connections.filter((c) => c.host.toLowerCase() !== cleanHost),
        { host: cleanHost, username: login },
      ];
      await saveGithubConnections(next);
      setConnections(next);
      setHost(GITHUB_COM);
      setToken("");
      useToastStore.getState().pushToast(t("toast.githubConnected", { user: login }), "success");
    } catch (e) {
      // Roll back the token we just wrote so a failed validation doesn't leave a broken
      // connection behind.
      await deleteGithubToken(cleanHost).catch(() => {});
      pushErrorToast(t("toast.githubSaveFailed", { error: String(e) }));
    } finally {
      setSaving(false);
    }
  };

  const handleRemove = async (removeHost: string) => {
    try {
      await deleteGithubToken(removeHost);
      const next = connections.filter((c) => c.host.toLowerCase() !== removeHost.toLowerCase());
      await saveGithubConnections(next);
      setConnections(next);
      useToastStore.getState().pushToast(t("toast.githubRemoved"), "info");
    } catch (e) {
      pushErrorToast(t("toast.githubRemoveFailed", { error: String(e) }));
    }
  };

  if (!loaded) return null;

  return (
    <section>
      {/* Same as the Azure form: the row above is the heading now. */}
      <p className="mb-3 text-relaxed text-[var(--cf-text-muted)]">{t("settings.githubHint")}</p>

      {connections.length > 0 && (
        <div className="mb-3 space-y-2">
          {connections.map((conn) => (
            <div key={conn.host} className="flex items-center gap-3 rounded-lg border border-[var(--cf-border)] p-3">
              <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-md bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]">
                <KeyRound size={15} />
              </span>
              <div className="min-w-0 flex-1">
                <p className="truncate text-body font-medium">{githubHostLabel(conn.host)}</p>
                <p className="truncate text-ui text-[var(--cf-text-muted)]">
                  {conn.username ? `@${conn.username}` : "••••••••••••"}
                </p>
              </div>
              <span className="flex shrink-0 items-center gap-1 rounded-full bg-[color-mix(in_oklab,var(--cf-success)_16%,transparent)] px-2 py-0.5 text-badge font-medium text-[var(--cf-success)]">
                <Check size={11} /> {t("settings.connected")}
              </span>
              <Button
                variant="danger"
                size="sm"
                icon={Trash2}
                className="shrink-0"
                onClick={() => handleRemove(conn.host)}
              >
                {t("settings.remove")}
              </Button>
            </div>
          ))}
        </div>
      )}

      <div className="space-y-2">
        <Field label={t("settings.githubHostLabel")} hint={t("settings.githubHostHint")}>
          {(field) => (
            <input
              {...field}
              value={host}
              onChange={(e) => setHost(e.target.value)}
              placeholder={GITHUB_COM}
              className={FIELD_INPUT}
            />
          )}
        </Field>
        <Field label={t("settings.personalAccessToken")}>
          {(field) => (
            <input
              {...field}
              type="password"
              value={token}
              onChange={(e) => setToken(e.target.value)}
              className={FIELD_INPUT}
            />
          )}
        </Field>

        <div className="pt-1">
          <Button
            variant="primary"
            icon={KeyRound}
            pending={saving}
            disabled={!host.trim() || !token.trim()}
            onClick={handleSave}
          >
            {saving ? t("settings.savingToken") : t("settings.saveToken")}
          </Button>
        </div>
      </div>
    </section>
  );
}
