import { Plus, Radio, Send, Settings2, Trash2 } from "lucide-react";
import { Button } from "../../common/Button";
import { IconButton } from "../../common/IconButton";
import { Checkbox } from "../../common/Checkbox";
import { CollapsibleSection } from "../../common/CollapsibleSection";
import { Select } from "../../common/Select";
import { useApiTabsStore } from "../../../state/apiTabsStore";
import { useApiRuntimeStore } from "../../../state/apiRuntimeStore";
import { useT } from "../../../state/languageStore";
import { pushErrorToast } from "../../../state/toastStore";
import { apiMqttPublish, apiMqttSubscribe, apiMqttUnsubscribe } from "../../../lib/ipc/apiCommands";
import type { MqttSubscription } from "../../../types/api";
import { INPUT, Transcript, toInt } from "./shared";
import { LabeledField } from "../LabeledField";

/** Renders exactly what `StreamPanel` shows for the `mqtt` protocol: the settings section,
 * the subscriptions section, the shared transcript, and the composer. */
export function MqttPanel({ tabId }: { tabId: string }) {
  const t = useT();
  const connection = useApiRuntimeStore((s) => s.connections[tabId] ?? null);
  const status = connection?.status ?? "closed";
  const open = status === "open";

  return (
    <>
      <div className="shrink-0 border-b border-[var(--cf-border)] px-3 py-2">
        <CollapsibleSection icon={Settings2} title={t("api.tab.settings")} defaultOpen>
          <MqttSettings tabId={tabId} locked={connection !== null} />
        </CollapsibleSection>
      </div>

      <div className="shrink-0 border-b border-[var(--cf-border)] px-3 py-2">
        <CollapsibleSection icon={Radio} title={t("api.tab.subscriptions")} defaultOpen>
          <MqttSubscriptions tabId={tabId} connectionId={open ? (connection?.id ?? null) : null} />
        </CollapsibleSection>
      </div>

      <Transcript tabId={tabId} />

      <div className="shrink-0 border-t border-[var(--cf-border)] px-3 py-2">
        <MqttComposer tabId={tabId} connectionId={open ? (connection?.id ?? null) : null} />
      </div>
    </>
  );
}

const QOS_OPTIONS = [
  { value: "0", label: "0" },
  { value: "1", label: "1" },
  { value: "2", label: "2" },
];

function toQos(value: string): 0 | 1 | 2 {
  return value === "1" ? 1 : value === "2" ? 2 : 0;
}

