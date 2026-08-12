import { beforeEach, describe, expect, test, vi } from "vitest";
import type {
  Ticket,
  TicketAccount,
  TicketCriteria,
  TicketLink,
  TicketSummary,
  TicketWithLinks,
} from "../types/domain";

// The sidecar is the only thing this store talks to. Under `environment: "node"` the real module
// reaches `window.codeflow`, which does not exist here.
vi.mock("../lib/ipc/commands", () => ({
  resolveTicketAccount: vi.fn(),
  listTickets: vi.fn(),
  ticketForBranch: vi.fn(),
  listSprintTickets: vi.fn(),
  listMyTickets: vi.fn(),
  syncTicket: vi.fn(),
  linkBranchTicket: vi.fn(),
  unlinkBranchTicket: vi.fn(),
  getTicketCriteria: vi.fn(),
  previewTicket: vi.fn(),
  listTicketReviews: vi.fn(),
  commentTicket: vi.fn(),
}));

const toasts: string[] = [];
vi.mock("./toastStore", () => ({
  pushErrorToast: (message: string) => toasts.push(message),
}));

import * as api from "../lib/ipc/commands";
import { translations } from "../lib/i18n/translations";
import { useTicketStore } from "./ticketStore";

// The whole state, actions included, captured before any test runs — `setState(…, true)` replaces
// rather than merges, so resetting with the data alone would strip the actions off the store.
const initial = useTicketStore.getState();

const ticket = (externalId: string, overrides: Partial<Ticket> = {}): Ticket => ({
  id: `azure:contoso:Web:${externalId}`,
  provider: "azure",
  org: "contoso",
  project: "Web",
  external_id: externalId,
  title: `Ticket ${externalId}`,
  state: "Active",
  work_item_type: "Product Backlog Item",
  assigned_to: null,
  web_url: `https://dev.azure.com/contoso/Web/_workitems/edit/${externalId}`,
  rev: 1,
  mirror_path: "/tmp/mirror",
  synced_at: "2026-08-11T00:00:00.0000000+00:00",
  ...overrides,
});

const account = (source: TicketAccount["source"], org: string | null = "contoso"): TicketAccount => ({
  org,
  project: org ? "Web" : null,
  source,
});

/** A list entry: the ticket plus where it is linked. One link is the ordinary case. */
const entry = (
  externalId: string,
  link: Partial<TicketLink> = {},
  overrides: Partial<Ticket> = {},
): TicketWithLinks => ({
  ticket: ticket(externalId, overrides),
  links: [
    {
      project_id: link.project_id ?? "proj",
      project_name: link.project_name ?? "code-flow",
      branch: link.branch ?? "feature/x",
    },
  ],
});

const summary = (externalId: string): TicketSummary => ({
  external_id: externalId,
  title: `Row ${externalId}`,
  state: "Active",
  work_item_type: "Bug",
  assigned_to: null,
});

beforeEach(() => {
  vi.resetAllMocks();
  toasts.length = 0;
  useTicketStore.setState(initial, true);
});

