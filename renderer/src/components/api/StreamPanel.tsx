import { useCallback, useEffect } from "react";
import { AlertTriangle, Plug, Unplug } from "lucide-react";
import { Button } from "../common/Button";
import { useApiSettingsStore } from "../../state/apiSettingsStore";
import { useApiCookieStore } from "../../state/apiCookieStore";
import { getAuthChainForTab, getVariableContext } from "../../state/apiStore";
import { useApiTabsStore } from "../../state/apiTabsStore";
import { useApiRuntimeStore } from "../../state/apiRuntimeStore";
import { useT } from "../../state/languageStore";
import { pushErrorToast } from "../../state/toastStore";
import { resolveRequest } from "../../lib/api/send";
import {
  apiMqttConnect,
  apiSocketioConnect,
  apiStreamDisconnect,
  apiWsConnect,
} from "../../lib/ipc/apiCommands";
import { StatusDot, statusLabel } from "./stream/shared";
import { WebSocketPanel } from "./stream/WebSocketPanel";
import { SocketIoPanel } from "./stream/SocketIoPanel";
import { MqttPanel } from "./stream/MqttPanel";

/**
 * The workbench for the three long-lived protocols — WebSocket, Socket.IO and MQTT.
 *
 * They share the same shape (open a connection, watch a transcript, send something into it), so
 * they share one component and branch on the draft's protocol for the settings row and the
 * composer. Splitting them into three would have meant three copies of the transcript, which is
 * where all the actual difficulty lives.
 *
 * Nothing here is fetched: the connection lives in `apiRuntimeStore` keyed by tab id, frames
 * arrive on the `api:stream-message` event that the runtime store already subscribes to, and the
 * only outgoing calls are the IPC transports.
 */