function MqttSettings({ tabId, locked }: { tabId: string; locked: boolean }) {
  const t = useT();
  const settings = useApiTabsStore((s) => s.openTabs.find((tab) => tab.id === tabId)?.draft.mqtt);
  const updateDraft = useApiTabsStore((s) => s.updateDraft);
  if (!settings) return <></>;

  const patch = (next: Partial<typeof settings>) => updateDraft(tabId, { mqtt: { ...settings, ...next } });

  return (
    <div className="space-y-2">
      <div className="grid grid-cols-4 gap-2">
        <LabeledField label={t("api.mqtt.clientId")}>
          <input
            value={settings.clientId}
            disabled={locked}
            onChange={(e) => patch({ clientId: e.target.value })}
            className={`${INPUT} font-mono`}
          />
        </LabeledField>
        <LabeledField label={t("api.mqtt.keepAlive")}>
          <input
            type="number"
            min={0}
            value={settings.keepAlive}
            disabled={locked}
            onChange={(e) => patch({ keepAlive: toInt(e.target.value, 60) })}
            className={INPUT}
          />
        </LabeledField>
        <LabeledField label={t("api.mqtt.version")}>
          <Select
            size="sm"
            disabled={locked}
            value={settings.version}
            onChange={(value) => patch({ version: value as typeof settings.version })}
            options={[
              { value: "3.1.1", label: "3.1.1" },
              { value: "5.0", label: "5.0" },
            ]}
          />
        </LabeledField>
        <div className="flex items-end pb-1">
          <label className="flex cursor-pointer items-center gap-1.5 py-1 text-ui text-[var(--cf-text)]">
            <Checkbox
              checked={settings.cleanSession}
              disabled={locked}
              onChange={(checked) => patch({ cleanSession: checked })}
            />
            {t("api.mqtt.cleanSession")}
          </label>
        </div>
      </div>

      <div className="grid grid-cols-2 gap-2">
        <LabeledField label={t("api.auth.username")}>
          <input
            value={settings.username}
            disabled={locked}
            onChange={(e) => patch({ username: e.target.value })}
            className={INPUT}
          />
        </LabeledField>
        <LabeledField label={t("api.auth.password")}>
          <input
            type="password"
            value={settings.password}
            disabled={locked}
            onChange={(e) => patch({ password: e.target.value })}
            className={INPUT}
          />
        </LabeledField>
      </div>

      <div>
        <p className="mb-1 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
          {t("api.mqtt.lastWill")}
        </p>
        <div className="grid grid-cols-4 gap-2">
          <LabeledField label={t("api.mqtt.topic")}>
            <input
              value={settings.lastWill.topic}
              disabled={locked}
              onChange={(e) => patch({ lastWill: { ...settings.lastWill, topic: e.target.value } })}
              className={`${INPUT} font-mono`}
            />
          </LabeledField>
          <div className="col-span-2">
            <LabeledField label={t("api.mqtt.payload")}>
              <input
                value={settings.lastWill.payload}
                disabled={locked}
                onChange={(e) => patch({ lastWill: { ...settings.lastWill, payload: e.target.value } })}
                className={`${INPUT} font-mono`}
              />
            </LabeledField>
          </div>
          <div className="flex items-end gap-2 pb-0.5">
            <LabeledField label={t("api.mqtt.qos")}>
              <Select
                size="sm"
                disabled={locked}
                value={String(settings.lastWill.qos)}
                onChange={(value) => patch({ lastWill: { ...settings.lastWill, qos: toQos(value) } })}
                options={QOS_OPTIONS}
              />
            </LabeledField>
            <label className="flex cursor-pointer items-center gap-1.5 py-1 text-ui text-[var(--cf-text)]">
              <Checkbox
                checked={settings.lastWill.retain}
                disabled={locked}
                onChange={(checked) => patch({ lastWill: { ...settings.lastWill, retain: checked } })}
              />
              {t("api.mqtt.retain")}
            </label>
          </div>
        </div>
      </div>
    </div>
  );
}

function MqttSubscriptions({ tabId, connectionId }: { tabId: string; connectionId: string | null }) {
  const t = useT();
  const settings = useApiTabsStore((s) => s.openTabs.find((tab) => tab.id === tabId)?.draft.mqtt);
  const updateDraft = useApiTabsStore((s) => s.updateDraft);
  if (!settings) return <></>;

  const setRows = (subscriptions: MqttSubscription[]) =>
    updateDraft(tabId, { mqtt: { ...settings, subscriptions } });

  const patchRow = (id: string, next: Partial<MqttSubscription>) =>
    setRows(settings.subscriptions.map((row) => (row.id === id ? { ...row, ...next } : row)));

  /** Toggling a row while connected has to reach the broker too — the checkbox is the
   * subscription, not a note about one. */
  const applyEnabled = async (row: MqttSubscription, enabled: boolean) => {
    patchRow(row.id, { enabled });
    if (!connectionId || row.topic.trim() === "") return;
    try {
      if (enabled) await apiMqttSubscribe(connectionId, row.topic, row.qos);
      else await apiMqttUnsubscribe(connectionId, row.topic);
    } catch (e) {
      pushErrorToast(String(e));
    }
  };

  return (
    <div className="space-y-1">
      {settings.subscriptions.length === 0 && (
        <p className="text-badge text-[var(--cf-text-muted)]">{t("api.mqtt.noSubscriptions")}</p>
      )}

      {settings.subscriptions.map((row) => (
        <div key={row.id} className="flex items-center gap-1.5">
          <Checkbox checked={row.enabled} onChange={(checked) => void applyEnabled(row, checked)} />
          <input
            value={row.topic}
            onChange={(e) => patchRow(row.id, { topic: e.target.value })}
            placeholder="sensors/+/temperature"
            className={`${INPUT} font-mono`}
          />
          <div className="w-16 shrink-0">
            <Select
              size="sm"
              ariaLabel={t("api.mqtt.qos")}
              value={String(row.qos)}
              onChange={(value) => patchRow(row.id, { qos: toQos(value) })}
              options={QOS_OPTIONS}
            />
          </div>
          <Button
            variant="secondary"
            size="sm"
            className="shrink-0"
            disabled={!connectionId || row.topic.trim() === ""}
            onClick={() => void applyEnabled(row, true)}
          >
            {t("api.mqtt.subscribe")}
          </Button>
          <Button
            variant="secondary"
            size="sm"
            className="shrink-0"
            disabled={!connectionId || row.topic.trim() === ""}
            onClick={() => void applyEnabled(row, false)}
          >
            {t("api.mqtt.unsubscribe")}
          </Button>
          <IconButton
            label="api.removeRow"
            icon={Trash2}
            variant="danger"
            className="shrink-0"
            onClick={() => setRows(settings.subscriptions.filter((other) => other.id !== row.id))}
          />
        </div>
      ))}

      <Button
        variant="ghost"
        size="sm"
        icon={Plus}
        onClick={() =>
          setRows([
            ...settings.subscriptions,
            { id: newRowId(), topic: "", qos: 0, enabled: true },
          ])
        }
      >
        {t("api.mqtt.addSubscription")}
      </Button>
    </div>
  );
}

