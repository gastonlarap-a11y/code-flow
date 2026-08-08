import { useEffect, useMemo, useRef, useState } from "react";
import { Editor } from "../../../lib/monacoEditor";
import {
  AlertTriangle,
  ArrowDown,
  ArrowDownToLine,
  ArrowUp,
  ChevronDown,
  ChevronRight,
  Copy,
  Eraser,
  Info,
} from "lucide-react";
import { Button } from "../../common/Button";
import { Checkbox } from "../../common/Checkbox";
import { IconButton } from "../../common/IconButton";
import { useApiTabsStore } from "../../../state/apiTabsStore";
import { useApiRuntimeStore } from "../../../state/apiRuntimeStore";
import { useThemeStore } from "../../../state/themeStore";
import { useT } from "../../../state/languageStore";
import type { StreamMessage } from "../../../types/api";

type Translate = ReturnType<typeof useT>;

/** Frames actually rendered. `apiRuntimeStore` keeps 2000 per tab; a subscription to `#` would
 * otherwise put 2000 DOM subtrees on the page and make scrolling unusable. */
const RENDER_WINDOW = 300;
const BINARY_PREVIEW_BYTES = 512;
/** Payloads longer than this start collapsed, so one chatty frame can't own the whole viewport. */
const COLLAPSE_THRESHOLD = 140;
/** How close to the bottom still counts as "following the log". */
const STICK_TO_BOTTOM_PX = 24;

const NO_MESSAGES: StreamMessage[] = [];

export const INPUT =
  "w-full rounded-md border border-[var(--cf-border)] bg-transparent px-2 py-1 text-ui outline-none focus:border-[var(--cf-accent)] disabled:opacity-50";

// ---------------------------------------------------------------------------
// Connection bar
// ---------------------------------------------------------------------------

export function StatusDot({ status }: { status: "connecting" | "open" | "closed" | "error" }) {
  const color =
    status === "open"
      ? "var(--cf-success)"
      : status === "connecting"
        ? "var(--cf-warning)"
        : status === "error"
          ? "var(--cf-danger)"
          : "var(--cf-text-muted)";
  return (
    <span
      className={`h-2 w-2 shrink-0 rounded-full ${status === "connecting" ? "animate-pulse" : ""}`}
      style={{ backgroundColor: color }}
    />
  );
}

export function statusLabel(status: "connecting" | "open" | "closed" | "error", t: Translate): string {
  switch (status) {
    case "connecting":
      return t("api.connecting");
    case "open":
      return t("api.connected");
    case "error":
      return t("api.ws.error");
    case "closed":
      return t("api.disconnected");
  }
}

// ---------------------------------------------------------------------------
// Transcript
// ---------------------------------------------------------------------------

export function Transcript({ tabId }: { tabId: string }) {
  const t = useT();
  const messages = useApiRuntimeStore((s) => s.messages[tabId] ?? NO_MESSAGES);
  const clearMessages = useApiRuntimeStore((s) => s.clearMessages);
  const listeners = useApiTabsStore((s) => s.openTabs.find((tab) => tab.id === tabId)?.draft.socketio.listeners,
  );
  const isSocketIo = useApiTabsStore((s) => s.openTabs.find((tab) => tab.id === tabId)?.draft.protocol === "socketio",
  );

  const [filter, setFilter] = useState("");
  const [autoScroll, setAutoScroll] = useState(true);
  const scrollRef = useRef<HTMLDivElement>(null);

  const filtered = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    // The Socket.IO transport has no per-event subscribe call, so the listener list can only be
    // what it says on the label: what this transcript shows. `[]` means "everything".
    const wanted = isSocketIo && listeners && listeners.length > 0 ? new Set(listeners) : null;
    // The frame's position in the untrimmed transcript comes along as its React key: the store
    // only ever appends, so that index names one frame for as long as it exists, while a key
    // based on the position in the rendered window would move under a row every time the
    // window slides — taking its expanded/hex state with it.
    const kept: { message: StreamMessage; index: number }[] = [];
    messages.forEach((message, index) => {
      if (wanted && message.direction === "received" && !wanted.has(message.channel)) return;
      if (
        needle &&
        !message.channel.toLowerCase().includes(needle) &&
        !message.payload.toLowerCase().includes(needle)
      ) {
        return;
      }
      kept.push({ message, index });
    });
    return kept;
  }, [messages, filter, listeners, isSocketIo]);

  const visible = filtered.length > RENDER_WINDOW ? filtered.slice(-RENDER_WINDOW) : filtered;

  useEffect(() => {
    if (!autoScroll) return;
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [visible.length, autoScroll]);

  // Position *is* the toggle: scrolling away turns following off, scrolling back turns it on.
  // Two independent notions of "am I following the log" would only ever disagree.
  const onScroll = () => {
    const el = scrollRef.current;
    if (!el) return;
    setAutoScroll(el.scrollHeight - el.scrollTop - el.clientHeight < STICK_TO_BOTTOM_PX);
  };

  const jumpToLatest = () => {
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
    setAutoScroll(true);
  };

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="flex shrink-0 items-center gap-2 px-3 py-1.5">
        <span className="text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
          {t("api.ws.messages")}
        </span>
        <input
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          placeholder={t("api.ws.filterPlaceholder")}
          className="w-48 rounded-md border border-[var(--cf-border)] bg-transparent px-2 py-0.5 text-badge outline-none focus:border-[var(--cf-accent)]"
        />
        <div className="flex-1" />
        <label className="flex cursor-pointer items-center gap-1.5 text-badge text-[var(--cf-text-muted)]">
          <Checkbox checked={autoScroll} onChange={(next) => (next ? jumpToLatest() : setAutoScroll(false))} />
          {t("api.ws.autoScroll")}
        </label>
        {/* `Eraser`, not `Trash2`: this empties a live transcript, and `Trash2` is reserved for
            deleting something that was stored (icon dictionary, §II.3). */}
        <IconButton label="api.ws.clear" icon={Eraser} onClick={() => clearMessages(tabId)} />
      </div>

      {filtered.length > visible.length && (
        <div className="shrink-0 px-3 pb-1 text-badge text-[var(--cf-text-muted)]">
          {t("api.ws.windowed", { shown: visible.length, total: filtered.length })}
        </div>
      )}

      <div className="relative min-h-0 flex-1">
        <div ref={scrollRef} onScroll={onScroll} className="h-full overflow-y-auto px-1 pb-1">
          {visible.length === 0 ? (
            <p className="px-2 py-6 text-center text-ui text-[var(--cf-text-muted)]">
              {messages.length === 0 ? t("api.ws.noMessages") : t("api.ws.noMatches")}
            </p>
          ) : (
            visible.map((entry) => <MessageRow key={entry.index} message={entry.message} t={t} />)
          )}
        </div>

        {!autoScroll && visible.length > 0 && (
          <Button
            variant="secondary"
            size="sm"
            icon={ArrowDownToLine}
            className="absolute bottom-3 left-1/2 -translate-x-1/2 shadow-[var(--cf-shadow)]"
            onClick={jumpToLatest}
          >
            {t("api.ws.jumpToLatest")}
          </Button>
        )}
      </div>
    </div>
  );
}