describe("load", () => {
  test("brings back the account, the list and the branch's own ticket", async () => {
    vi.mocked(api.resolveTicketAccount).mockResolvedValue(account("workspace"));
    vi.mocked(api.listTickets).mockResolvedValue([entry("111")]);
    vi.mocked(api.ticketForBranch).mockResolvedValue(ticket("222"));

    await useTicketStore.getState().load("proj", "feature/x");

    const state = useTicketStore.getState();
    expect(state.account?.source).toBe("workspace");
    expect(state.tickets.map((e) => e.ticket.external_id)).toEqual(["111"]);
    expect(state.linked?.external_id).toBe("222");
    expect(state.loading).toBe(false);
    // The branch's ticket opens by itself: that is the question this module answers.
    expect(state.selectedId).toBe(ticket("222").id);
  });

  test("switching to a project whose branch has no ticket clears the selection", async () => {
    // The defect: `load` left `selectedId` alone, so the ticket of the repository you just left
    // stayed open in the detail pane looking like this branch's. A user hit exactly this and asked
    // whether it was right. It was not.
    useTicketStore.setState({ selectedId: "azure:contoso:Web:111", tickets: [entry("111")] });
    vi.mocked(api.resolveTicketAccount).mockResolvedValue(account("workspace"));
    vi.mocked(api.listTickets).mockResolvedValue([]);
    vi.mocked(api.ticketForBranch).mockResolvedValue(null);

    await useTicketStore.getState().load("other-proj", "main");

    expect(useTicketStore.getState().selectedId).toBeNull();
  });

  test("the list is read for this repository, not for the workspace", async () => {
    // The scope the module answers for. It was workspace-wide first, and using it settled the
    // question: a list that mixes in another repository's tickets answers a question this view
    // never asked.
    vi.mocked(api.resolveTicketAccount).mockResolvedValue(account("workspace"));
    vi.mocked(api.listTickets).mockResolvedValue([]);
    vi.mocked(api.ticketForBranch).mockResolvedValue(null);

    await useTicketStore.getState().load("proj", "main");

    expect(api.listTickets).toHaveBeenCalledWith("proj");
  });

  test("a list entry carries the repository and branch it belongs to", async () => {
    vi.mocked(api.resolveTicketAccount).mockResolvedValue(account("workspace"));
    vi.mocked(api.listTickets).mockResolvedValue([
      entry("111", { project_name: "seguros-api", branch: "fix/prima" }),
    ]);
    vi.mocked(api.ticketForBranch).mockResolvedValue(null);

    await useTicketStore.getState().load("proj", "main");

    const links = useTicketStore.getState().tickets[0]?.links ?? [];
    expect(links).toHaveLength(1);
    expect(links[0]?.project_name).toBe("seguros-api");
    expect(links[0]?.branch).toBe("fix/prima");
  });

  test("a detached head has no branch, so no link is looked up", async () => {
    vi.mocked(api.resolveTicketAccount).mockResolvedValue(account("project"));
    vi.mocked(api.listTickets).mockResolvedValue([]);

    await useTicketStore.getState().load("proj", null);

    expect(api.ticketForBranch).not.toHaveBeenCalled();
    expect(useTicketStore.getState().linked).toBeNull();
  });

  test("a failure clears the loading flag instead of leaving a spinner", async () => {
    vi.mocked(api.resolveTicketAccount).mockRejectedValue(new Error("no such project"));

    await useTicketStore.getState().load("proj", "main");

    expect(useTicketStore.getState().loading).toBe(false);
    expect(toasts).toHaveLength(1);
  });
});

describe("browse", () => {
  test("an undecided account never reaches the network", async () => {
    // The point of `source: "none"`: with two organisations connected and nothing choosing, a
    // request would read whichever one it guessed and show an empty list.
    useTicketStore.setState({ account: account("none", null) });

    await useTicketStore.getState().browseFor("sprint");

    expect(api.listSprintTickets).not.toHaveBeenCalled();
    expect(useTicketStore.getState().browse).toEqual({
      status: "failed",
      source: "sprint",
      message: "no-account",
    });
  });

  test("the sprint is read for the resolved account", async () => {
    useTicketStore.setState({ account: account("workspace") });
    vi.mocked(api.listSprintTickets).mockResolvedValue([summary("1"), summary("2")]);

    await useTicketStore.getState().browseFor("sprint");

    expect(api.listSprintTickets).toHaveBeenCalledWith("contoso", "Web");
    const browse = useTicketStore.getState().browse;
    expect(browse.status).toBe("loaded");
    expect(browse.status === "loaded" && browse.rows).toHaveLength(2);
  });

  test("a failure keeps the sidecar's own message, which names the cause", async () => {
    // The sidecar already says "the organisation name is right / the token has not expired"
    // (DIVERGENCE-PROV-c). Replacing it with "something went wrong" throws that away.
    useTicketStore.setState({ account: account("workspace") });
    vi.mocked(api.listMyTickets).mockRejectedValue(new Error("Azure DevOps returned 404 Not Found: …"));

    await useTicketStore.getState().browseFor("mine");

    const browse = useTicketStore.getState().browse;
    expect(browse.status).toBe("failed");
    expect(browse.status === "failed" && browse.message).toContain("404");
    // Not a toast: the picker is open and waiting on this list.
    expect(toasts).toHaveLength(0);
  });
});

