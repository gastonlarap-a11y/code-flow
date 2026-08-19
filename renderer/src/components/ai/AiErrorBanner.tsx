import { Check, Copy, ExternalLink } from "lucide-react";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import { useCopy } from "../../lib/ui/useCopy";
import type { ClaudeErrorInfo } from "../../lib/claudeError";
import { openExternalUrl } from "../../lib/ipc/commands";
import { useT } from "../../state/languageStore";
import { isTimeout, timeoutMinutes } from "../../state/aiRunStore";
import { useTaskProvider } from "../../state/aiProviderStore";
import { authCommandFor } from "../../lib/ui/authCommand";

/**
 * How an AI failure is shown. Five cases, because the advice differs: a usage limit lifts on its
 * own (tell them when), an empty balance needs them to top up (link them straight there), an engine
 * that lost its login needs one command typed in a terminal (name it), a run that hit its deadline
 * never failed at all and its marker would be unreadable on screen, and anything else is a real
 * error worth showing verbatim.
 */
export function AiErrorBanner({
  error,
  compact = false,
  task,
}: {
  error: ClaudeErrorInfo;
  compact?: boolean;
  /** Which routed engine produced this, so a lost login can name its own re-login command. Omitted
   * where the caller does not know, which costs only the command and not the notice. */
  task?: string;
}) {
  const t = useT();
  const [copied, copy] = useCopy();
  const size = compact ? "text-ui" : "text-body";
  const subSize = compact ? "text-badge" : "text-ui";

  // Called unconditionally, as a hook must be; the result is only trusted when the caller named a
  // task, because with none `useTaskProvider` answers with the global provider — a plausible-looking
  // guess at which engine failed, which is exactly what must not reach a command someone types.
  const routed = useTaskProvider(task ?? "");
  const authCommand = task ? authCommandFor(routed) : null;

  const timedOut = isTimeout(error.message);
  const minutes = timedOut ? timeoutMinutes(error.message) : null;

  const headline = timedOut
    ? minutes
      ? t("ai.runTimedOut", { minutes })
      : t("ai.runTimedOutPlain")
    : !error.isQuotaExceeded
      ? error.message
      : error.kind === "billing"
        ? t("ai.billingMessage")
        : t("changes.quotaMessage");

  return (
    <div className="rounded-lg border border-[var(--cf-danger)]/30 bg-[color-mix(in_oklab,var(--cf-danger)_8%,transparent)] p-4">
      <div className="flex items-start gap-2">
        {/* `select-text` re-enables selection against the app-wide `body { user-select: none }`,
            the same local opt-out `DiffView` and `VariableInput` make. An error nobody can select
            is an error nobody can send you, which is how one was retyped by hand to report it. */}
        <p className={`min-w-0 flex-1 select-text whitespace-pre-wrap break-words ${size} text-[var(--cf-danger)]`}>
          {headline}
        </p>

        {/* Selection alone is fiddly on a wrapped, multi-line message; the button copies the whole
            thing. It copies `error.message` rather than the headline: a quota or timeout notice is
            worded for a reader, and what is worth sending on is what the engine actually said. */}
        <IconButton
          label={copied ? "ai.errorCopied" : "ai.copyError"}
          icon={copied ? Check : Copy}
          className="shrink-0"
          onClick={() => copy(error.message)}
        />
      </div>

      {timedOut && (
        <p className={`mt-1 ${subSize} text-[var(--cf-text-muted)]`}>{t("ai.runTimedOutHint")}</p>
      )}

      {/* The headline already carries the CLI's own sentence, which names the provider and is worth
          reading; this adds the only thing it cannot know — what to type to fix it. */}
      {error.isAuthExpired && (
        <p className={`mt-1 ${subSize} text-[var(--cf-text-muted)]`}>
          {authCommand ? t("ai.authExpiredHint", { command: authCommand }) : t("ai.authExpiredHintPlain")}
        </p>
      )}

      {error.isQuotaExceeded && (
        <p className={`mt-1 ${subSize} text-[var(--cf-text-muted)]`}>
          {error.kind === "billing"
            ? t("ai.billingHint")
            : error.resetHint
              ? t("changes.quotaRetry", { hint: error.resetHint })
              : t("changes.quotaRetryLater")}
        </p>
      )}

      {error.actionUrl && (
        <Button
          variant="ghost"
          size="sm"
          icon={ExternalLink}
          className="mt-2"
          onClick={() => void openExternalUrl(error.actionUrl!)}
        >
          {/* Providers don't always link to billing — OpenAI's quota error points at its error-code
              docs — so the label follows the URL rather than promising a payments page. */}
          {/billing|payment|plans?\b/i.test(error.actionUrl) ? t("ai.openBilling") : t("ai.openLink")}
        </Button>
      )}
    </div>
  );
}
