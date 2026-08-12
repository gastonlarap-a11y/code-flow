import { create } from "zustand";
import * as api from "../lib/ipc/commands";
import { translations } from "../lib/i18n/translations";
import { useLanguageStore } from "./languageStore";
import { pushErrorToast } from "./toastStore";
import type {
  Ticket,
  TicketAccount,
  TicketCriteria,
  TicketReviewResult,
  TicketSummary,
  TicketWithLinks,
} from "../types/domain";

/**
 * What a picker is listing, and how it got the list.
 *
 * A discriminated union rather than a list plus two booleans: "loading with stale rows still on
 * screen" and "loaded but empty" are different pictures, and flags let you render neither.
 */
export type TicketBrowse =
  | { status: "idle" }
  | { status: "loading"; source: TicketSource }
  | { status: "loaded"; source: TicketSource; rows: TicketSummary[] }
  | { status: "failed"; source: TicketSource; message: string };

/** Where a picker's rows come from. Two ways of *finding* a ticket in a board of thousands — a real
 * one held 46 rows in the sprint where the project held thousands — never a condition on what can be
 * linked. A ticket belongs to a branch whatever sprint it is in. */
export type TicketSource = "sprint" | "mine";

/**
 * Where a work item lives.
 *
 * `org`/`project` are null when the user typed a bare id, and then the workspace's account fills
 * them in. When they are present they came from a pasted URL and they **win**: the address names its
 * own board, so linking a ticket from another project needs no reconfiguration — and the dialog
 * shows which one, so the choice is visible rather than confirmed.
 */
export interface TicketAddress {
  org: string | null;
  project: string | null;
  externalId: string;
}

/**
 * Which board an address actually points at: its own, or the workspace's.
 *
 * Null when neither answers — a bare id with no account configured. That used to be an early
 * `return`, so the link button did nothing and said nothing; it is now the one case that has to be
 * reported, and this function exists to make it a single, testable decision rather than a condition
 * repeated at two call sites.
 */
function resolveAddress(
  account: TicketAccount | null,
  address: TicketAddress,
): { org: string; project: string } | null {
  const org = address.org ?? account?.org ?? null;
  const project = address.project ?? account?.project ?? null;
  return org && project ? { org, project } : null;
}

/** Translates outside React, the way `jobsStore` already does — this store is not a component. */
function translate(key: keyof typeof translations.en): string {
  const language = useLanguageStore.getState().language;
  return translations[language][key] ?? translations.en[key] ?? key;
}

interface TicketState {
  /**
   * Every ticket the active workspace's projects have linked, most recently synced first.
   *
   * Workspace-wide, so each entry carries the branches it is work for — a list that mixes two
   * repositories and does not say which is which is what let a ticket from another repo stay on
   * screen looking like this one's.
   */
  tickets: TicketWithLinks[];
  /** The ticket explicitly linked to the checked-out branch, if any. */
  linked: Ticket | null;
  /** What the ticket asks for, keyed by ticket id — fetched on demand, never with the list. */
  criteria: Record<string, TicketCriteria>;
  /** Which Azure account this project's tickets come from, and whether anything decided it. */
  account: TicketAccount | null;
  browse: TicketBrowse;
  loading: boolean;
  /** The ticket whose detail is open, or null for the list. */
  selectedId: string | null;
  /**
   * The project the current account answers for.
   *
   * Remembered so a settings change can re-resolve it. The work-items view reads the account once,
   * when it mounts, and the settings panel opens *over* it rather than replacing it — so nothing
   * unmounted, no effect re-fired, and choosing an organisation left the view still showing the
   * "nothing decided this" state the user had just answered.
   */
  projectId: string | null;
  /**
   * The most recent stored review of the checked-out branch.
   *
   * What the ticket-review tab shows before anything has run this session — a verdict a person read
   * yesterday should still be there today, and re-running it costs a model call.
   */
  lastReview: TicketReviewResult | null;
  /** A publish in flight, so the button can say so and cannot be pressed twice. */
  commenting: boolean;

