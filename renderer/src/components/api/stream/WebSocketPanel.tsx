import { Send, Settings2 } from "lucide-react";
import { Button } from "../../common/Button";
import { CollapsibleSection } from "../../common/CollapsibleSection";
import { Select } from "../../common/Select";
import { useApiTabsStore } from "../../../state/apiTabsStore";
import { useApiRuntimeStore } from "../../../state/apiRuntimeStore";
import { useT } from "../../../state/languageStore";
import { pushErrorToast } from "../../../state/toastStore";
import { apiWsSend } from "../../../lib/ipc/apiCommands";
import { INPUT, isJson, JsonEditor, Transcript, toInt } from "./shared";
import { LabeledField } from "../LabeledField";

/** Renders exactly what `StreamPanel` shows for the `websocket` protocol: the settings section,
 * the shared transcript, and the composer. */
export function WebSocketPanel({ tabId }: { tabId: string }) {
  const t = useT();
  const connection = useApiRuntimeStore((s) => s.connections[tabId] ?? null);
  const status = connection?.status ?? "closed";
  const open = status === "open";

  return (
    <>
      <div className="shrink-0 border-b border-[var(--cf-border)] px-3 py-2">
        <CollapsibleSection icon={Settings2} title={t("api.tab.settings")} defaultOpen>
          <WebsocketSettings tabId={tabId} locked={connection !== null} />
        </CollapsibleSection>
      </div>

      <Transcript tabId={tabId} />

      <div className="shrink-0 border-t border-[var(--cf-border)] px-3 py-2">
        <WebsocketComposer tabId={tabId} connectionId={open ? (connection?.id ?? null) : null} />
      </div>
    </>
  );
}

function WebsocketSettings({ tabId, locked }: { tabId: string; locked: boolean }) {
  const t = useT();
  const settings = useApiTabsStore((s) => s.openTabs.find((tab) => tab.id === tabId)?.draft.websocket);
  const updateDraft = useApiTabsStore((s) => s.updateDraft);
  if (!settings) return <></>;

  return (
    <div className="grid grid-cols-2 gap-2">
      <LabeledField label={t("api.ws.subprotocols")}>
        <input
          value={settings.subprotocols}
          disabled={locked}
          onChange={(e) => updateDraft(tabId, { websocket: { ...settings, subprotocols: e.target.value } })}
          placeholder="graphql-ws, json"
          className={INPUT}
        />
      </LabeledField>
      <LabeledField label={t("api.ws.pingInterval")}>
        <input
          type="number"
          min={0}
          value={settings.pingIntervalMs}
          disabled={locked}
          onChange={(e) =>
            updateDraft(tabId, { websocket: { ...settings, pingIntervalMs: toInt(e.target.value, 0) } })
          }
          className={INPUT}
        />
      </LabeledField>
    </div>
  );
}

function WebsocketComposer({ tabId, connectionId }: { tabId: string; connectionId: string | null }) {
  const t = useT();
  const settings = useApiTabsStore((s) => s.openTabs.find((tab) => tab.id === tabId)?.draft.websocket);
  const updateDraft = useApiTabsStore((s) => s.updateDraft);
  const appendMessage = useApiRuntimeStore((s) => s.appendMessage);
  if (!settings) return <></>;

  const jsonError = settings.messageFormat === "json" && !isJson(settings.draftMessage);

  const send = async () => {
    if (!connectionId) return;
    const binary = settings.messageFormat === "binary";
    try {
      await apiWsSend(connectionId, settings.draftMessage, binary);
      // The transports only report what arrives; without a local echo the log would show half
      // of the conversation.
      appendMessage(tabId, {
        connection_id: connectionId,
        direction: "sent",
        channel: "",
        payload: settings.draftMessage,
        binary,
        at: Date.now(),
      });
    } catch (e) {
      pushErrorToast(String(e));
    }
  };

  return (
    <div className="space-y-1.5">
      <div className="flex items-center gap-2">
        <span className="text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
          {t("api.ws.format")}
        </span>
        <div className="w-28 shrink-0">
          <Select
            size="sm"
            value={settings.messageFormat}
            onChange={(value) =>
              updateDraft(tabId, {
                websocket: { ...settings, messageFormat: value as typeof settings.messageFormat },
              })
            }
            options={[
              { value: "text", label: t("api.ws.formatText") },
              { value: "json", label: t("api.ws.formatJson") },
              { value: "binary", label: t("api.ws.formatBinary") },
            ]}
          />
        </div>
        {settings.messageFormat === "binary" && (
          <span className="text-badge text-[var(--cf-text-muted)]">{t("api.ws.binaryHint")}</span>
        )}
        {jsonError && <span className="text-badge text-[var(--cf-danger)]">{t("api.ws.invalidJson")}</span>}
        <div className="flex-1" />
        <Button
          variant="primary"
          size="sm"
          icon={Send}
          disabled={!connectionId || jsonError}
          onClick={() => void send()}
        >
          {t("api.ws.send")}
        </Button>
      </div>

      {settings.messageFormat === "json" ? (
        <JsonEditor
          value={settings.draftMessage}
          onChange={(value) => updateDraft(tabId, { websocket: { ...settings, draftMessage: value } })}
        />
      ) : (
        <textarea
          value={settings.draftMessage}
          onChange={(e) => updateDraft(tabId, { websocket: { ...settings, draftMessage: e.target.value } })}
          placeholder={t("api.ws.composePlaceholder")}
          rows={3}
          className="w-full resize-none rounded-md border border-[var(--cf-border)] bg-transparent px-2 py-1.5 font-mono text-ui outline-none focus:border-[var(--cf-accent)]"
        />
      )}
    </div>
  );
}