function MessageRow({ message, t }: { message: StreamMessage; t: Translate }) {
  const rendered = useMemo(() => renderPayload(message), [message]);
  const [expanded, setExpanded] = useState(false);
  const [asHex, setAsHex] = useState(true);

  const collapsible = rendered.kind === "binary" || rendered.text.length > COLLAPSE_THRESHOLD || rendered.text.includes("\n");
  const showFull = expanded || !collapsible;

  const body =
    rendered.kind === "binary"
      ? asHex
        ? rendered.hex
        : rendered.base64
      : showFull
        ? rendered.text
        : firstLine(rendered.text);

  return (
    <div className="group flex items-start gap-2 rounded-md px-2 py-1 hover:bg-black/[0.03] dark:hover:bg-white/[0.04]">
      <DirectionIcon direction={message.direction} t={t} />
      <span className="mt-px shrink-0 font-mono text-badge tabular-nums text-[var(--cf-text-muted)]">
        {formatTime(message.at)}
      </span>

      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-1.5">
          {message.channel && (
            <span className="rounded bg-[var(--cf-accent-soft)] px-1 py-px font-mono text-badge text-[var(--cf-accent)]">
              {message.channel}
            </span>
          )}
          {rendered.kind === "json" && (
            <span className="rounded border border-[var(--cf-border)] px-1 py-px text-badge uppercase tracking-wide text-[var(--cf-text-muted)]">
              {t("api.ws.formatJson")}
            </span>
          )}
          {rendered.kind === "binary" && (
            <>
              <span className="rounded border border-[var(--cf-border)] px-1 py-px text-badge uppercase tracking-wide text-[var(--cf-text-muted)]">
                {t("api.ws.bytes", { n: rendered.size })}
              </span>
              <Button variant="secondary" size="sm" onClick={() => setAsHex((v) => !v)}>
                {asHex ? t("api.ws.hex") : t("api.ws.base64")}
              </Button>
            </>
          )}
          {message.qos !== undefined && (
            <span className="text-badge uppercase tracking-wide text-[var(--cf-text-muted)]">
              {t("api.mqtt.qos")} {message.qos}
              {message.retain ? ` · ${t("api.mqtt.retain")}` : ""}
            </span>
          )}
        </div>

        {collapsible ? (
          <button
            onClick={() => setExpanded((v) => !v)}
            className="mt-0.5 flex w-full items-start gap-1 text-left"
          >
            {expanded ? (
              <ChevronDown size={12} className="mt-0.5 shrink-0 text-[var(--cf-text-muted)]" />
            ) : (
              <ChevronRight size={12} className="mt-0.5 shrink-0 text-[var(--cf-text-muted)]" />
            )}
            <pre
              className={`min-w-0 flex-1 font-mono text-badge leading-snug text-[var(--cf-text)] ${
                showFull ? "whitespace-pre-wrap break-all" : "truncate"
              }`}
            >
              {body}
            </pre>
          </button>
        ) : (
          <pre className="mt-0.5 whitespace-pre-wrap break-all font-mono text-badge leading-snug text-[var(--cf-text)]">
            {body}
          </pre>
        )}
      </div>

      {/* Dimmed, never hidden: `opacity-0` is what made this action impossible to find by keyboard,
          by touch, or by looking — the same fix `RowActions` applies to its trigger. */}
      <IconButton
        label="api.ws.copyMessage"
        icon={Copy}
        className="shrink-0 opacity-55 group-hover:opacity-100 group-focus-within:opacity-100"
        onClick={() => void navigator.clipboard.writeText(message.payload)}
      />
    </div>
  );
}

