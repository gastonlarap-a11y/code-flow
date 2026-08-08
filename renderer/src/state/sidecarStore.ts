import { create } from "zustand";
import { host } from "../lib/bridge/host";
import { onSidecarStatus } from "../lib/ipc/events";
import type { UnlistenFn } from "../lib/ipc/events";

/**
 * Whether the .NET core is answering, and what it said if it is not.
 *
 * The shell has forwarded `codeflow:sidecar-status` since the sidecar became its own process, and
 * nothing in the renderer ever listened. That is why a core which failed to start looked like an app
 * whose buttons did nothing: every `invoke` rejected with a clear message, each caller dropped it,
 * and the one component that could have said so did not exist.
 *
 * Both halves are needed. The event catches a core that dies mid-session; the getter catches the case
 * this store was actually written for — a core that never started, which is `down` milliseconds after
 * launch, long before this window has attached a listener.
 */

export type SidecarStatus = "starting" | "ready" | "down";

interface SidecarState {
  status: SidecarStatus;
  /** The shell's own words: a missing binary, an exit code, a refused connection. */
  detail: string | null;
  /** Where the two log files live, so the banner can name a path rather than a platform. */
  logsDirectory: string | null;

  /** Subscribes, then reconciles with the state the shell already holds. */
  init: () => Promise<UnlistenFn>;
}

export const useSidecarStore = create<SidecarState>((set) => ({
  status: "starting",
  detail: null,
  logsDirectory: null,

  init: async () => {
    // Subscribed before the read, not after: a core that goes down between the two would otherwise
    // announce it to nobody and then be overwritten by the older answer.
    const unlisten = await onSidecarStatus((event) =>
      set({ status: event.status, detail: event.detail ?? null }),
    );

    try {
      const current = await host.sidecarStatus();
      set((s) => ({
        // The event wins if one already arrived: it is the newer of the two by construction.
        status: s.status === "starting" ? current.status : s.status,
        detail: s.detail ?? current.detail ?? null,
        logsDirectory: current.logsDirectory,
      }));
    } catch {
      // An older shell, or the renderer in a plain browser (`pnpm dev` with no Electron around it).
      // Neither is a reason to fail the app's startup — this store only ever adds an explanation.
    }

    return unlisten;
  },
}));