/** A bare id: nothing but the number, so the workspace's account has to supply the board. */
const bare = (externalId: string) => ({ org: null, project: null, externalId });

describe("link", () => {
  test("syncs before linking, so a branch never points at a row nothing has read", async () => {
    useTicketStore.setState({ account: account("workspace") });
    vi.mocked(api.syncTicket).mockResolvedValue(ticket("426647"));
    vi.mocked(api.linkBranchTicket).mockResolvedValue(null);

    await useTicketStore.getState().link("proj", "feature/x", bare("426647"));

    expect(api.syncTicket).toHaveBeenCalledWith("contoso", "Web", "426647");
    expect(api.linkBranchTicket).toHaveBeenCalledWith("proj", "feature/x", "azure:contoso:Web:426647");
    expect(useTicketStore.getState().linked?.external_id).toBe("426647");
  });

  test("a pasted address wins over the workspace's account", async () => {
    // The cross-project case, and the reason the address travels at all: a URL names the board its
    // work item lives on, so linking one from another project needs no reconfiguration.
    useTicketStore.setState({ account: account("workspace") });
    vi.mocked(api.syncTicket).mockResolvedValue(
      ticket("3", { org: "kakaroto044", project: "app-personales", id: "azure:kakaroto044:app-personales:3" }),
    );
    vi.mocked(api.linkBranchTicket).mockResolvedValue(null);

    await useTicketStore
      .getState()
      .link("proj", "feature/x", { org: "kakaroto044", project: "app-personales", externalId: "3" });

    expect(api.syncTicket).toHaveBeenCalledWith("kakaroto044", "app-personales", "3");
  });

  test("with nothing to address a board it says so instead of doing nothing", async () => {
    // The defect this replaced: an early `return` with no toast, so the button was silently dead on
    // a repository whose account had no board project — every GitHub-hosted one.
    useTicketStore.setState({ account: account("none", null) });

    await useTicketStore.getState().link("proj", "feature/x", bare("426647"));

    expect(api.syncTicket).not.toHaveBeenCalled();
    expect(toasts).toHaveLength(1);
    // Compared against the key rather than the sentence: what this pins is that something
    // actionable is said, not how it is worded in one locale.
    expect(toasts[0]).toBe(translations.en["tickets.linkNoAccount"]);
  });

  test("the list is re-read from the links table rather than patched by hand", async () => {
    // The link table just changed, and every entry now carries the branches it points at — which
    // this call site does not know. Re-reading is what stops the list drifting from the table.
    useTicketStore.setState({ account: account("workspace"), projectId: "proj" });
    vi.mocked(api.syncTicket).mockResolvedValue(ticket("426647", { state: "Done" }));
    vi.mocked(api.linkBranchTicket).mockResolvedValue(null);
    vi.mocked(api.listTickets).mockResolvedValue([entry("426647", {}, { state: "Done" }), entry("111")]);

    await useTicketStore.getState().link("proj", "feature/x", bare("426647"));

    expect(api.listTickets).toHaveBeenCalledWith("proj");
    const tickets = useTicketStore.getState().tickets;
    expect(tickets.map((e) => e.ticket.external_id)).toEqual(["426647", "111"]);
    expect(useTicketStore.getState().selectedId).toBe(ticket("426647").id);
  });

  test("unlinking forgets the branch's link and closes what was open for it", async () => {
    useTicketStore.setState({
      linked: ticket("111"),
      selectedId: ticket("111").id,
      tickets: [entry("111")],
      projectId: "proj",
    });
    vi.mocked(api.unlinkBranchTicket).mockResolvedValue(null);
    vi.mocked(api.listTickets).mockResolvedValue([]);

    await useTicketStore.getState().unlink("proj", "feature/x");

    expect(useTicketStore.getState().linked).toBeNull();
    // The pane was showing this branch's ticket, and it no longer has one.
    expect(useTicketStore.getState().selectedId).toBeNull();
  });
});

