import { useState } from "react";
import { Plus, Radio, Send, Settings2, Trash2 } from "lucide-react";
import { Button } from "../../common/Button";
import { IconButton } from "../../common/IconButton";
import { CollapsibleSection } from "../../common/CollapsibleSection";
import { Select } from "../../common/Select";
import { useApiTabsStore } from "../../../state/apiTabsStore";
import { useApiRuntimeStore } from "../../../state/apiRuntimeStore";
import { useT } from "../../../state/languageStore";
import { pushErrorToast } from "../../../state/toastStore";
import { apiSocketioEmit } from "../../../lib/ipc/apiCommands";
import { INPUT, isJson, JsonEditor, Transcript } from "./shared";
import { LabeledField } from "../LabeledField";

/** Renders exactly what `StreamPanel` shows for the `socketio` protocol: the settings section,
 * the listeners section, the shared transcript, and the composer. */
export function SocketIoPanel({ tabId }: { tabId: string }) {
  const t = useT();
  const connection = useApiRuntimeStore((s) => s.connections[tabId] ?? null);
  const status = connection?.status ?? "closed";
  const open = status === "open";

  return (
    <>
      <div className="shrink-0 border-b border-[var(--cf-border)] px-3 py-2">
        <CollapsibleSection icon={Settings2} title={t("api.tab.settings")} defaultOpen>
          <SocketIoSettings tabId={tabId} locked={connection !== null} />
        </CollapsibleSection>
      </div>

      <div className="shrink-0 border-b border-[var(--cf-border)] px-3 py-2">
        <CollapsibleSection icon={Radio} title={t("api.socketio.listeners")} defaultOpen>
          <SocketIoListeners tabId={tabId} />
        </CollapsibleSection>
      </div>

      <Transcript tabId={tabId} />

      <div className="shrink-0 border-t border-[var(--cf-border)] px-3 py-2">
        <SocketIoComposer tabId={tabId} connectionId={open ? (connection?.id ?? null) : null} />
      </div>
    </>
  );
}

function SocketIoSettings({ tabId, locked }: { tabId: string; locked: boolean }) {
  const t = useT();
  const settings = useApiTabsStore((s) => s.openTabs.find((tab) => tab.id === tabId)?.draft.socketio);
  const updateDraft = useApiTabsStore((s) => s.updateDraft);
  if (!settings) return <></>;

  return (
    <div className="grid grid-cols-3 gap-2">
      <LabeledField label={t("api.socketio.path")}>
        <input
          value={settings.path}
          disabled={locked}
          onChange={(e) => updateDraft(tabId, { socketio: { ...settings, path: e.target.value } })}
          className={INPUT}
        />
      </LabeledField>
      <LabeledField label={t("api.socketio.namespace")}>
        <input
          value={settings.namespace}
          disabled={locked}
          onChange={(e) => updateDraft(tabId, { socketio: { ...settings, namespace: e.target.value } })}
          className={INPUT}
        />
      </LabeledField>
      <LabeledField label={t("api.socketio.version")}>
        <Select
          size="sm"
          disabled={locked}
          value={settings.version}
          onChange={(value) =>
            updateDraft(tabId, { socketio: { ...settings, version: value as typeof settings.version } })
          }
          options={[
            { value: "v4", label: "v4 · Socket.IO 3 / 4" },
            { value: "v3", label: "v3 · Socket.IO 2" },
          ]}
        />
      </LabeledField>
      <div className="col-span-3">
        <LabeledField label={t("api.socketio.handshakeAuth")}>
          <input
            value={settings.authJson}
            disabled={locked}
            onChange={(e) => updateDraft(tabId, { socketio: { ...settings, authJson: e.target.value } })}
            placeholder='{"token":"{{authToken}}"}'
            className={`${INPUT} font-mono ${
              isJson(settings.authJson) ? "" : "border-[var(--cf-danger)]"
            }`}
          />
        </LabeledField>
      </div>
    </div>
  );
}

