import { useEffect, useState } from "react";
import { ExternalLink, FolderOpen, RotateCw, SquareKanban, Trash2 } from "lucide-react";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import { Chip } from "../common/Chip";
import { EmptyState } from "../common/EmptyState";
import { PanelHeader } from "../common/PanelHeader";
import { CARD } from "../common/panelChrome";
import { LinkTicketModal } from "./LinkTicketModal";
import { TicketDetail } from "./TicketDetail";
import { openExternalUrl, revealInFileManager } from "../../lib/ipc/commands";
import { useTicketStore } from "../../state/ticketStore";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { useRepoStore } from "../../state/repoStore";
import { useUiStore } from "../../state/uiStore";
import { useT } from "../../state/languageStore";
import type { Ticket, TicketLink } from "../../types/domain";

/**
 * The work-items module: what this branch is working on, and the tickets linked before it.
 *
 * The branch's own ticket leads because that is the question the module answers — the review that
 * runs before a commit judges against exactly this one, and a list where it is merely one row
 * among many buries the thing everything else depends on.
 */
export function WorkItemsView() {
  const t = useT();
  const projectId = useWorkspaceStore((s) => s.activeProjectId);
  const workspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);
  const branch = useRepoStore((s) => s.status?.current_branch ?? null);

  const { tickets, linked, account, loading, selectedId } = useTicketStore();
  const load = useTicketStore((s) => s.load);
  const select = useTicketStore((s) => s.select);
  const unlink = useTicketStore((s) => s.unlink);
  const refresh = useTicketStore((s) => s.refresh);

  const [linking, setLinking] = useState(false);

  // The workspace still gates the module — without one there is no project either — but the list
  // itself is this repository's, so that is all `load` is given.
  useEffect(() => {
    if (workspaceId && projectId) void load(projectId, branch);
  }, [load, workspaceId, projectId, branch]);

  // The branch's own ticket may not be in the workspace list yet on the very first render after
  // linking, so it is searched for there too — `linked` is authoritative for this branch.
  const selected =
    tickets.find((entry) => entry.ticket.id === selectedId) ??
    (linked && linked.id === selectedId ? { ticket: linked, links: [] } : null);

  if (!projectId || !workspaceId) {
    return <EmptyState icon={SquareKanban} title={t("tabbar.workitems")} />;
  }

  // Said before anything else is attempted: with several organisations connected and nothing
  // choosing between them, a list would be empty for a reason that looks like Azure's fault.
  if (account?.source === "none") {
    return <AccountUndecided />;
  }

  return (
    <div className="flex h-full min-h-0 gap-3">
      <div className={`${CARD} flex min-h-0 w-80 shrink-0 flex-col`}>
        <PanelHeader title="tabbar.workitems" icon={SquareKanban}>
          {account?.org && (
            <span className="truncate text-badge text-[var(--cf-text-muted)]">
              {t("tickets.accountFrom", { org: account.org, project: account.project ?? "—" })}
            </span>
          )}
        </PanelHeader>

        <div className="min-h-0 flex-1 overflow-y-auto p-2">
          <p className="px-1 pb-1 text-badge uppercase text-[var(--cf-text-muted)]">
            {t("tickets.linkedTo")}
          </p>

          {linked ? (
            <TicketRow
              ticket={linked}
              active={selectedId === linked.id}
              onSelect={() => select(linked.id)}
            />
          ) : (
            <div className="rounded-card border border-dashed border-[var(--cf-border)] p-3">
              <p className="text-body">{t("tickets.noneLinked")}</p>
              <p className="mt-1 text-ui text-[var(--cf-text-muted)]">{t("tickets.noneLinkedHint")}</p>
            </div>
          )}

          <Button
            variant="secondary"
            size="sm"
            icon={SquareKanban}
            className="mt-2 w-full"
            disabled={!branch}
            onClick={() => setLinking(true)}
          >
            {t("tickets.link")}
          </Button>

          {tickets.length > 0 && (
            <>
              <p className="px-1 pb-1 pt-4 text-badge uppercase text-[var(--cf-text-muted)]">
                {t("tickets.recent")}
              </p>
              {tickets
                .filter((entry) => entry.ticket.id !== linked?.id)
                .map((entry) => (
                  <TicketRow
                    key={entry.ticket.id}
                    ticket={entry.ticket}
                    // Where it belongs, on every row. The list spans the workspace, so without this
                    // a ticket from another repository is indistinguishable from this branch's.
                    links={entry.links}
                    active={selectedId === entry.ticket.id}
                    onSelect={() => select(entry.ticket.id)}
                  />
                ))}
            </>
          )}

          {!loading && tickets.length === 0 && !linked && (
            <p className="p-3 text-ui text-[var(--cf-text-muted)]">{t("tickets.empty")}</p>
          )}
        </div>
      </div>

      <div className={`${CARD} flex min-h-0 flex-1 flex-col`}>
        {selected ? (
          <>
            <PanelHeader
              title="tickets.detailTitle"
              titleParams={{ id: selected.ticket.external_id, title: selected.ticket.title }}
            >
              <IconButton
                label="tickets.refresh"
                icon={RotateCw}
                onClick={() => void refresh(selected.ticket)}
              />
              <IconButton
                label="tickets.openFolder"
                icon={FolderOpen}
                onClick={() => void revealInFileManager(selected.ticket.mirror_path)}
              />
              <IconButton
                label="tickets.openInBrowser"
                icon={ExternalLink}
                onClick={() => void openExternalUrl(selected.ticket.web_url)}
              />
              {linked?.id === selected.ticket.id && branch && (
                <IconButton
                  label="tickets.unlink"
                  icon={Trash2}
                  variant="danger"
                  onClick={() => void unlink(projectId, branch)}
                />
              )}
            </PanelHeader>
            <TicketDetail
              ticket={selected.ticket}
              links={selected.links}
              currentProjectId={projectId}
              currentBranch={branch}
            />
          </>
        ) : (
          // "This branch has no ticket" rather than "nothing to show": with the selection now
          // following the branch, an empty pane is a statement about the branch, and the action
          // that answers it belongs right there.
          <div className="flex h-full flex-col items-center justify-center gap-2 p-6 text-center">
            <SquareKanban size={28} className="text-[var(--cf-text-muted)]" aria-hidden />
            <p className="text-body font-medium">{t("tickets.noneLinked")}</p>
            <p className="max-w-xs text-ui text-[var(--cf-text-muted)]">{t("tickets.noneLinkedHint")}</p>
            <Button
              variant="primary"
              size="sm"
              icon={SquareKanban}
              className="mt-1"
              disabled={!branch}
              onClick={() => setLinking(true)}
            >
              {t("tickets.link")}
            </Button>
          </div>
        )}
      </div>

      {linking && branch && (
        <LinkTicketModal projectId={projectId} branch={branch} onClose={() => setLinking(false)} />
      )}
    </div>
  );
}