  load: (projectId: string, branch: string | null) => Promise<void>;
  /** Just the branch's link and its last review — what the AI panel needs, without the list. */
  loadBranchReview: (projectId: string, branch: string) => Promise<void>;
  /** Re-reads which account this project's tickets come from, after Settings changed it. */
  refreshAccount: () => Promise<void>;
  select: (ticketId: string | null) => void;
  browseFor: (source: TicketSource) => Promise<void>;
  link: (projectId: string, branch: string, address: TicketAddress) => Promise<void>;
  /** One work item's row, for showing what a pasted address resolved to before linking it. */
  preview: (address: TicketAddress) => Promise<TicketSummary | null>;
  unlink: (projectId: string, branch: string) => Promise<void>;
  /** Re-reads the workspace's tickets after something changed which branches point where. */
  reloadList: () => Promise<void>;
  refresh: (ticket: Ticket) => Promise<void>;
  criteriaFor: (ticketId: string) => Promise<void>;
  /**
   * Publishes a verdict onto the linked work item, on an explicit press.
   *
   * Never called by a review finishing. A review is run many times while work is in progress, and a
   * board collecting every attempt is worse than a board with nothing on it — so the text is shown
   * first and this is what the user chooses afterwards. `WI-022`.
   */
  comment: (body: string) => Promise<boolean>;
}

const initial = {
  tickets: [] as TicketWithLinks[],
  linked: null as Ticket | null,
  criteria: {} as Record<string, TicketCriteria>,
  account: null as TicketAccount | null,
  browse: { status: "idle" } as TicketBrowse,
  loading: false,
  selectedId: null as string | null,
  lastReview: null as TicketReviewResult | null,
  projectId: null as string | null,
  commenting: false,
};