export function StreamPanel({ tabId }: { tabId: string }) {
  const t = useT();
  const protocol = useApiTabsStore((s) => s.openTabs.find((tab) => tab.id === tabId)?.draft.protocol);
  const url = useApiTabsStore((s) => s.openTabs.find((tab) => tab.id === tabId)?.draft.url ?? "");
  const connection = useApiRuntimeStore((s) => s.connections[tabId] ?? null);
  const initRuntime = useApiRuntimeStore((s) => s.init);

  useEffect(() => {
    initRuntime();
  }, [initRuntime]);

  // The runtime store's `disposeTab` runs from `closeTab`, which also drops the socket. This is
  // the safety net for every other way a tab can stop existing (a deleted request detaches it,
  // a future bulk-close): once nothing addresses the connection, nobody can ever close it.
  useEffect(() => {
    return () => {
      const stillOpen = useApiTabsStore.getState().openTabs.some((tab) => tab.id === tabId);
      if (stillOpen) return;
      const live = useApiRuntimeStore.getState().connections[tabId];
      if (!live) return;
      void apiStreamDisconnect(live.id).catch(() => {});
      useApiRuntimeStore.getState().closeConnection(tabId);
    };
  }, [tabId]);

  const status = connection?.status ?? "closed";

  // reqwest's MQTT transport speaks TCP only; a ws:// broker URL fails inside the backend with an
  // error the user can do nothing about, so the refusal happens here where it can be explained.
  const mqttOverWebsocket = protocol === "mqtt" && /^wss?:\/\//i.test(url.trim());

  const connect = useCallback(async () => {
    const tab = useApiTabsStore.getState().openTabs.find((x) => x.id === tabId);
    if (!tab) return;

    const runtime = useApiRuntimeStore.getState();
    const connectionId = `${tabId}:${Date.now().toString(36)}`;
    try {
      const resolved = await resolveRequest(
        tab.draft,
        getVariableContext(tab.collectionId),
        getAuthChainForTab(tabId),
        useApiSettingsStore.getState().settings,
        useApiCookieStore.getState().cookies,
      );
      // Registered before the invoke, not after: the backend can emit `connecting`/`error`
      // while the call is still in flight, and an event whose id maps to no tab is dropped.
      runtime.openConnection(tabId, connectionId);

      switch (tab.draft.protocol) {
        case "websocket": {
          const ws = tab.draft.websocket;
          await apiWsConnect(connectionId, {
            url: resolved.url,
            headers: resolved.headers,
            subprotocols: splitList(ws.subprotocols),
            ping_interval_ms: ws.pingIntervalMs,
            options: resolved.options,
          });
          break;
        }
        case "socketio": {
          const io = tab.draft.socketio;
          await apiSocketioConnect(connectionId, {
            url: resolved.url,
            path: io.path,
            namespace: io.namespace,
            version: io.version,
            headers: resolved.headers,
            auth_json: io.authJson.trim() || "{}",
            // Left empty on purpose: the Params table has already been folded into `resolved.url`
            // by `resolveRequest`, and the backend appends both to the same handshake query
            // string — passing them here as well would duplicate every parameter.
            query: [],
            options: resolved.options,
          });
          break;
        }
        case "mqtt": {
          const mqtt = tab.draft.mqtt;
          await apiMqttConnect(connectionId, {
            url: resolved.url,
            client_id: mqtt.clientId,
            username: mqtt.username,
            password: mqtt.password,
            keep_alive_secs: mqtt.keepAlive,
            clean_session: mqtt.cleanSession,
            version: mqtt.version,
            last_will: mqtt.lastWill.topic.trim()
              ? {
                  topic: mqtt.lastWill.topic,
                  payload: mqtt.lastWill.payload,
                  qos: mqtt.lastWill.qos,
                  retain: mqtt.lastWill.retain,
                }
              : null,
            subscriptions: mqtt.subscriptions
              .filter((row) => row.enabled && row.topic.trim() !== "")
              .map((row) => ({ topic: row.topic, qos: row.qos })),
            options: resolved.options,
          });
          break;
        }
        default:
          runtime.closeConnection(tabId);
          return;
      }
    } catch (e) {
      runtime.closeConnection(tabId);
      pushErrorToast(t("api.toast.connectFailed", { error: String(e) }));
    }
  }, [tabId, t]);

  const disconnect = useCallback(async () => {
    const live = useApiRuntimeStore.getState().connections[tabId];
    if (!live) return;
    try {
      await apiStreamDisconnect(live.id);
    } catch {
      // A socket the backend has already forgotten is still a socket that is closed.
    }
    useApiRuntimeStore.getState().closeConnection(tabId);
  }, [tabId]);

  if (protocol !== "websocket" && protocol !== "socketio" && protocol !== "mqtt") return <></>;

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex shrink-0 items-center gap-2 border-b border-[var(--cf-border)] px-3 py-2">
        <StatusDot status={status} />
        <span className="text-ui font-medium text-[var(--cf-text)]">{statusLabel(status, t)}</span>
        {connection?.detail && (
          <span className="min-w-0 truncate text-badge text-[var(--cf-text-muted)]">
            {connection.detail}
          </span>
        )}
        <div className="flex-1" />
        {connection ? (
          <Button variant="secondary" size="sm" icon={Unplug} onClick={() => void disconnect()}>
            {t("api.disconnect")}
          </Button>
        ) : (
          <Button
            variant="primary"
            size="sm"
            icon={Plug}
            disabled={mqttOverWebsocket || url.trim() === ""}
            onClick={() => void connect()}
          >
            {t("api.connect")}
          </Button>
        )}
      </div>

      {mqttOverWebsocket && (
        <div className="flex shrink-0 items-start gap-2 border-b border-[var(--cf-border)] bg-[var(--cf-warning)]/10 px-3 py-1.5 text-badge text-[var(--cf-text)]">
          <AlertTriangle size={13} className="mt-px shrink-0 text-[var(--cf-warning)]" />
          {t("api.mqtt.wsUnsupported")}
        </div>
      )}

      {protocol === "websocket" && <WebSocketPanel tabId={tabId} />}
      {protocol === "socketio" && <SocketIoPanel tabId={tabId} />}
      {protocol === "mqtt" && <MqttPanel tabId={tabId} />}
    </div>
  );
}

function splitList(value: string): string[] {
  return value
    .split(",")
    .map((item) => item.trim())
    .filter((item) => item !== "");
}