function MqttComposer({ tabId, connectionId }: { tabId: string; connectionId: string | null }) {
  const t = useT();
  const settings = useApiTabsStore((s) => s.openTabs.find((tab) => tab.id === tabId)?.draft.mqtt);
  const updateDraft = useApiTabsStore((s) => s.updateDraft);
  const appendMessage = useApiRuntimeStore((s) => s.appendMessage);
  if (!settings) return <></>;

  const patch = (next: Partial<typeof settings>) => updateDraft(tabId, { mqtt: { ...settings, ...next } });

  const publish = async () => {
    if (!connectionId) return;
    try {
      await apiMqttPublish(
        connectionId,
        settings.publishTopic,
        settings.publishPayload,
        settings.publishQos,
        settings.publishRetain,
      );
      appendMessage(tabId, {
        connection_id: connectionId,
        direction: "sent",
        channel: settings.publishTopic,
        payload: settings.publishPayload,
        binary: false,
        at: Date.now(),
        qos: settings.publishQos,
        retain: settings.publishRetain,
      });
    } catch (e) {
      pushErrorToast(String(e));
    }
  };

  return (
    <div className="space-y-1.5">
      <div className="flex items-center gap-2">
        <input
          value={settings.publishTopic}
          onChange={(e) => patch({ publishTopic: e.target.value })}
          placeholder={t("api.mqtt.topic")}
          className={`${INPUT} w-64 font-mono`}
        />
        <div className="w-16 shrink-0">
          <Select
            size="sm"
            ariaLabel={t("api.mqtt.qos")}
            value={String(settings.publishQos)}
            onChange={(value) => patch({ publishQos: toQos(value) })}
            options={QOS_OPTIONS}
          />
        </div>
        <label className="flex cursor-pointer items-center gap-1.5 py-1 text-ui text-[var(--cf-text)]">
          <Checkbox checked={settings.publishRetain} onChange={(checked) => patch({ publishRetain: checked })} />
          {t("api.mqtt.retain")}
        </label>
        <div className="flex-1" />
        <Button
          variant="primary"
          size="sm"
          icon={Send}
          disabled={!connectionId || settings.publishTopic.trim() === ""}
          onClick={() => void publish()}
        >
          {t("api.mqtt.publish")}
        </Button>
      </div>
      <textarea
        value={settings.publishPayload}
        onChange={(e) => patch({ publishPayload: e.target.value })}
        placeholder={t("api.mqtt.payload")}
        rows={3}
        className="w-full resize-none rounded-md border border-[var(--cf-border)] bg-transparent px-2 py-1.5 font-mono text-ui outline-none focus:border-[var(--cf-accent)]"
      />
    </div>
  );
}

function newRowId(): string {
  return `sub-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}
