import { useEffect, useMemo, useState } from "react";
import { Link2, SquareKanban } from "lucide-react";
import { PickerModal } from "../common/PickerModal";
import { Button } from "../common/Button";
import { Chip } from "../common/Chip";
import { Tabs } from "../common/Tabs";
import { resolveTicketLink, suggestTicketForBranch } from "../../lib/ipc/commands";
import { useTicketStore, type TicketAddress, type TicketSource } from "../../state/ticketStore";
import { useUiStore } from "../../state/uiStore";
import { useT } from "../../state/languageStore";
import type { TicketSummary } from "../../types/domain";

/** How long the field settles before a lookup goes out. */
const PREVIEW_DEBOUNCE_MS = 350;

/**
 * Picks the ticket a branch is work for.
 *
 * <b>Pasting the address is the path, and browsing the board is the fallback.</b> That order is the
 * whole point of this component's shape: a work item's URL already names its organisation, its
 * project and its id, so pasting it needs no configuration at all — while the sprint and
 * assigned-to-me lists need an organisation *and* a board project to have been chosen, which a
 * repository hosted anywhere but Azure has no way of supplying on its own.
 *
 * It used to be the other way round. The lists led, pasting a URL was a side effect of typing in the
 * filter, and the resolved row's title was the placeholder text. Worse, the URL's own organisation
 * and project were parsed and then discarded, so linking fell back to the workspace's account and
 * did nothing — silently — when that account was incomplete.
 *
 * The two lists are kept because a board holds thousands of work items and a flat search over them
 * is a worse tool than the browser the user already has open; a real board measured 46 rows in its
 * current sprint. They are labelled as ways to *find* a ticket, because reading them as a condition
 * on what can be linked is exactly the misunderstanding the old layout invited.
 */
