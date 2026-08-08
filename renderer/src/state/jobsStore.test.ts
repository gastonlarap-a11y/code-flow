import { beforeEach, describe, expect, test, vi } from "vitest";

/**
 * How a failed job's error text is filed.
 *
 * Everything the UI decides about a finished run — cancelled or failed, refusal or error, quota or
 * not — is a text match against this one string. `String(error)` prepends `"Error: "` to it, which
 * silently moved every sentinel off the start of the message: a clean working tree was shown as a
 * red `Error: NOTHING_TO_ANALYZE: …` banner, while the identical refusal reloaded from
 * `job_history` (stored raw) rendered as the empty state. The prefixes are a cross-language
 * contract (`docs/business-rules/13-cross-language-contracts.md`), so this is where they survive.
 */

vi.mock("../lib/ipc/commands", () => ({
  listJobHistory: vi.fn(() => Promise.resolve([])),
  renameJobHistoryEntry: vi.fn(() => Promise.resolve()),
  deleteJobHistoryEntry: vi.fn(() => Promise.resolve()),
}));
vi.mock("../lib/ipc/events", () => ({ onAiOutput: vi.fn(() => Promise.resolve(() => {})) }));

const { useJobsStore } = await import("./jobsStore");
const { CANCELLED_MARKER } = await import("./aiRunStore");
const { isRefusal, NOTHING_TO_ANALYZE_PREFIX } = await import("../lib/analyzeRefusal");

const initial = useJobsStore.getState();

beforeEach(() => {
  vi.resetAllMocks();
  useJobsStore.setState(initial, true);
});

/** Runs one job whose task rejects, and hands back the row it settled into. */
async function failWith(reason: unknown) {
  const id = useJobsStore.getState().run({
    projectId: "p1",
    kind: "analyze-changes",
    label: "Análisis de cambios · 10:00",
    task: () => Promise.reject(reason),
  });

  // The `.catch` that settles the row is one microtask behind the rejection.
  await Promise.resolve();
  await Promise.resolve();

  const job = useJobsStore.getState().jobsFor("p1").find((j) => j.id === id);
  if (!job) throw new Error("the job was not filed");
  return job;
}

describe("filing a failure", () => {
  test("keeps the sidecar's message, without the transport's `Error: `", async () => {
    const job = await failWith(new Error(`${NOTHING_TO_ANALYZE_PREFIX}no hay cambios`));

    expect(job.status).toBe("error");
    expect(job.error?.message).toBe(`${NOTHING_TO_ANALYZE_PREFIX}no hay cambios`);
    // The whole point: the empty state wins instead of a red banner.
    expect(isRefusal(job)).toBe(true);
  });

  test("a sentinel anchored at the start survives for the other features too", async () => {
    const job = await failWith(new Error("STALE_REVIEW: the head moved"));

    expect(job.error?.message.startsWith("STALE_REVIEW: ")).toBe(true);
  });

  test("a quota failure is still parsed into its own shape", async () => {
    const job = await failWith(new Error("QUOTA_EXCEEDED::usage limit reached, resets in 3 hours"));

    expect(job.error?.isQuotaExceeded).toBe(true);
    expect(job.error?.kind).toBe("usage");
  });

  test("a rejection that is not an Error still files something readable", async () => {
    const job = await failWith("the core stopped");

    expect(job.error?.message).toBe("the core stopped");
  });

  test("a stopped run is cancelled, not failed", async () => {
    const job = await failWith(new Error(`${CANCELLED_MARKER} job-1`));

    expect(job.status).toBe("cancelled");
    expect(job.error).toBe(null);
  });
});

describe("filing a success", () => {
  test("keeps the result and stops running", async () => {
    const id = useJobsStore.getState().run({
      projectId: "p1",
      kind: "analyze-changes",
      label: "Análisis de cambios · 10:00",
      task: () => Promise.resolve("### finding"),
    });

    await Promise.resolve();
    await Promise.resolve();

    const job = useJobsStore.getState().jobsFor("p1").find((j) => j.id === id);
    expect(job?.status).toBe("done");
    expect(job?.result).toBe("### finding");
  });
});