function SocketIoListeners({ tabId }: { tabId: string }) {
  const t = useT();
  const settings = useApiTabsStore((s) => s.openTabs.find((tab) => tab.id === tabId)?.draft.socketio);
  const updateDraft = useApiTabsStore((s) => s.updateDraft);
  const [draftName, setDraftName] = useState("");
  if (!settings) return <></>;

  const setListeners = (listeners: string[]) => updateDraft(tabId, { socketio: { ...settings, listeners } });

  const add = () => {
    const name = draftName.trim();
    if (!name || settings.listeners.includes(name)) return;
    setListeners([...settings.listeners, name]);
    setDraftName("");
  };

  return (
    <div className="space-y-1.5">
      {settings.listeners.length === 0 ? (
        <p className="text-badge text-[var(--cf-text-muted)]">{t("api.socketio.listenAll")}</p>
      ) : (
        <div className="flex flex-wrap gap-1.5">
          {settings.listeners.map((name) => (
            <span
              key={name}
              className="flex items-center gap-1 rounded bg-[var(--cf-accent-soft)] py-0.5 pl-2 pr-0.5 font-mono text-badge text-[var(--cf-accent)]"
            >
              {name}
              <IconButton
                label="api.socketio.removeListener"
                icon={Trash2}
                variant="danger"
                onClick={() => setListeners(settings.listeners.filter((other) => other !== name))}
              />
            </span>
          ))}
        </div>
      )}

      <div className="flex items-center gap-1.5">
        <input
          value={draftName}
          onChange={(e) => setDraftName(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") add();
          }}
          placeholder={t("api.socketio.event")}
          className={`${INPUT} font-mono`}
        />
        <Button variant="secondary" size="sm" icon={Plus} className="shrink-0" onClick={add}>
          {t("api.socketio.addListener")}
        </Button>
      </div>
      <p className="text-badge leading-snug text-[var(--cf-text-muted)]">{t("api.socketio.listenerHint")}</p>
    </div>
  );
}

function SocketIoComposer({ tabId, connectionId }: { tabId: string; connectionId: string | null }) {
  const t = useT();
  const settings = useApiTabsStore((s) => s.openTabs.find((tab) => tab.id === tabId)?.draft.socketio);
  const updateDraft = useApiTabsStore((s) => s.updateDraft);
  const appendMessage = useApiRuntimeStore((s) => s.appendMessage);
  if (!settings) return <></>;

  const jsonError = !isJson(settings.draftPayload);

  const emit = async () => {
    if (!connectionId) return;
    try {
      await apiSocketioEmit(connectionId, settings.draftEvent, settings.draftPayload);
      appendMessage(tabId, {
        connection_id: connectionId,
        direction: "sent",
        channel: settings.draftEvent,
        payload: settings.draftPayload,
        binary: false,
        at: Date.now(),
      });
    } catch (e) {
      pushErrorToast(String(e));
    }
  };

  return (
    <div className="space-y-1.5">
      <div className="flex items-center gap-2">
        <input
          value={settings.draftEvent}
          onChange={(e) => updateDraft(tabId, { socketio: { ...settings, draftEvent: e.target.value } })}
          placeholder={t("api.socketio.event")}
          className={`${INPUT} w-48 font-mono`}
        />
        {jsonError && <span className="text-badge text-[var(--cf-danger)]">{t("api.ws.invalidJson")}</span>}
        <div className="flex-1" />
        <Button
          variant="primary"
          size="sm"
          icon={Send}
          disabled={!connectionId || jsonError || settings.draftEvent.trim() === ""}
          onClick={() => void emit()}
        >
          {t("api.socketio.emit")}
        </Button>
      </div>
      <JsonEditor
        value={settings.draftPayload}
        onChange={(value) => updateDraft(tabId, { socketio: { ...settings, draftPayload: value } })}
      />
    </div>
  );
}