export function LinkTicketModal({
  projectId,
  branch,
  onClose,
}: {
  projectId: string;
  branch: string;
  onClose: () => void;
}) {
  const t = useT();
  const [filter, setFilter] = useState("");
  const [source, setSource] = useState<TicketSource>("sprint");

  const account = useTicketStore((s) => s.account);
  const browse = useTicketStore((s) => s.browse);
  const browseFor = useTicketStore((s) => s.browseFor);
  const link = useTicketStore((s) => s.link);

  // A board is browsable only once something names one. Pasting an address never needs this, which
  // is why the two halves are gated separately — the old dialog let a missing account take the
  // whole thing down, including the half that would have worked.
  const browsable = Boolean(account?.org && account.project);

  useEffect(() => {
    if (browsable) void browseFor(source);
  }, [browseFor, source, browsable]);

  const trimmed = filter.trim();

  /**
   * What the typed text addresses, kept with the text it was for.
   *
   * Two stages: parsing is local and runs per keystroke, the lookup is a REST call and is debounced.
   * Storing the answer against its own input is what stops an in-flight lookup from rendering a row
   * that belongs to what the field used to say, and it keeps the empty case derived at render rather
   * than written back from an effect.
   */
  const [addressed, setAddressed] = useState<{ forFilter: string; address: TicketAddress } | null>(null);
  const [previewed, setPreviewed] = useState<{
    forExternalId: string;
    summary: TicketSummary | null;
  } | null>(null);

  useEffect(() => {
    if (trimmed.length === 0) return;

    let cancelled = false;
    void resolveTicketLink(trimmed)
      .then((reference) => {
        if (cancelled || !reference) return;
        setAddressed({
          forFilter: trimmed,
          address: {
            org: reference.org,
            project: reference.project,
            externalId: String(reference.id),
          },
        });
      })
      .catch(() => {
        // A string that is not an address is the normal case while typing a title.
      });

    return () => {
      cancelled = true;
    };
  }, [trimmed]);

  const address = addressed?.forFilter === trimmed ? addressed.address : null;
  const externalId = address?.externalId ?? null;

  // Debounced because a bare id parses on its first digit: without this, typing `426647` would fire
  // six lookups, five of them for work items nobody meant.
  useEffect(() => {
    if (!address || !externalId) return;

    let cancelled = false;
    const timer = setTimeout(() => {
      void useTicketStore
        .getState()
        .preview(address)
        .then((summary) => {
          if (!cancelled) setPreviewed({ forExternalId: externalId, summary });
        });
    }, PREVIEW_DEBOUNCE_MS);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [address, externalId]);

  const preview = previewed?.forExternalId === externalId ? previewed.summary : undefined;

  // The branch name usually already says which ticket this is. Offered, never applied: the
  // heuristic accepts `release/2025-cleanup` as work item 2025, and that has to be rejectable.
  const [suggested, setSuggested] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    void suggestTicketForBranch(branch)
      .then((hit) => {
        if (!cancelled) setSuggested(hit && hit.provider === "azure" ? hit.external_id : null);
      })
      .catch(() => {
        if (!cancelled) setSuggested(null);
      });
    return () => {
      cancelled = true;
    };
  }, [branch]);

  const rows = useMemo(() => {
    if (browse.status !== "loaded") return [];
    const needle = trimmed.toLowerCase();
    if (needle.length === 0) return browse.rows;
    return browse.rows.filter(
      (row) => row.title.toLowerCase().includes(needle) || row.external_id.includes(needle),
    );
  }, [browse, trimmed]);

  const choose = async (chosen: TicketAddress) => {
    onClose();
    await link(projectId, branch, chosen);
  };

  return (
    <PickerModal
      placeholder={t("tickets.pickPlaceholder")}
      value={filter}
      onValueChange={setFilter}
      onClose={onClose}
      size="lg"
    >
      {address && (
        <ResolvedAddress
          address={address}
          summary={preview}
          fallbackOrg={account?.org ?? null}
          fallbackProject={account?.project ?? null}
          onLink={() => void choose(address)}
        />
      )}

      {suggested && !address && (
        <button
          type="button"
          onClick={() => void choose({ org: null, project: null, externalId: suggested })}
          className="cf-focusable m-1 flex w-[calc(100%-0.5rem)] items-center gap-2 rounded-control px-2 py-1.5 text-left text-body hover:bg-[var(--cf-hover)]"
        >
          <SquareKanban size={14} className="shrink-0 text-[var(--cf-text-muted)]" />
          <span className="font-mono text-ui text-[var(--cf-text-muted)]">{suggested}</span>
          <Chip tone="accent">{t("tickets.suggested")}</Chip>
        </button>
      )}

      <div className="border-t border-[var(--cf-border)] px-3 pt-2">
        <p className="text-ui font-medium">{t("tickets.pickBrowseTitle")}</p>
        <p className="mt-0.5 text-badge text-[var(--cf-text-muted)]">{t("tickets.pickBrowseHint")}</p>
      </div>

      {browsable ? (
        <>
          <div className="px-2">
            <Tabs<TicketSource>
              activeId={source}
              onSelect={setSource}
              layoutId="ticket-source"
              label={t("tickets.pickBrowseTitle")}
              options={[
                { id: "sprint", labelKey: "tickets.sourceSprint" },
                { id: "mine", labelKey: "tickets.sourceMine" },
              ]}
            />
          </div>

          <div className="max-h-[40vh] overflow-y-auto p-1">
            {browse.status === "loading" && (
              <p className="p-3 text-body text-[var(--cf-text-muted)]">{t("tickets.pickLoading")}</p>
            )}

            {browse.status === "failed" && (
              // The sidecar's own message names a wrong organisation or an expired token
              // (DIVERGENCE-PROV-c), so it is shown rather than replaced with "something failed".
              <p className="p-3 text-body text-[var(--cf-danger)]">{browse.message}</p>
            )}

            {browse.status === "loaded" && rows.length === 0 && (
              <p className="p-3 text-body text-[var(--cf-text-muted)]">{t("tickets.pickEmpty")}</p>
            )}

            {rows.map((row) => (
              <BoardRow
                key={row.external_id}
                row={row}
                onChoose={() =>
                  void choose({
                    // The board being listed is the account's, so its rows carry no address of
                    // their own — unlike a pasted URL, which does.
                    org: null,
                    project: null,
                    externalId: row.external_id,
                  })
                }
              />
            ))}
          </div>
        </>
      ) : (
        <NoBoardConfigured />
      )}
    </PickerModal>
  );
}

