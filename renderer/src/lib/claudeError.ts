const QUOTA_MARKER = "QUOTA_EXCEEDED::";

/** The engine's own login is gone — its OAuth session expired, or it was never signed in. The
 * sidecar only ever tags a run that already failed, so this never arrives on a real answer
 * (`AuthSignals`, `XLANG-003`). */
const AUTH_MARKER = "AUTH_EXPIRED::";

/** Why the provider refused. `usage` is a rate/usage limit that lifts on its own; `billing` is an
 * account-balance problem that needs the user to top up — different advice, so they're separated
 * rather than both shown as "you hit your limit". */
export type QuotaKind = "usage" | "billing";

const BILLING_SIGNALS = ["insufficient balance", "insufficient credit", "out of credit", "payment required", "billing"];

export interface ClaudeErrorInfo {
  isQuotaExceeded: boolean;
  /** The engine is not signed in. Never true at the same time as `isQuotaExceeded` — the sidecar
   * tests for a quota refusal first, so a message that reads as both is a quota one. */
  isAuthExpired: boolean;
  /** Only set when `isQuotaExceeded`. */
  kind: QuotaKind | null;
  message: string;
  /** Best-effort "resets in N hours/minutes" extracted from the CLI's own message. */
  resetHint: string | null;
  /** An http(s) link the provider pointed at (e.g. its billing page), when it included one. */
  actionUrl: string | null;
}

export function parseClaudeError(raw: string): ClaudeErrorInfo {
  if (raw.includes(QUOTA_MARKER)) {
    const message = raw.slice(raw.indexOf(QUOTA_MARKER) + QUOTA_MARKER.length).trim();
    const lower = message.toLowerCase();
    const kind: QuotaKind = BILLING_SIGNALS.some((s) => lower.includes(s)) ? "billing" : "usage";
    const match = message.match(/(\d+)\s*(hours?|hrs?|minutes?|mins?)/i);
    // Trailing punctuation is common when the URL ends a sentence, and would break the link.
    const url = message.match(/https?:\/\/[^\s)]+/)?.[0]?.replace(/[.,;:]+$/, "") ?? null;
    return {
      isQuotaExceeded: true,
      isAuthExpired: false,
      kind,
      message,
      resetHint: kind === "usage" && match ? `${match[1]} ${match[2]!.toLowerCase()}` : null,
      actionUrl: url,
    };
  }

  // Checked after quota, mirroring the order the sidecar tags in, so a message carrying both reads
  // the same on either side. No reset hint and no action URL: nothing about a lost login lifts on a
  // timer, and the fix is a command in a terminal rather than a page to open.
  if (raw.includes(AUTH_MARKER)) {
    return {
      isQuotaExceeded: false,
      isAuthExpired: true,
      kind: null,
      message: raw.slice(raw.indexOf(AUTH_MARKER) + AUTH_MARKER.length).trim(),
      resetHint: null,
      actionUrl: null,
    };
  }

  return {
    isQuotaExceeded: false,
    isAuthExpired: false,
    kind: null,
    message: raw,
    resetHint: null,
    actionUrl: null,
  };
}