export const useTicketStore = create<TicketState>((set, get) => ({
  ...initial,

  /**
   * Everything the module needs for one project: its account, its tickets, and the branch's link.
   *
   * The account resolves first and is kept even when it decided nothing — `source: "none"` is what
   * lets the view ask which organisation to use instead of showing an empty list and blaming Azure.
   *
   * <b>The selection follows the branch, always.</b> It used to be left alone here, and because the
   * list is workspace-wide the ticket of the repository you just left stayed both in the list and
   * open in the detail pane — with nothing on screen saying it was not this branch's. Now the branch
   * decides: its ticket if it has one, nothing if it does not. That is also the right default for a
   * module whose whole question is "what is this branch working on".
   */
  load: async (projectId, branch) => {
    set({ loading: true });
    try {
      const [account, tickets, linked] = await Promise.all([
        api.resolveTicketAccount(projectId),
        api.listTickets(projectId),
        branch ? api.ticketForBranch(projectId, branch) : Promise.resolve(null),
      ]);
      set({ account, tickets, linked, selectedId: linked?.id ?? null, loading: false, projectId });
    } catch (error) {
      set({ loading: false, projectId });
      pushErrorToast(String(error));
    }
  },

  /**
   * Re-resolves the account for whatever project was last loaded.
   *
   * Called by the settings row that changes it. Without this the answer was read once and never
   * again: the settings panel opens over the work-items view rather than replacing it, so no effect
   * re-fires and the view keeps showing the state the user just resolved.
   */
  refreshAccount: async () => {
    const projectId = get().projectId;
    if (!projectId) return;
    try {
      set({ account: await api.resolveTicketAccount(projectId) });
    } catch (error) {
      pushErrorToast(String(error));
    }
  },

  /**
   * The branch's ticket and its last review, for a panel that never opened the work-items module.
   *
   * Failures are swallowed to `null` rather than toasted: this runs whenever the AI panel's ticket
   * tab is shown, and a branch with no link is the ordinary case, not something to announce.
   */
  loadBranchReview: async (projectId, branch) => {
    const [linked, reviews] = await Promise.all([
      api.ticketForBranch(projectId, branch).catch(() => null),
      api.listTicketReviews(projectId, branch).catch(() => []),
    ]);
    set({ linked, lastReview: reviews[0] ?? null, projectId });
  },

  select: (ticketId) => set({ selectedId: ticketId }),

  /**
   * Fills the picker from Azure.
   *
   * Failures land in the union rather than in a toast: the picker is open and the person is
   * waiting on this list, so the reason belongs where the rows would have been. The sidecar's
   * message already names a wrong organisation or an expired token (`DIVERGENCE-PROV-c`).
   */
  browseFor: async (source) => {
    const account = get().account;
    if (!account?.org || !account.project) {
      set({ browse: { status: "failed", source, message: "no-account" } });
      return;
    }

    set({ browse: { status: "loading", source } });
    try {
      const rows =
        source === "sprint"
          ? await api.listSprintTickets(account.org, account.project)
          : await api.listMyTickets(account.org, account.project);
      set({ browse: { status: "loaded", source, rows } });
    } catch (error) {
      set({ browse: { status: "failed", source, message: String(error) } });
    }
  },

  /**
   * Syncs the ticket first, then links it: a branch never points at a row nothing has read.
   *
   * <b>The address wins over the workspace's account.</b> A pasted URL already names the board its
   * work item lives on, and throwing that away is what made pasting the address of a real ticket do
   * nothing at all: the account had no project — a repository hosted on GitHub has none — so this
   * returned early, in silence, with the button apparently dead. There is no silent path out of here
   * any more; what cannot be resolved is said out loud.
   */
  link: async (projectId, branch, address) => {
    const resolved = resolveAddress(get().account, address);
    if (!resolved) {
      pushErrorToast(translate("tickets.linkNoAccount"));
      return;
    }

    try {
      const ticket = await api.syncTicket(resolved.org, resolved.project, address.externalId);
      await api.linkBranchTicket(projectId, branch, ticket.id);
      set({ linked: ticket, selectedId: ticket.id });
      // Re-read rather than patch the list by hand: the link table just changed, and every entry
      // now carries the branches it is work for — which this call site does not know and would have
      // to invent. A rebuilt list cannot drift from the table it came from.
      await get().reloadList();
    } catch (error) {
      pushErrorToast(String(error));
    }
  },

  /**
   * Re-reads the workspace's tickets, after something changed which branches point where.
   *
   * Quiet on failure: it runs after an action that already succeeded and reported itself, and a
   * stale list is a smaller problem than a second toast for the same click.
   */
  reloadList: async () => {
    const projectId = get().projectId;
    if (!projectId) return;
    try {
      set({ tickets: await api.listTickets(projectId) });
    } catch {
      // See the remarks.
    }
  },

  /**
   * What a resolved address points at, or null.
   *
   * Null covers three cases the caller renders the same way — nothing to address, no such work item,
   * and a lookup that failed — because all three mean "there is nothing to show you yet" while
   * somebody is still typing. A missing token for the organisation is the one worth naming, and the
   * sidecar's own message already names it (`PullRequestHosts.PatForOrg`).
   */
  preview: async (address) => {
    const resolved = resolveAddress(get().account, address);
    if (!resolved) return null;

    try {
      return await api.previewTicket(resolved.org, resolved.project, address.externalId);
    } catch {
      return null;
    }
  },

  comment: async (body) => {
    const ticket = get().linked;
    if (!ticket || get().commenting) return false;

    set({ commenting: true });
    try {
      await api.commentTicket(ticket.id, body);
      return true;
    } catch (error) {
      // Loudly, like linking: a publish that silently does nothing is the failure this feature
      // already made once, and a board is exactly where "did it work?" cannot be answered by looking
      // at the app.
      pushErrorToast(String(error));
      return false;
    } finally {
      set({ commenting: false });
    }
  },

  unlink: async (projectId, branch) => {
    try {
      await api.unlinkBranchTicket(projectId, branch);
      // The selection goes with it: what was on screen was this branch's ticket, and it no longer
      // is one. The list is re-read because that branch's link just disappeared from it.
      set({ linked: null, selectedId: null });
      await get().reloadList();
    } catch (error) {
      pushErrorToast(String(error));
    }
  },

  refresh: async (ticket) => {
    try {
      const fresh = await api.syncTicket(ticket.org, ticket.project, ticket.external_id);
      set((s) => ({
        // The links are unchanged by a refresh — it re-reads the work item, not who points at it —
        // so the entry keeps them and only its ticket half is replaced.
        tickets: s.tickets.map((entry) =>
          entry.ticket.id === fresh.id ? { ...entry, ticket: fresh } : entry,
        ),
        linked: s.linked?.id === fresh.id ? fresh : s.linked,
        // The criteria are derived from the payload that just changed, so the cached answer is
        // stale by definition. Dropped rather than refetched: the detail asks when it renders.
        criteria: Object.fromEntries(Object.entries(s.criteria).filter(([id]) => id !== fresh.id)),
      }));
    } catch (error) {
      pushErrorToast(String(error));
    }
  },

  criteriaFor: async (ticketId) => {
    if (get().criteria[ticketId]) return;
    try {
      const criteria = await api.getTicketCriteria(ticketId);
      set((s) => ({ criteria: { ...s.criteria, [ticketId]: criteria } }));
    } catch (error) {
      pushErrorToast(String(error));
    }
  },
}));