/** The work item a pasted address points at, with the board it lives on spelled out. */
function ResolvedAddress({
  address,
  summary,
  fallbackOrg,
  fallbackProject,
  onLink,
}: {
  address: TicketAddress;
  /** `undefined` while the lookup is out, `null` when the board holds no such work item. */
  summary: TicketSummary | null | undefined;
  fallbackOrg: string | null;
  fallbackProject: string | null;
  onLink: () => void;
}) {
  const t = useT();
  const org = address.org ?? fallbackOrg;
  const project = address.project ?? fallbackProject;

  return (
    <div className="m-1 rounded-card border border-[var(--cf-accent)] bg-[var(--cf-surface)] p-3">
      <div className="flex items-center gap-2">
        <Link2 size={14} className="shrink-0 text-[var(--cf-accent)]" aria-hidden />
        <span className="font-mono text-ui text-[var(--cf-text-muted)]">#{address.externalId}</span>
        {/* Where it lives, always. This is what makes linking a ticket from another project safe to
            do without a confirmation step: the answer is on screen instead of being asked for. */}
        {project && <Chip>{project}</Chip>}
        {org && <Chip>{org}</Chip>}
        <span className="ml-auto shrink-0">
          <Button variant="primary" size="sm" icon={Link2} onClick={onLink}>
            {t("tickets.pickLinkAction")}
          </Button>
        </span>
      </div>

      <p className="mt-1.5 text-body">
        {summary === undefined ? (
          <span className="text-[var(--cf-text-muted)]">{t("tickets.pickResolving")}</span>
        ) : summary === null ? (
          <span className="text-[var(--cf-warning)]">
            {t("tickets.pickNotFound", { id: address.externalId })}
          </span>
        ) : (
          summary.title
        )}
      </p>

      {summary && (
        <div className="mt-1.5 flex flex-wrap items-center gap-1.5">
          <Chip>{summary.work_item_type}</Chip>
          <Chip tone="accent">{summary.state}</Chip>
          {summary.assigned_to && (
            <span className="text-badge text-[var(--cf-text-muted)]">{summary.assigned_to}</span>
          )}
        </div>
      )}
    </div>
  );
}

/** Says the board is unset — and only about the half that needs one. */
function NoBoardConfigured() {
  const t = useT();
  const openSettings = useUiStore((s) => s.openSettings);

  return (
    <div className="p-4 text-center">
      <p className="text-body text-[var(--cf-text-muted)]">{t("tickets.accountNone")}</p>
      <Button variant="secondary" size="sm" className="mt-2" onClick={() => openSettings("integrations")}>
        {t("tickets.accountOpenSettings")}
      </Button>
    </div>
  );
}

function BoardRow({ row, onChoose }: { row: TicketSummary; onChoose: () => void }) {
  return (
    <button
      type="button"
      onClick={onChoose}
      className="cf-focusable flex w-full items-center gap-2 rounded-control px-2 py-1.5 text-left text-body hover:bg-[var(--cf-hover)]"
    >
      <SquareKanban size={14} className="shrink-0 text-[var(--cf-text-muted)]" />
      <span className="shrink-0 font-mono text-ui text-[var(--cf-text-muted)]">{row.external_id}</span>
      <span className="truncate">{row.title}</span>
      <span className="ml-auto flex shrink-0 items-center gap-1.5">
        <Chip>{row.work_item_type}</Chip>
        <Chip tone="accent">{row.state}</Chip>
        {row.assigned_to && (
          <span className="text-badge text-[var(--cf-text-muted)]">{row.assigned_to}</span>
        )}
      </span>
    </button>
  );
}