describe("refresh", () => {
  test("drops the cached criteria, which were derived from the payload that just changed", async () => {
    const criteria: TicketCriteria = { mode: "prose", field: "System.Description", markdown: "old", items: [] };
    useTicketStore.setState({
      tickets: [entry("111")],
      criteria: { "azure:contoso:Web:111": criteria },
    });
    vi.mocked(api.syncTicket).mockResolvedValue(ticket("111", { state: "Done" }));

    await useTicketStore.getState().refresh(ticket("111"));

    expect(useTicketStore.getState().tickets[0]?.ticket.state).toBe("Done");
    // A refresh re-reads the work item, not who points at it, so the links survive untouched.
    expect(useTicketStore.getState().tickets[0]?.links).toHaveLength(1);
    expect(useTicketStore.getState().criteria["azure:contoso:Web:111"]).toBeUndefined();
  });
});

describe("criteria", () => {
  test("are fetched once and then served from the cache", async () => {
    const criteria: TicketCriteria = { mode: "list", field: "AC", markdown: "- uno", items: ["uno"] };
    vi.mocked(api.getTicketCriteria).mockResolvedValue(criteria);

    await useTicketStore.getState().criteriaFor("t1");
    await useTicketStore.getState().criteriaFor("t1");

    expect(api.getTicketCriteria).toHaveBeenCalledTimes(1);
    expect(useTicketStore.getState().criteria["t1"]).toEqual(criteria);
  });
});

describe("comment", () => {
  test("publishes the text it was given, on the linked ticket", async () => {
    useTicketStore.setState({ linked: ticket("3") });
    vi.mocked(api.commentTicket).mockResolvedValue("https://dev.azure.com/contoso/Web/_workitems/edit/3");

    const ok = await useTicketStore.getState().comment("## VERIFICACIÓN\n**cumple**");

    expect(ok).toBe(true);
    // Verbatim, and against the linked ticket's id. Anything else here would publish something the
    // user never read.
    expect(api.commentTicket).toHaveBeenCalledWith("azure:contoso:Web:3", "## VERIFICACIÓN\n**cumple**");
  });

  test("with no ticket linked there is nothing to publish onto", async () => {
    const ok = await useTicketStore.getState().comment("cualquier cosa");

    expect(ok).toBe(false);
    expect(api.commentTicket).not.toHaveBeenCalled();
  });

  test("a second press while the first is in flight is ignored", async () => {
    useTicketStore.setState({ linked: ticket("3"), commenting: true });

    // A board is the one place a duplicate cannot be taken back from inside the app, so the guard is
    // state rather than only a disabled attribute on the button.
    expect(await useTicketStore.getState().comment("uno")).toBe(false);
    expect(api.commentTicket).not.toHaveBeenCalled();
  });

  test("a refusal is reported rather than swallowed", async () => {
    useTicketStore.setState({ linked: ticket("3") });
    vi.mocked(api.commentTicket).mockRejectedValue(new Error("CREDENTIAL_REFUSED: the PAT expired"));

    expect(await useTicketStore.getState().comment("uno")).toBe(false);
    expect(toasts[0]).toContain("CREDENTIAL_REFUSED");
    // And the flag comes back down, or the button would stay dead for the rest of the session.
    expect(useTicketStore.getState().commenting).toBe(false);
  });
});
