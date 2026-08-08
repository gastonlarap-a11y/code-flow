import { useEffect, useId, type ReactNode } from "react";
import {
  Info,
  Network,
  Plus,
  Settings2,
  ShieldCheck,
  Trash2,
  Waypoints,
  type LucideIcon,
} from "lucide-react";
import { Button } from "../common/Button";
import { Checkbox } from "../common/Checkbox";
import { IconButton } from "../common/IconButton";
import { ApiModal, Field, Row } from "./ApiModal";
import { ensureApiStoreLoaded } from "../../state/apiStore";
import { useApiCookieStore } from "../../state/apiCookieStore";
import { useApiHistoryStore } from "../../state/apiHistoryStore";
import { useApiSettingsStore } from "../../state/apiSettingsStore";
import { confirmAction } from "../../state/confirmStore";
import { pushErrorToast, useToastStore } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import { apiPickFile } from "../../lib/ipc/apiCommands";
import type { ClientCert } from "../../types/api";

const CERT_EXTENSIONS = ["p12", "pfx", "pem", "crt", "cer"];
const KEY_EXTENSIONS = ["pem", "key"];

function newCertId(): string {
  return `cert-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

function Section({
  icon: Icon,
  title,
  children,
}: {
  icon: LucideIcon;
  title: string;
  children: ReactNode;
}) {
  return (
    <section className="mb-4">
      <h3 className="mb-1 flex items-center gap-1.5 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
        <Icon size={12} />
        {title}
      </h3>
      <div className="rounded-lg border border-[var(--cf-border)] px-3 py-2">{children}</div>
    </section>
  );
}

/** A path field with a native picker beside it, used by all four certificate slots. */
function PathField({
  id,
  ariaLabel,
  value,
  extensions,
  placeholder,
  onChange,
}: {
  /** Id of the input, when a separate `<label htmlFor>` names it. */
  id?: string;
  ariaLabel?: string;
  value: string;
  extensions: string[];
  placeholder: string;
  onChange: (value: string) => void;
}) {
  const t = useT();
  const browse = async () => {
    const path = await apiPickFile(extensions).catch((e: unknown) => {
      pushErrorToast(String(e));
      return null;
    });
    if (path) onChange(path);
  };
  return (
    <div className="flex items-center gap-1.5">
      <Field
        {...(id ? { id } : {})}
        {...(ariaLabel ? { ariaLabel } : {})}
        mono
        value={value}
        placeholder={placeholder}
        onChange={onChange}
      />
      <Button variant="ghost" size="sm" onClick={() => void browse()}>
        {t("api.settings.browse")}
      </Button>
    </div>
  );
}

export function ApiSettingsModal({ onClose }: { onClose: () => void }) {
  const t = useT();
  return (
    <ApiModal icon={Settings2} title={t("api.settings.title")} size="lg" onClose={onClose}>
      <div className="min-h-0 flex-1 overflow-auto p-4">
        <ApiSettingsBody />
      </div>
    </ApiModal>
  );
}

/**
 * The settings themselves, without the modal around them, so the `api` section of the main
 * Settings window shows exactly the same controls instead of a second copy that drifts.
 */
export function ApiSettingsBody() {
  const t = useT();
  const caCertId = useId();
  // Reachable from the Settings window before the API view has ever been opened, in which case
  // the store still holds its defaults — and writing one back would overwrite what's on disk.
  useEffect(() => {
    void ensureApiStoreLoaded();
  }, []);
  const settings = useApiSettingsStore((s) => s.settings);
  const updateSettings = useApiSettingsStore((s) => s.updateSettings);
  const clearHistory = useApiHistoryStore((s) => s.clearHistory);
  const clearCookies = useApiCookieStore((s) => s.clearCookies);
  const historyCount = useApiHistoryStore((s) => s.history.length);
  const cookieCount = useApiCookieStore((s) => s.cookies.length);
  const pushToast = useToastStore((s) => s.pushToast);

  const number = (value: string, fallback: number) => {
    const parsed = Number(value);
    return Number.isFinite(parsed) && parsed >= 0 ? Math.floor(parsed) : fallback;
  };

  const patchCert = (id: string, patch: Partial<ClientCert>) =>
    void updateSettings({
      clientCerts: settings.clientCerts.map((cert) => (cert.id === id ? { ...cert, ...patch } : cert)),
    });

  const addCert = () =>
    void updateSettings({
      clientCerts: [
        ...settings.clientCerts,
        { id: newCertId(), host: "", certPath: "", keyPath: "", passphrase: "" },
      ],
    });

  const removeCert = (id: string) =>
    void updateSettings({ clientCerts: settings.clientCerts.filter((cert) => cert.id !== id) });

  const wipeHistory = async () => {
    if (!(await confirmAction(t("api.settings.clearHistoryConfirm")))) return;
    await clearHistory();
  };

  const wipeCookies = async () => {
    if (!(await confirmAction(t("api.cookie.clearAllConfirm")))) return;
    await clearCookies();
    pushToast(t("api.toast.cookieCleared"), "success");
  };

  return (
    <>
    <Section icon={Network} title={t("api.settings.network")}>
      <Row label={t("api.settings.timeout")}>
        <Field
          type="number"
          value={String(settings.timeoutMs)}
          onChange={(value) => void updateSettings({ timeoutMs: number(value, settings.timeoutMs) })}
        />
      </Row>
      <Row label={t("api.settings.followRedirects")}>
        <Checkbox
          checked={settings.followRedirects}
          onChange={(followRedirects) => void updateSettings({ followRedirects })}
        />
      </Row>
      <Row label={t("api.settings.maxRedirects")}>
        <Field
          type="number"
          disabled={!settings.followRedirects}
          value={String(settings.maxRedirects)}
          onChange={(value) =>
            void updateSettings({ maxRedirects: number(value, settings.maxRedirects) })
          }
        />
      </Row>
      <Row label={t("api.settings.verifySsl")}>
        <Checkbox
          checked={settings.verifySsl}
          onChange={(verifySsl) => void updateSettings({ verifySsl })}
        />
      </Row>

      {/* Shown rather than hidden: the field exists in `ApiSettings` and in the per-request
          overrides, so leaving it out entirely would read as an oversight instead of a limit. */}
      <div className="flex items-center gap-3 py-1 opacity-60">
        <span className="min-w-0 flex-1">
          <span className="block text-ui text-[var(--cf-text)]">
            {t("api.settings.keepAuthOnRedirect")}
          </span>
          <span className="flex items-start gap-1 text-badge leading-snug text-[var(--cf-text-muted)]">
            <Info size={11} className="mt-[2px] shrink-0" />
            {t("api.settings.keepAuthUnavailable")}
          </span>
        </span>
        <span className="flex w-[180px] shrink-0 justify-end">
          <Checkbox checked={false} disabled onChange={() => {}} />
        </span>
      </div>

      <Row label={t("api.settings.sendCookies")}>
        <Checkbox
          checked={settings.sendCookies}
          onChange={(sendCookies) => void updateSettings({ sendCookies })}
        />
      </Row>
    </Section>

    <Section icon={Waypoints} title={t("api.settings.proxy")}>
      <Row label={t("api.settings.proxyEnabled")}>
        <Checkbox
          checked={settings.proxyEnabled}
          onChange={(proxyEnabled) => void updateSettings({ proxyEnabled })}
        />
      </Row>
      <Row label={t("api.settings.proxyUrl")}>
        <Field
          mono
          disabled={!settings.proxyEnabled}
          value={settings.proxyUrl}
          placeholder="http://127.0.0.1:8080"
          onChange={(proxyUrl) => void updateSettings({ proxyUrl })}
        />
      </Row>
    </Section>

    <Section icon={ShieldCheck} title={t("api.settings.certificates")}>
      <div className="mb-2">
        <label htmlFor={caCertId} className="mb-1 block text-badge font-medium text-[var(--cf-text-muted)]">
          {t("api.settings.caCert")}
        </label>
        <PathField
          id={caCertId}
          value={settings.caCertPath}
          extensions={["pem", "crt", "cer"]}
          placeholder="ca-bundle.pem"
          onChange={(caCertPath) => void updateSettings({ caCertPath })}
        />
      </div>

      <div className="mb-1 flex items-center">
        <span className="mr-auto text-badge font-medium text-[var(--cf-text-muted)]">
          {t("api.settings.clientCerts")}
        </span>
        <Button variant="ghost" size="sm" icon={Plus} onClick={addCert}>
          {t("api.settings.addCert")}
        </Button>
      </div>

      {settings.clientCerts.length === 0 ? (
        <p className="py-1 text-badge text-[var(--cf-text-muted)]">{t("api.settings.noCerts")}</p>
      ) : (
        settings.clientCerts.map((cert) => (
          <div key={cert.id} className="mb-2 rounded-md border border-[var(--cf-border)] p-2">
            <div className="mb-1.5 flex items-center gap-2">
              <Field
                mono
                value={cert.host}
                placeholder={t("api.settings.certHost")}
                ariaLabel={t("api.settings.certHost")}
                onChange={(host) => patchCert(cert.id, { host })}
              />
              <IconButton
                label="api.settings.removeCert"
                icon={Trash2}
                variant="danger"
                className="shrink-0"
                onClick={() => removeCert(cert.id)}
              />
            </div>
            <div className="mb-1.5">
              <PathField
                value={cert.certPath}
                extensions={CERT_EXTENSIONS}
                placeholder={t("api.settings.certFile")}
                ariaLabel={t("api.settings.certFile")}
                onChange={(certPath) => patchCert(cert.id, { certPath })}
              />
            </div>
            <div className="mb-1.5">
              <PathField
                value={cert.keyPath}
                extensions={KEY_EXTENSIONS}
                placeholder={t("api.settings.keyFile")}
                ariaLabel={t("api.settings.keyFile")}
                onChange={(keyPath) => patchCert(cert.id, { keyPath })}
              />
            </div>
            <Field
              type="password"
              value={cert.passphrase}
              placeholder={t("api.settings.passphrase")}
              ariaLabel={t("api.settings.passphrase")}
              onChange={(passphrase) => patchCert(cert.id, { passphrase })}
            />
          </div>
        ))
      )}
    </Section>

    <Section icon={Settings2} title={t("settings.general")}>
      <Row label={t("api.settings.maxResponse")}>
        <Field
          type="number"
          value={String(settings.maxResponseBytes)}
          onChange={(value) =>
            void updateSettings({ maxResponseBytes: number(value, settings.maxResponseBytes) })
          }
        />
      </Row>
      <Row label={t("api.settings.prettyPrint")}>
        <Checkbox
          checked={settings.prettyPrint}
          onChange={(prettyPrint) => void updateSettings({ prettyPrint })}
        />
      </Row>
      <Row label={t("api.settings.saveHistory")}>
        <Checkbox
          checked={settings.saveHistory}
          onChange={(saveHistory) => void updateSettings({ saveHistory })}
        />
      </Row>
      <Row label={t("api.settings.historyLimit")}>
        <Field
          type="number"
          disabled={!settings.saveHistory}
          value={String(settings.historyLimit)}
          onChange={(value) =>
            void updateSettings({ historyLimit: number(value, settings.historyLimit) })
          }
        />
      </Row>

      {/* Every control above is stored under the one global `api_settings` key; the two buttons
          below empty only the current workspace's history and jar — as do the counts that
          disable them. Saying so is cheaper than the support question. */}
      <div className="mt-2 border-t border-[var(--cf-border)] pt-2">
        <p className="mb-1.5 flex items-start gap-1 text-badge leading-snug text-[var(--cf-text-muted)]">
          <Info size={11} className="mt-[2px] shrink-0" />
          {t("api.settings.workspaceScope")}
        </p>
        <div className="flex items-center gap-2">
          <Button
            variant="danger"
            size="sm"
            icon={Trash2}
            onClick={() => void wipeHistory()}
            disabled={historyCount === 0}
          >
            {t("api.settings.clearHistory")}
          </Button>
          <Button
            variant="danger"
            size="sm"
            icon={Trash2}
            onClick={() => void wipeCookies()}
            disabled={cookieCount === 0}
          >
            {t("api.settings.clearCookies")}
          </Button>
        </div>
      </div>
    </Section>
    </>
  );
}
