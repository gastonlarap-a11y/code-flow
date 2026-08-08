import { describe, expect, test } from "vitest";
import { parseClaudeError } from "./claudeError";

/**
 * The renderer half of `XLANG-003`, the quota sentinel.
 *
 * `QUOTA_EXCEEDED::` is a byte-level contract with `AiText.cs`: the sidecar decides a provider
 * refusal is a quota problem and prefixes the marker; this decides what the user is then told to do
 * about it. Nothing checks the two agree. Change the marker on either side and this parser stops
 * recognising it — the app does not throw, it just shows the raw CLI error where it used to show
 * "you hit your limit, it resets in 3 hours", which reads as a bug in the app rather than a bill to
 * pay.
 *
 * Note there is no trailing space, unlike the four `PREFIX: ` sentinels the stores parse. That is
 * the contract, and it is why the marker is matched with `includes` rather than `startsWith`.
 */

const MARKER = "QUOTA_EXCEEDED::";

describe("output the sidecar did not mark", () => {
  test("passes through untouched", () => {
    const info = parseClaudeError("error: unknown flag --nope");

    expect(info.isQuotaExceeded).toBe(false);
    expect(info.message).toBe("error: unknown flag --nope");
    expect(info.kind).toBe(null);
    expect(info.resetHint).toBe(null);
    expect(info.actionUrl).toBe(null);
  });

  // The marker is one token; a message that merely talks about quotas is not marked.
  test("is not recognised by its wording alone", () => {
    expect(parseClaudeError("You have exceeded your quota").isQuotaExceeded).toBe(false);
  });
});

describe("telling a spent balance from a rate limit", () => {
  // Both arrive under the same marker — `AiText.cs` prefixes either — but the advice differs:
  // a usage limit lifts on its own, a billing one needs the user to go and pay.
  test("a rate limit is a usage problem", () => {
    const info = parseClaudeError(`${MARKER} Rate limit reached. Try again in 30 minutes.`);

    expect(info.isQuotaExceeded).toBe(true);
    expect(info.kind).toBe("usage");
  });

  for (const signal of [
    "insufficient balance",
    "insufficient credit",
    "out of credit",
    "payment required",
    "billing",
  ]) {
    test(`"${signal}" is a billing problem`, () => {
      expect(parseClaudeError(`${MARKER} Error: ${signal} on your account.`).kind).toBe("billing");
    });
  }

  // The wording the sidecar's own fixture carries (`ai.vectors.json`, quota_signal), so the two
  // halves are checked against one message rather than two invented ones.
  test("the message from the sidecar's own vector reads as billing", () => {
    const info = parseClaudeError(
      `${MARKER} Error: Insufficient balance. Manage your billing here: https://x/billing`,
    );

    expect(info.kind).toBe("billing");
    expect(info.actionUrl).toBe("https://x/billing");
  });

  test("the signals are matched regardless of case", () => {
    expect(parseClaudeError(`${MARKER} PAYMENT REQUIRED`).kind).toBe("billing");
  });
});

describe("the message", () => {
  test("keeps only what follows the marker, trimmed", () => {
    expect(parseClaudeError(`${MARKER}   You are out of requests.  `).message).toBe(
      "You are out of requests.",
    );
  });

  // `includes`, not `startsWith`: the CLI's own prefix ("Error: ") can sit in front of it.
  test("is found even when the marker is not at the start", () => {
    expect(parseClaudeError(`Error: ${MARKER} limit reached`).message).toBe("limit reached");
  });
});

describe("the reset hint", () => {
  for (const [written, expected] of [
    ["3 hours", "3 hours"],
    ["1 hour", "1 hour"],
    ["2 HRS", "2 hrs"],
    ["45 minutes", "45 minutes"],
    ["10 mins", "10 mins"],
  ] as const) {
    test(`reads "${written}"`, () => {
      expect(parseClaudeError(`${MARKER} Usage limit reached, resets in ${written}.`).resetHint).toBe(
        expected,
      );
    });
  }

  test("is absent when the message names no duration", () => {
    expect(parseClaudeError(`${MARKER} Usage limit reached.`).resetHint).toBe(null);
  });

  // A balance does not refill on a timer, so a duration found in a billing message would be
  // "resets in 5 minutes" advice for a problem only a payment fixes.
  test("is never offered for a billing problem", () => {
    expect(
      parseClaudeError(`${MARKER} Insufficient balance. Retry in 5 minutes.`).resetHint,
    ).toBe(null);
  });
});

describe("the action link", () => {
  test("is absent when the provider gave none", () => {
    expect(parseClaudeError(`${MARKER} Usage limit reached.`).actionUrl).toBe(null);
  });

  // The URL usually ends a sentence, and a trailing period is part of no URL.
  test("drops the punctuation that ended the sentence", () => {
    expect(parseClaudeError(`${MARKER} Top up at https://example.com/billing.`).actionUrl).toBe(
      "https://example.com/billing",
    );
  });

  test("stops at a closing parenthesis", () => {
    expect(parseClaudeError(`${MARKER} See (https://example.com/plans) for limits`).actionUrl).toBe(
      "https://example.com/plans",
    );
  });
});
