import { Fragment, useEffect, useMemo, useRef, useState } from "react";
import { ArrowUp, Check, Copy, Sparkles } from "lucide-react";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import { Tooltip } from "../common/Tooltip";
import { StopSquare } from "../../lib/ui/icons";
import { renderMarkdown } from "../../lib/markdown";
import { parseClaudeError } from "../../lib/claudeError";
import { useUiStore } from "../../state/uiStore";
import { useChatStore, EMPTY_CHAT, type ChatMessage } from "../../state/chatStore";
import { useChatHistoryStore, EMPTY_CONVERSATIONS } from "../../state/activityStore";
import { useLanguageStore, useT } from "../../state/languageStore";
import { modelDisplayLabel, providerDisplayLabel } from "../../lib/aiProviders";
import { AiRunLog } from "./AiRunLog";
import { ChatModelPicker } from "./ChatModelPicker";
import { ChatAgentPicker } from "./ChatAgentPicker";
import { AiErrorBanner } from "./AiErrorBanner";
import { useCopy } from "../../lib/ui/useCopy";

const formatResponseTime = (ms: number) => (ms < 1000 ? `${Math.round(ms)}ms` : `${(ms / 1000).toFixed(1)}s`);

/** The app's own language decides how timestamps read, not the OS locale — otherwise a chat in a
 * Spanish UI would print English dates. */
const useLocale = () => (useLanguageStore((s) => s.language) === "es" ? "es-ES" : "en-US");

/** Parses a stored RFC 3339 stamp, tolerating the `undefined` of turns recorded before timestamps
 * were kept and the (theoretical) unparseable value rather than rendering "Invalid Date". */