function DirectionIcon({ direction, t }: { direction: StreamMessage["direction"]; t: Translate }) {
  switch (direction) {
    case "sent":
      return <ArrowUp size={13} className="mt-px shrink-0 text-[var(--cf-accent)]" aria-label={t("api.ws.sentAt")} />;
    case "received":
      return <ArrowDown size={13} className="mt-px shrink-0 text-[var(--cf-success)]" aria-label={t("api.ws.receivedAt")} />;
    case "error":
      return <AlertTriangle size={13} className="mt-px shrink-0 text-[var(--cf-danger)]" aria-label={t("api.ws.error")} />;
    case "system":
      return <Info size={13} className="mt-px shrink-0 text-[var(--cf-text-muted)]" aria-label={t("api.ws.system")} />;
  }
}

// ---------------------------------------------------------------------------
// Shared bits
// ---------------------------------------------------------------------------

export function JsonEditor({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const monacoTheme = useThemeStore((s) => s.monacoTheme);
  return (
    <div className="overflow-hidden rounded-md border border-[var(--cf-border)]">
      <Editor
        height="110px"
        language="json"
        value={value}
        theme={monacoTheme}
        onChange={(next) => onChange(next ?? "")}
        options={{
          minimap: { enabled: false },
          fontSize: 12,
          lineNumbers: "off",
          folding: false,
          wordWrap: "on",
          scrollBeyondLastLine: false,
          renderLineHighlight: "none",
          overviewRulerLanes: 0,
          automaticLayout: true,
          scrollbar: { verticalScrollbarSize: 8, horizontalScrollbarSize: 8 },
        }}
      />
    </div>
  );
}

type RenderedPayload =
  | { kind: "text" | "json"; text: string }
  | { kind: "binary"; hex: string; base64: string; size: number };

/** Pretty-prints JSON, and turns a base64 binary frame into a hex dump. Both are computed once
 * per message and memoised by the row — a 2000-frame transcript re-parsing on every keystroke in
 * the filter box is exactly the melt this window is meant to avoid. */
function renderPayload(message: StreamMessage): RenderedPayload {
  if (message.binary) {
    const bytes = decodeBase64(message.payload);
    if (bytes) {
      return { kind: "binary", hex: hexDump(bytes), base64: message.payload, size: bytes.length };
    }
    return { kind: "text", text: message.payload };
  }
  const trimmed = message.payload.trim();
  if (trimmed.startsWith("{") || trimmed.startsWith("[")) {
    try {
      return { kind: "json", text: JSON.stringify(JSON.parse(trimmed), null, 2) };
    } catch {
      // Not JSON after all — a payload that merely starts with a brace is still just text.
    }
  }
  return { kind: "text", text: message.payload };
}

function decodeBase64(payload: string): Uint8Array | null {
  try {
    const binary = atob(payload);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes;
  } catch {
    return null;
  }
}

/** `offset  hex  ascii`, capped — nobody reads past the first few lines of a 4 MB frame. */
function hexDump(bytes: Uint8Array): string {
  const slice = bytes.subarray(0, BINARY_PREVIEW_BYTES);
  const lines: string[] = [];
  for (let offset = 0; offset < slice.length; offset += 16) {
    const chunk = slice.subarray(offset, offset + 16);
    const hex = Array.from(chunk, (byte) => byte.toString(16).padStart(2, "0")).join(" ");
    const ascii = Array.from(chunk, (byte) =>
      byte >= 32 && byte < 127 ? String.fromCharCode(byte) : ".",
    ).join("");
    lines.push(`${offset.toString(16).padStart(8, "0")}  ${hex.padEnd(47, " ")}  ${ascii}`);
  }
  if (bytes.length > slice.length) lines.push("…");
  return lines.join("\n");
}

function firstLine(text: string): string {
  const index = text.indexOf("\n");
  return index < 0 ? text : text.slice(0, index);
}

function formatTime(at: number): string {
  const date = new Date(at);
  const pad = (value: number, width = 2) => String(value).padStart(width, "0");
  return `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}.${pad(date.getMilliseconds(), 3)}`;
}

export function isJson(text: string): boolean {
  if (text.trim() === "") return true;
  try {
    JSON.parse(text);
    return true;
  } catch {
    return false;
  }
}

export function toInt(value: string, fallback: number): number {
  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : fallback;
}