/** How many links a row spells out before it gives up and counts the rest. */
const LINKS_SHOWN = 2;

function TicketRow({
  ticket,
  links,
  active,
  onSelect,
}: {
  ticket: Ticket;
  /** Empty for the branch's own ticket, whose heading already says where it belongs. */
  links?: TicketLink[];
  active: boolean;
  onSelect: () => void;
}) {
  const shown = links?.slice(0, LINKS_SHOWN) ?? [];
  const rest = (links?.length ?? 0) - shown.length;

  return (
    <button
      type="button"
      onClick={onSelect}
      aria-current={active}
      className={`cf-focusable mb-1 flex w-full flex-col gap-1 rounded-control px-2 py-1.5 text-left ${
        active ? "bg-[var(--cf-active)]" : "hover:bg-[var(--cf-hover)]"
      }`}
    >
      <span className="flex items-center gap-1.5">
        <span className="font-mono text-ui text-[var(--cf-text-muted)]">{ticket.external_id}</span>
        <Chip tone="accent">{ticket.state}</Chip>
      </span>
      <span className="line-clamp-2 text-body">{ticket.title}</span>
      {shown.map((link) => (
        <span
          key={`${link.project_id}:${link.branch}`}
          className="truncate text-badge text-[var(--cf-text-muted)]"
        >
          ↳ {link.project_name} · {link.branch}
        </span>
      ))}
      {/* Two fit; past that the row would grow taller than the title it is describing. */}
      {rest > 0 && <span className="text-badge text-[var(--cf-text-muted)]">+{rest}</span>}
    </button>
  );
}

/** Nothing decided which organisation to read, so it asks instead of showing an empty board. */
function AccountUndecided() {
  const t = useT();
  const openSettings = useUiStore((s) => s.openSettings);

  return (
    <div className={`${CARD} flex h-full items-center justify-center`}>
      <div className="max-w-md text-center">
        <SquareKanban size={28} className="mx-auto mb-3 text-[var(--cf-text-muted)]" />
        <p className="text-relaxed">{t("tickets.accountNone")}</p>
        <p className="mt-2 text-body text-[var(--cf-text-muted)]">{t("tickets.accountNoneHint")}</p>
        <Button variant="primary" className="mt-4" onClick={() => openSettings("integrations")}>
          {t("tickets.accountOpenSettings")}
        </Button>
      </div>
    </div>
  );
}