function parseStamp(iso: string | undefined): Date | null {
  if (!iso) return null;
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** One muted 10px line under a turn: when it happened and, for an answer, what produced it —
 * engine, model, CLI version, and how long it took.
 *
 * Deliberately a single row rather than a chip or a header. The process log sitting right above
 * it is already a box, and this is reference information you go looking for ("which model wrote
 * this?"), not something the transcript should be announcing. Only the time is shown; the day is
 * carried by the divider between days, and the full date is on hover. */
function ChatStamp({ message }: { message: ChatMessage }) {
  const t = useT();
  const locale = useLocale();
  const when = parseStamp(message.createdAt);

  const parts: string[] = [];
  if (message.role === "assistant") {
    if (message.responseTimeMs !== undefined) parts.push(`⏱ ${formatResponseTime(message.responseTimeMs)}`);
    if (message.provider) parts.push(providerDisplayLabel(message.provider, t));
    // An empty provider still yields the raw model id, which is the honest answer for a turn
    // recorded before the provider was tracked.
    if (message.model) parts.push(modelDisplayLabel(message.provider ?? "", message.model, t));
    if (message.engineVersion) parts.push(`v${message.engineVersion}`);
  }
  if (when) parts.push(when.toLocaleTimeString(locale, { hour: "2-digit", minute: "2-digit" }));
  if (parts.length === 0) return null;

  return (
    <Tooltip label={when?.toLocaleString(locale) ?? ""}>
      <div
        className={`px-0.5 text-badge leading-tight text-[var(--cf-text-muted)] ${
          message.role === "user" ? "text-right" : ""
        }`}
      >
        {parts.join(" · ")}
      </div>
    </Tooltip>
  );
}

/** The date to announce before `message`, or `null` when it falls on the same day as the one
 * before it. Carrying the day here keeps every per-message stamp down to a bare time. */
function dayDivider(message: ChatMessage, previous: ChatMessage | undefined, locale: string): string | null {
  const when = parseStamp(message.createdAt);
  if (!when) return null;
  const before = parseStamp(previous?.createdAt);
  if (before && before.toDateString() === when.toDateString()) return null;
  return when.toLocaleDateString(locale, { day: "numeric", month: "long", year: "numeric" });
}

function ChatBubble({ message }: { message: ChatMessage }) {
  const t = useT();
  const [copied, copy] = useCopy();
  const [traceOpen, setTraceOpen] = useState(false);
  // The recorded process behind this answer. Rendered under every kind of assistant turn —
  // including the failed and the stopped ones, where "what was it doing when it died?" is the
  // whole question.
  const trace = message.trace;
  const traceLog = trace && trace.length > 0 && (
    <div className="mr-auto max-w-[95%] pt-1">
      <AiRunLog
        lines={trace}
        running={false}
        label={t("ai.traceSteps", { n: trace.length })}
        expanded={traceOpen}
        onToggle={() => setTraceOpen((v) => !v)}
      />
    </div>
  );
  const html = useMemo(
    () => (message.role === "assistant" && !message.isError ? renderMarkdown(message.content) : null),
    [message.role, message.content, message.isError],
  );
  // Parsed at render, not stored: a reopened conversation gets the same billing link and retry
  // advice as the moment it failed, from the raw text kept in the transcript.
  const parsedError = useMemo(
    () => (message.isError ? parseClaudeError(message.content) : null),
    [message.isError, message.content],
  );

  if (parsedError) {
    return (
      <div className="mr-auto max-w-[95%] space-y-1">
        <AiErrorBanner error={parsedError} compact />
        {traceLog}
        <ChatStamp message={message} />
      </div>
    );
  }

  if (message.isCancelled) {
    return (
      <div className="mr-auto max-w-[85%] space-y-1">
        <div className="flex items-center gap-1.5 rounded-lg border border-dashed border-[var(--cf-border)] px-2.5 py-1 text-badge text-[var(--cf-text-muted)]">
          <StopSquare size={11} aria-hidden />
          {t("ai.runStopped")}
        </div>
        {traceLog}
        <ChatStamp message={message} />
      </div>
    );
  }

  return (
    <div className="space-y-1">
      <div
        className={`group relative rounded-lg px-2.5 py-1.5 text-ui leading-relaxed ${
          message.role === "user"
            ? "ml-auto max-w-[85%] whitespace-pre-wrap bg-[var(--cf-accent-solid)] text-[var(--cf-accent-on-solid)]"
            : "mr-auto max-w-[85%] bg-[color-mix(in_oklab,var(--cf-accent)_6%,var(--cf-surface))] text-[var(--cf-text)]"
        }`}
      >
        {html !== null ? (
          <div className="cf-markdown-preview cf-markdown-chat" dangerouslySetInnerHTML={{ __html: html }} />
        ) : (
          message.content
        )}
        {/* Dimmed rather than hidden: a copy button that only exists under the pointer cannot be
            reached by keyboard or on a touch screen. */}
        <IconButton
          label="chat.copyMessage"
          icon={copied ? Check : Copy}
          className={`absolute -top-3 border border-[var(--cf-border)] bg-[var(--cf-surface)] opacity-55 shadow-sm group-hover:opacity-100 group-focus-within:opacity-100 ${
            message.role === "user" ? "-left-3" : "-right-3"
          }${copied ? " !text-[var(--cf-success)]" : ""}`}
          onClick={() => copy(message.content)}
        />
      </div>
      {traceLog}
      <ChatStamp message={message} />
    </div>
  );
}

export function ChatSection({ projectId }: { projectId: string }) {
  const t = useT();
  const locale = useLocale();
  const chat = useChatStore((s) => s.byProject[projectId] ?? EMPTY_CHAT);
  const send = useChatStore((s) => s.send);
  const clearChat = useChatStore((s) => s.clear);
  const conversations = useChatHistoryStore((s) => s.byProject[projectId] ?? EMPTY_CONVERSATIONS);
  const chatLoaded = useChatHistoryStore((s) => s.loaded[projectId] ?? false);
  const [input, setInput] = useState("");
  // Collapsed by default: the newest line is enough to know it's alive, and the full log is one
  // click away for when it isn't going well.
  const [logExpanded, setLogExpanded] = useState(false);
  const openSettings = useUiStore((s) => s.openSettings);
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: "smooth" });
  }, [chat.messages.length, chat.sending]);

  // Self-heal a chat whose conversation was deleted from history: once the persisted list is
  // loaded and this chat's conversation is no longer in it, it's gone — reset the panel so a
  // deleted chat can't keep showing (or get re-created on the next message). Keyed on
  // `conversations` (not on `chat`) so it evaluates against the freshest list and never races a
  // just-arrived reply whose conversation hasn't been reloaded into the list yet.
  useEffect(() => {
    if (!chatLoaded) return;
    const current = useChatStore.getState().byProject[projectId];
    if (!current || current.sending || current.messages.length === 0) return;
    if (!current.conversationId) return;
    const stillExists = conversations.some((c) => c.session_id === current.conversationId);
    if (!stillExists) clearChat(projectId);
  }, [conversations, chatLoaded, clearChat, projectId]);

  const submit = () => {
    if (!input.trim() || chat.sending) return;
    send(projectId, input);
    setInput("");
  };

  return (
    <div className="flex h-full flex-col">
      <div ref={scrollRef} className="flex-1 overflow-auto p-4">
        {chat.messages.length === 0 ? (
          <div className="flex h-full flex-col items-center justify-center gap-3 text-center">
            <div className="flex h-10 w-10 items-center justify-center rounded-2xl bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]">
              <Sparkles size={18} />
            </div>
            <p className="text-relaxed font-semibold">{t("chat.title")}</p>
            <p className="max-w-[220px] text-ui text-[var(--cf-text-muted)]">
              {t("chat.hint")}{" "}
              <Button variant="ghost" size="sm" onClick={() => openSettings("review")}>
                {t("chat.configure")}
              </Button>
            </p>
          </div>
        ) : (
          <div className="space-y-2.5">
            {chat.messages.map((m, i) => {
              // The day is announced once, between turns that fall on different dates, so each
              // message's own stamp stays a bare time instead of repeating the date all the way
              // down the transcript.
              const day = dayDivider(m, chat.messages[i - 1], locale);
              return (
                <Fragment key={i}>
                  {day && (
                    <div className="flex items-center gap-2 pt-1.5">
                      <div className="h-px flex-1 bg-[var(--cf-border)]" />
                      <span className="text-badge text-[var(--cf-text-muted)]">{day}</span>
                      <div className="h-px flex-1 bg-[var(--cf-border)]" />
                    </div>
                  )}
                  <ChatBubble message={m} />
                </Fragment>
              );
            })}
            {chat.sending && chat.runId && (
              // Replaces the old "thinking…" bubble: same reassurance, except now it says what
              // the engine is actually doing and can be stopped.
              <AiRunLog
                runId={chat.runId}
                running
                expanded={logExpanded}
                onToggle={() => setLogExpanded((v) => !v)}
              />
            )}
          </div>
        )}
      </div>

      <div className="border-t border-[var(--cf-border)] p-2.5">
        <div className="flex flex-col gap-1.5 rounded-xl border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-1.5">
          <textarea
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter" && !e.shiftKey) {
                e.preventDefault();
                submit();
              }
            }}
            placeholder={t("chat.placeholder")}
            aria-label={t("chat.placeholder")}
            rows={2}
            className="resize-none bg-transparent px-1.5 py-1 text-ui outline-none"
          />
          <div className="flex items-center gap-1.5 px-0.5">
            {/* Which engine this chat talks to — and the control that changes it. Picking here
                rewrites the *chat* task's routing, so it's a real settings change, not a
                per-conversation override. Once there are turns on screen the picker locks to the
                current provider's versions: sessions don't transfer between CLIs. */}
            <ChatModelPicker liveModel={chat.model} chatActive={chat.messages.length > 0} />
            <ChatAgentPicker projectId={projectId} />
            <IconButton
              label="chat.send"
              icon={ArrowUp}
              variant="primary"
              pending={chat.sending}
              disabled={!input.trim()}
              className="ml-auto"
              onClick={submit}
            />
          </div>
        </div>
      </div>
    </div>
  );
}
