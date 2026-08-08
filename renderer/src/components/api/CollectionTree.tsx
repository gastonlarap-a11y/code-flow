import { Fragment, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import {
  Boxes,
  ChevronDown,
  ChevronRight,
  Copy,
  FilePlus,
  Folder,
  FolderOpen,
  FolderPlus,
  MoreHorizontal,
  Plus,
  Pencil,
  Play,
  Share2,
  Trash2,
  type LucideIcon,
} from "lucide-react";
import { EmptyState } from "../common/EmptyState";
import { IconButton } from "../common/IconButton";
import { Tooltip } from "../common/Tooltip";
import { DragGhost } from "../common/DragGhost";
import { VirtualizedTree } from "../common/VirtualizedTree";
import { useTreeVirtualizer } from "../../lib/useTreeVirtualizer";
import { treeIndent } from "../../lib/ui/treeIndent";
import { menuKeyAction, type MenuItemState } from "../../lib/ui/menuNavigation";
import { badgeColor, badgeLabel } from "./methodStyle";
import { flattenCollectionTree, type CollectionTreeRow } from "../../lib/collectionTreeFlatten";
import { DRAG_THRESHOLD, setDragCursor } from "../../lib/pointerDrag";
import { canDrop, useApiDragStore, type ApiDrag, type ApiDropZone } from "../../state/apiDragStore";
import { useApiTabsStore } from "../../state/apiTabsStore";
import { useApiTreeStore } from "../../state/apiTreeStore";
import { useApiModalStore } from "../../state/apiModalStore";
import { useRowHoverStore } from "../../state/rowHoverStore";
import { confirmAction } from "../../state/confirmStore";
import { useT } from "../../state/languageStore";
import { defaultRequestSpec } from "../../types/api";
import type { ApiFolder, ApiProtocol, ApiRequestRow } from "../../types/api";

/** Dimmed, never hidden — `opacity-0` is what made these two actions undiscoverable. */
const ROW_ACTION = "opacity-55 group-hover:opacity-100 group-focus-within:opacity-100";

/** How much of a folder row's height, top and bottom, aims *between* rows rather than into it.
 * Small enough that the middle — "into this folder" — is what you hit without trying. */
const EDGE_FRACTION = 0.28;

/** How long the pointer has to rest on a collapsed container before it springs open. Without it a
 * deep move is impossible: you can't drop into a folder you can't see the inside of. */
const SPRING_LOAD_MS = 600;

type ApiNodeKind = "collection" | "folder" | "request";

interface NodeRef {
  kind: ApiNodeKind;
  id: string;
  collectionId: string;
  /** The container the node lives in — a folder's `parent_id`, a request's `folder_id`. */
  parentId: string | null;
  name: string;
}

/** An in-progress "new request"/"new folder": the inline input the explorer uses, not a modal. */
interface Draft {
  kind: "folder" | "request";
  collectionId: string;
  parentId: string | null;
}

// ---------------------------------------------------------------------------
// Badges — shared with the sidebar's search results and the history list
// ---------------------------------------------------------------------------

/**
 * The little uppercase tag in front of every request row — a verb for HTTP, the protocol name for
 * everything else. Colour and wording come from `methodStyle` so the tree, the tab strip and the
 * URL bar can never disagree about what a POST looks like.
 */
export function MethodBadge({ protocol, method }: { protocol: ApiProtocol; method: string }) {
  return (
    <span
      className="w-[38px] shrink-0 truncate text-right font-mono text-badge font-bold uppercase leading-none tracking-tight"
      style={{ color: badgeColor(protocol, method) }}
    >
      {badgeLabel(protocol, method)}
    </span>
  );
}

// ---------------------------------------------------------------------------
// Context menu — shared with the sidebar's "…" button
// ---------------------------------------------------------------------------

export interface MenuItem extends MenuItemState {
  label: string;
  icon: LucideIcon;
  onClick: () => void;
  danger?: boolean;
  /** Draws a hairline above this item. */
  separated?: boolean;
}

/**
 * A floating menu at a point, portalled so no scroll container can clip it. Positioned after
 * mount because its size is only known once it's rendered — the clamp is what keeps a menu opened
 * near the bottom of the window from hanging off the edge.
 *
 * It is opened two ways — right-clicking a row and pressing the row's "…" — and both have to land
 * on the same menu, which is why the button does not use `RowActions` instead. Once that button
 * became reachable by Tab, this had to answer the keyboard too: arrows, Home/End and Enter come
 * from `lib/ui/menuNavigation.ts`, the same tested reducer `RowActions` runs on, and focus moves to
 * the active item so a screen reader announces each one as it is reached.
 */
export function ContextMenu({
  x,
  y,
  items,
  onClose,
}: {
  x: number;
  y: number;
  items: MenuItem[];
  onClose: () => void;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState({ left: x, top: y });
  const [activeIndex, setActiveIndex] = useState(-1);

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    setPos({
      left: Math.max(4, Math.min(x, window.innerWidth - rect.width - 4)),
      top: Math.max(4, Math.min(y, window.innerHeight - rect.height - 4)),
    });
  }, [x, y]);

  // Focus lands inside on open, so the menu is where the keyboard already is — and Escape can then
  // return it to the row, which `onClose` does on the caller's side by re-rendering the tree.
  useEffect(() => {
    ref.current?.focus();
  }, []);

  useEffect(() => {
    if (activeIndex < 0) return;
    ref.current?.querySelectorAll<HTMLElement>('[role="menuitem"]')[activeIndex]?.focus();
  }, [activeIndex]);

  useEffect(() => {
    const onPointerDown = (e: MouseEvent) => {
      if (!ref.current?.contains(e.target as Node)) onClose();
    };
    document.addEventListener("mousedown", onPointerDown);
    window.addEventListener("resize", onClose);
    // Capture phase: the scroll that matters happens inside the sidebar, not on the window, and
    // a menu left floating over rows that have moved on points at the wrong node.
    window.addEventListener("scroll", onClose, true);
    return () => {
      document.removeEventListener("mousedown", onPointerDown);
      window.removeEventListener("resize", onClose);
      window.removeEventListener("scroll", onClose, true);
    };
  }, [onClose]);

  const onKeyDown = (event: React.KeyboardEvent) => {
    const action = menuKeyAction(event.key, items, activeIndex);
    if (action.kind === "none") return;
    event.preventDefault();
    if (action.kind === "close") return onClose();
    if (action.kind === "move") return setActiveIndex(action.index);
    onClose();
    items[action.index]!.onClick();
  };

  return createPortal(
    <div
      ref={ref}
      role="menu"
      // The menu itself is focusable so the first arrow key has somewhere to start from.
      tabIndex={-1}
      onKeyDown={onKeyDown}
      style={{ position: "fixed", left: pos.left, top: pos.top }}
      className="z-[9999] min-w-[172px] rounded-md border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-1 shadow-[var(--cf-shadow)] outline-none"
    >
      {items.map((item, i) => (
        <Fragment key={`${item.label}-${i}`}>
          {item.separated && i > 0 && <div className="my-1 h-px bg-[var(--cf-border)]" role="separator" />}
          <button
            role="menuitem"
            type="button"
            // The menu owns arrow navigation; items are reached through it, not through Tab.
            tabIndex={-1}
            onClick={() => {
              onClose();
              item.onClick();
            }}
            className={`cf-focusable flex w-full items-center gap-2 rounded px-2 py-1 text-left text-ui hover:bg-[color-mix(in_oklab,var(--cf-accent)_16%,transparent)] ${
              item.danger ? "text-[var(--cf-danger)]" : "text-[var(--cf-text)]"
            }`}
          >
            <item.icon size={13} className="shrink-0 opacity-70" />
            <span className="truncate">{item.label}</span>
          </button>
        </Fragment>
      ))}
    </div>,
    document.body,
  );
}

// ---------------------------------------------------------------------------
// Tree grouping
// ---------------------------------------------------------------------------

interface GroupedTree {
  folders: Map<string, ApiFolder[]>;
  requests: Map<string, ApiRequestRow[]>;
}

/** `\0` can't appear in a uuid, so no pair of ids can collide into one key. */
const containerKey = (collectionId: string, parentId: string | null) =>
  `${collectionId}\u0000${parentId ?? ""}`;

function pushInto<T>(map: Map<string, T[]>, key: string, value: T) {
  const existing = map.get(key);
  if (existing) existing.push(value);
  else map.set(key, [value]);
}

function groupTree(folders: ApiFolder[], requests: ApiRequestRow[]): GroupedTree {
  const grouped: GroupedTree = { folders: new Map(), requests: new Map() };
  for (const folder of [...folders].sort((a, b) => a.sort_order - b.sort_order)) {
    pushInto(grouped.folders, containerKey(folder.collection_id, folder.parent_id), folder);
  }
  for (const request of [...requests].sort((a, b) => a.sort_order - b.sort_order)) {
    pushInto(grouped.requests, containerKey(request.collection_id, request.folder_id), request);
  }
  return grouped;
}

/**
 * A gap in the list as *rendered* → the index `moveNode` wants.
 *
 * The rendered list still contains the row being dragged; the backend renumbers the destination
 * with it already removed. Every gap past its current position therefore shifts down by one, and
 * both gaps around it collapse onto the same slot — which is exactly what makes a drag that goes
 * nowhere a no-op instead of an off-by-one.
 */
function storeIndex(gap: number, draggedAt: number): number {
  return draggedAt >= 0 && gap > draggedAt ? gap - 1 : gap;
}

// ---------------------------------------------------------------------------
// Rows
// ---------------------------------------------------------------------------

interface TreeRowProps {
  node: NodeRef;
  depth: number;
  /** Containers only; `undefined` on a request. */
  expanded?: boolean;
  protocol?: ApiProtocol;
  method?: string;
  renaming: boolean;
  dragging: boolean;
  /** The pointer is aiming *into* this container, as opposed to at a gap beside it. */
  dropInto: boolean;
  onActivate: () => void;
  onMenu: (x: number, y: number) => void;
  /** Containers only: the inline "+" that starts a new request without opening the menu. */
  onQuickAdd?: () => void;
  onBeginDrag?: (e: React.PointerEvent<HTMLElement>) => void;
  onRename: (name: string) => void;
  onCancelRename: () => void;
  /** True once, right after a drag, so the trailing click doesn't also open the request. */
  suppressClick: () => boolean;
}

function TreeRow({
  node,
  depth,
  expanded,
  protocol,
  method,
  renaming,
  dragging,
  dropInto,
  onActivate,
  onMenu,
  onQuickAdd,
  onBeginDrag,
  onRename,
  onCancelRename,
  suppressClick,
}: TreeRowProps) {
  const t = useT();
  const hoverKey = `api:${node.id}`;
  // Only this row and the one being left re-render when the pointer moves between them, which is
  // what makes tracking hover in state affordable on a tree this size.
  const isHovered = useRowHoverStore((s) => s.key === hoverKey);
  const anyDrag = useApiDragStore((s) => s.drag !== null);
  const isContainer = node.kind !== "request";

  return (
    <Tooltip label={node.name}>
    <div
      data-cf-apirow={node.id}
      data-cf-apikind={node.kind}
      data-cf-apicol={node.collectionId}
      data-cf-apiparent={node.parentId ?? ""}
      role="treeitem"
      aria-expanded={isContainer ? expanded : undefined}
      // A div rather than a button because the row carries its own "…" button, and a button
      // inside a button is invalid — so focus and the Enter/Space activation come back by hand.
      tabIndex={renaming ? -1 : 0}
      onPointerDown={onBeginDrag}
      onPointerEnter={() => useRowHoverStore.getState().enter(hoverKey)}
      onPointerLeave={() => useRowHoverStore.getState().leave(hoverKey)}
      // Kills the browser's own press-and-sweep text selection without costing the `click` that
      // follows — preventing it on `pointerdown` would suppress that too.
      onMouseDown={(e) => e.preventDefault()}
      onClick={() => {
        if (suppressClick()) return;
        onActivate();
      }}
      onKeyDown={(e) => {
        if (renaming || e.target !== e.currentTarget) return;
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          onActivate();
        }
      }}
      onContextMenu={(e) => {
        e.preventDefault();
        onMenu(e.clientX, e.clientY);
      }}
      style={{ paddingLeft: treeIndent(depth) }}
      className={`group flex h-[var(--cf-row-height)] cursor-pointer items-center gap-1.5 rounded-md pr-1 text-body ${
        // Nothing but the drop target lights up while a drag is in flight.
        isHovered && !anyDrag ? "cf-row-hover" : ""
      } ${
        dropInto ? "bg-[var(--cf-accent-soft)] ring-1 ring-inset ring-[var(--cf-accent)]" : ""
      } ${dragging ? "opacity-40" : ""}`}
    >
      {isContainer ? (
        <>
          {expanded ? (
            <ChevronDown size={12} className="shrink-0 text-[var(--cf-text-muted)]" />
          ) : (
            <ChevronRight size={12} className="shrink-0 text-[var(--cf-text-muted)]" />
          )}
          {node.kind === "collection" ? (
            <Boxes size={13} className="shrink-0 text-[var(--cf-accent)]" />
          ) : expanded ? (
            <FolderOpen size={13} className="shrink-0 text-[var(--cf-text-muted)]" />
          ) : (
            <Folder size={13} className="shrink-0 text-[var(--cf-text-muted)]" />
          )}
        </>
      ) : (
        <>
          <span className="w-3 shrink-0" />
          <MethodBadge protocol={protocol ?? "http"} method={method ?? "GET"} />
        </>
      )}

      {renaming ? (
        <input
          autoFocus
          defaultValue={node.name}
          // The row above swallows presses to stop text selection and to arm the drag; the input
          // has to opt out of both or it can never take focus or a caret.
          onPointerDown={(e) => e.stopPropagation()}
          onMouseDown={(e) => e.stopPropagation()}
          onClick={(e) => e.stopPropagation()}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              onRename(e.currentTarget.value);
            } else if (e.key === "Escape") {
              e.preventDefault();
              onCancelRename();
            }
          }}
          onBlur={onCancelRename}
          className="min-w-0 flex-1 rounded-sm border border-[var(--cf-accent)] bg-[var(--cf-bg)] px-1 py-0 text-body text-[var(--cf-text)] outline-none"
        />
      ) : (
        <span
          className={`min-w-0 flex-1 truncate ${
            node.kind === "request" ? "text-[var(--cf-text)]" : "font-medium text-[var(--cf-text)]"
          }`}
        >
          {node.name || t("api.untitledRequest")}
        </span>
      )}

      {/* Creating a request is the overwhelmingly common thing to do to a collection, and it was
          two clicks behind the menu. Sits left of the overflow, revealed on hover like it. */}
      {onQuickAdd && (
        <IconButton
          label="api.newRequest"
          icon={Plus}
          className={`shrink-0 ${ROW_ACTION}`}
          onPointerDown={(e: React.PointerEvent) => e.stopPropagation()}
          onClick={(e: React.MouseEvent) => {
            e.stopPropagation();
            onQuickAdd();
          }}
        />
      )}

      {/* Not `RowActions`: this opens the very menu the row's right-click opens, and a second,
          differently-built menu for the same list of actions is how the two drift apart.
          `ContextMenu` is the one menu, and it is the one that grew keyboard navigation. */}
      <IconButton
        label="api.moreActions"
        icon={MoreHorizontal}
        className={`shrink-0 ${ROW_ACTION}`}
        onPointerDown={(e: React.PointerEvent) => e.stopPropagation()}
        onClick={(e: React.MouseEvent<HTMLButtonElement>) => {
          e.stopPropagation();
          const rect = e.currentTarget.getBoundingClientRect();
          onMenu(rect.left, rect.bottom + 2);
        }}
      />
    </div>
    </Tooltip>
  );
}

function DraftRow({
  kind,
  depth,
  onSubmit,
  onCancel,
}: {
  kind: "folder" | "request";
  depth: number;
  onSubmit: (name: string) => void;
  onCancel: () => void;
}) {
  const t = useT();
  return (
    <div
      style={{ paddingLeft: treeIndent(depth) }}
      className="flex h-[var(--cf-row-height)] items-center gap-1.5 pr-2 text-body"
    >
      <span className="w-3 shrink-0" />
      {kind === "folder" ? (
        <Folder size={13} className="shrink-0 text-[var(--cf-text-muted)]" />
      ) : (
        <FilePlus size={13} className="shrink-0 text-[var(--cf-text-muted)]" />
      )}
      <input
        autoFocus
        placeholder={t(kind === "folder" ? "api.untitledFolder" : "api.untitledRequest")}
        onKeyDown={(e) => {
          if (e.key === "Enter") {
            e.preventDefault();
            onSubmit(e.currentTarget.value);
          } else if (e.key === "Escape") {
            e.preventDefault();
            onCancel();
          }
        }}
        // Clicking away abandons the entry rather than committing it — a half-typed name losing
        // focus shouldn't leave a stray request behind.
        onBlur={onCancel}
        className="min-w-0 flex-1 rounded-sm border border-[var(--cf-accent)] bg-[var(--cf-bg)] px-1 py-0 text-body text-[var(--cf-text)] outline-none"
      />
    </div>
  );
}

// ---------------------------------------------------------------------------
// The tree
// ---------------------------------------------------------------------------

export function CollectionTree() {
  const t = useT();
  const collections = useApiTreeStore((s) => s.collections);
  const folders = useApiTreeStore((s) => s.folders);
  const requests = useApiTreeStore((s) => s.requests);

  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [renaming, setRenaming] = useState<NodeRef | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [menu, setMenu] = useState<{ x: number; y: number; node: NodeRef } | null>(null);
  const openModal = useApiModalStore((s) => s.openApiModal);

  const drag = useApiDragStore((s) => s.drag);
  const over = useApiDragStore((s) => s.over);
  const origin = useApiDragStore((s) => s.origin);

  const grouped = useMemo(() => groupTree(folders, requests), [folders, requests]);

  // The recursive tree, flattened to the rows currently visible — what the virtualizer windows.
  const rows = useMemo(
    () =>
      flattenCollectionTree({ collections, folders, requests, expanded, draft, dragging: drag !== null }),
    [collections, folders, requests, expanded, draft, drag],
  );
  const rowIndexById = useMemo(() => new Map(rows.map((row, index) => [row.id, index])), [rows]);
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const virtualizer = useTreeVirtualizer(rows, scrollRef);

  // The drag's hit-test runs on every pointer move and needs the same grouping the render used;
  // a ref keeps it reading the current one without rebuilding the maps per move.
  const groupedRef = useRef(grouped);
  groupedRef.current = grouped;
  const suppressClickRef = useRef(false);
  const renamingRef = useRef<NodeRef | null>(renaming);
  renamingRef.current = renaming;
  const ghostRef = useRef<HTMLDivElement | null>(null);

  const childFolders = (collectionId: string, parentId: string | null) =>
    grouped.folders.get(containerKey(collectionId, parentId)) ?? [];
  const childRequests = (collectionId: string, parentId: string | null) =>
    grouped.requests.get(containerKey(collectionId, parentId)) ?? [];

  const toggle = (id: string) =>
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const expand = (id: string) =>
    setExpanded((prev) => (prev.has(id) ? prev : new Set(prev).add(id)));

  // ---------- mutations ----------

  const startDraft = (kind: "folder" | "request", collectionId: string, parentId: string | null) => {
    expand(parentId ?? collectionId);
    setDraft({ kind, collectionId, parentId });
  };

  const submitDraft = async (name: string) => {
    if (!draft) return;
    const trimmed = name.trim();
    setDraft(null);
    const state = useApiTreeStore.getState();
    if (draft.kind === "folder") {
      await state.createFolder(draft.collectionId, draft.parentId, trimmed || t("api.untitledFolder"));
      return;
    }
    const created = await state.createRequest(
      draft.collectionId,
      draft.parentId,
      trimmed || t("api.untitledRequest"),
      defaultRequestSpec(),
    );
    // A request you just named is a request you want to edit, so it opens straight away.
    if (created) useApiTabsStore.getState().openRequest(created.id);
  };

  const commitRename = async (node: NodeRef, name: string) => {
    setRenaming(null);
    const trimmed = name.trim();
    if (!trimmed || trimmed === node.name) return;
    const state = useApiTreeStore.getState();
    if (node.kind === "collection") {
      const collection = state.collections.find((c) => c.id === node.id);
      if (collection) await state.updateCollection({ ...collection, name: trimmed });
    } else if (node.kind === "folder") {
      const folder = state.folders.find((f) => f.id === node.id);
      if (folder) await state.updateFolder({ ...folder, name: trimmed });
    } else {
      const request = state.requests.find((r) => r.id === node.id);
      if (request) await state.updateRequest({ ...request, name: trimmed });
    }
  };

  const remove = async (node: NodeRef) => {
    const message =
      node.kind === "collection"
        ? t("api.deleteCollectionConfirm", { name: node.name })
        : node.kind === "folder"
          ? t("api.deleteFolderConfirm", { name: node.name })
          : t("api.deleteRequestConfirm", { name: node.name });
    if (!(await confirmAction(message, true, t("api.delete")))) return;
    const state = useApiTreeStore.getState();
    if (node.kind === "collection") await state.deleteCollection(node.id);
    else if (node.kind === "folder") await state.deleteFolder(node.id);
    else await state.deleteRequest(node.id);
  };

  const menuItems = (node: NodeRef): MenuItem[] => {
    const items: MenuItem[] = [];
    if (node.kind !== "request") {
      const parentId = node.kind === "collection" ? null : node.id;
      items.push({
        label: t("api.newRequest"),
        icon: FilePlus,
        onClick: () => startDraft("request", node.collectionId, parentId),
      });
      items.push({
        label: t("api.newFolder"),
        icon: FolderPlus,
        onClick: () => startDraft("folder", node.collectionId, parentId),
      });
      items.push({
        label: t("api.runner.run"),
        icon: Play,
        separated: true,
        onClick: () => openModal({ kind: "runner", collectionId: node.collectionId, folderId: parentId }),
      });
    }
    if (node.kind === "collection") {
      items.push({
        label: t("api.export.title"),
        icon: Share2,
        onClick: () => openModal({ kind: "export", collectionId: node.id }),
      });
    }
    items.push({
      label: t("api.rename"),
      icon: Pencil,
      separated: node.kind !== "request",
      onClick: () => setRenaming(node),
    });
    if (node.kind !== "folder") {
      items.push({
        label: t("api.duplicate"),
        icon: Copy,
        onClick: () =>
          node.kind === "collection"
            ? void useApiTreeStore.getState().duplicateCollection(node.id)
            : void useApiTreeStore.getState().duplicateRequest(node.id),
      });
    }
    items.push({ label: t("api.delete"), icon: Trash2, danger: true, separated: true, onClick: () => void remove(node) });
    return items;
  };

  // ---------- drag ----------

  /**
   * Where a drop at (x, y) would land, or `null` when it can't land there.
   *
   * The pointer is hit-tested against the rows' `data-cf-api*` markers rather than tracked with
   * per-row handlers, which is what lets a row stand in for a slot beside it: the top and bottom
   * slivers of a folder aim at the gaps around it, the middle aims inside, and a request — which
   * can't contain anything — splits cleanly in half.
   */
  const zoneAt = (x: number, y: number, dragged: ApiDrag): ApiDropZone | null => {
    const element = document.elementFromPoint(x, y);
    const row = element?.closest<HTMLElement>("[data-cf-apirow]") ?? null;
    const kind = row?.dataset.cfApikind as ApiNodeKind | undefined;
    const id = row?.dataset.cfApirow;
    if (!row || !kind || !id) return null;

    const list = (collectionId: string, parentId: string | null) =>
      dragged.kind === "folder"
        ? (groupedRef.current.folders.get(containerKey(collectionId, parentId)) ?? [])
        : (groupedRef.current.requests.get(containerKey(collectionId, parentId)) ?? []);

    const into = (collectionId: string, parentId: string | null): ApiDropZone => {
      const siblings = list(collectionId, parentId);
      const at = siblings.findIndex((n) => n.id === dragged.id);
      return { collectionId, parentId, index: storeIndex(siblings.length, at), mode: "into" };
    };

    const between = (
      collectionId: string,
      parentId: string | null,
      rowKind: "folder" | "request",
      rowId: string,
      side: "before" | "after",
    ): ApiDropZone => {
      const siblings = list(collectionId, parentId);
      const at = siblings.findIndex((n) => n.id === dragged.id);
      let gap: number;
      if (rowKind === dragged.kind) {
        const index = siblings.findIndex((n) => n.id === rowId);
        gap = index < 0 ? siblings.length : side === "before" ? index : index + 1;
      } else {
        // Folders always render above requests inside a container, so a gap in the *other* list
        // resolves to this one's near edge. The indicator then redraws where the node really
        // lands, rather than lying about the slot the pointer happens to be in.
        gap = dragged.kind === "folder" ? siblings.length : 0;
      }
      return { collectionId, parentId, index: storeIndex(gap, at), mode: "between" };
    };

    const collectionId = kind === "collection" ? id : (row.dataset.cfApicol ?? "");
    const parentId = row.dataset.cfApiparent ? row.dataset.cfApiparent : null;
    const rect = row.getBoundingClientRect();
    const fraction = rect.height > 0 ? (y - rect.top) / rect.height : 0.5;

    let zone: ApiDropZone;
    if (kind === "collection") {
      zone = into(id, null);
    } else if (kind === "folder") {
      if (fraction < EDGE_FRACTION) zone = between(collectionId, parentId, "folder", id, "before");
      else if (fraction > 1 - EDGE_FRACTION) zone = between(collectionId, parentId, "folder", id, "after");
      else zone = into(collectionId, id);
    } else {
      zone = between(collectionId, parentId, "request", id, fraction < 0.5 ? "before" : "after");
    }
    return canDrop(dragged, zone, useApiTreeStore.getState().folders) ? zone : null;
  };

  const isCurrentSlot = (dragged: ApiDrag, zone: ApiDropZone): boolean => {
    const siblings =
      dragged.kind === "folder"
        ? (groupedRef.current.folders.get(containerKey(zone.collectionId, zone.parentId)) ?? [])
        : (groupedRef.current.requests.get(containerKey(zone.collectionId, zone.parentId)) ?? []);
    const at = siblings.findIndex((n) => n.id === dragged.id);
    return at >= 0 && at === zone.index;
  };

  const beginDrag = (e: React.PointerEvent<HTMLElement>, node: NodeRef) => {
    if (e.button !== 0 || node.kind === "collection" || renamingRef.current) return;
    const from = { x: e.clientX, y: e.clientY };
    const dragged: ApiDrag = {
      kind: node.kind === "folder" ? "folder" : "request",
      id: node.id,
      collectionId: node.collectionId,
      name: node.name,
    };
    let started = false;
    let spring: { id: string; timer: number } | null = null;

    /** Rests the pointer on a container long enough and it opens — otherwise a folder that starts
     * collapsed can never be dropped into. */
    const armSpring = (id: string | null) => {
      if (spring?.id === id) return;
      if (spring) clearTimeout(spring.timer);
      spring = null;
      if (id === null) return;
      spring = { id, timer: window.setTimeout(() => expand(id), SPRING_LOAD_MS) };
    };

    const onMove = (ev: PointerEvent) => {
      if (!started) {
        if (Math.hypot(ev.clientX - from.x, ev.clientY - from.y) < DRAG_THRESHOLD) return;
        started = true;
        suppressClickRef.current = true;
        setDragCursor(true);
        useApiDragStore.getState().start(dragged, ev.clientX, ev.clientY);
      }
      if (ghostRef.current) {
        ghostRef.current.style.transform = `translate(${ev.clientX + 12}px, ${ev.clientY + 12}px)`;
      }
      const zone = zoneAt(ev.clientX, ev.clientY, dragged);
      useApiDragStore.getState().hover(zone);
      armSpring(zone?.mode === "into" ? (zone.parentId ?? zone.collectionId) : null);
    };

    const onUp = (ev: PointerEvent) => {
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
      window.removeEventListener("pointercancel", onUp);
      armSpring(null);
      if (!started) return;
      const zone = zoneAt(ev.clientX, ev.clientY, dragged);
      setDragCursor(false);
      useApiDragStore.getState().end();
      // Dropped on nothing droppable, or back where it started: the tree stays as it was rather
      // than paying a round trip to renumber a list into the order it already had.
      if (zone && !isCurrentSlot(dragged, zone)) {
        void useApiTreeStore.getState().moveNode(dragged.kind, dragged.id, zone.collectionId, zone.parentId, zone.index);
      }
    };

    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
    window.addEventListener("pointercancel", onUp);
  };

  const takeSuppressedClick = () => {
    if (!suppressClickRef.current) return false;
    suppressClickRef.current = false;
    return true;
  };

  // ---------- render ----------

  const renderRow = (row: CollectionTreeRow) => {
    switch (row.kind) {
      case "collection": {
        const node: NodeRef = {
          kind: "collection",
          id: row.collection.id,
          collectionId: row.collection.id,
          parentId: null,
          name: row.collection.name,
        };
        return (
          <TreeRow
            node={node}
            depth={0}
            expanded={expanded.has(row.collection.id)}
            renaming={renaming?.id === row.collection.id}
            dragging={false}
            dropInto={over?.mode === "into" && over.parentId === null && over.collectionId === row.collection.id}
            onActivate={() => toggle(row.collection.id)}
            onMenu={(x, y) => setMenu({ x, y, node })}
            onQuickAdd={() => startDraft("request", row.collection.id, null)}
            onRename={(name) => void commitRename(node, name)}
            onCancelRename={() => setRenaming(null)}
            suppressClick={takeSuppressedClick}
          />
        );
      }
      case "folder": {
        const node: NodeRef = {
          kind: "folder",
          id: row.folder.id,
          collectionId: row.folder.collection_id,
          parentId: row.folder.parent_id,
          name: row.folder.name,
        };
        return (
          <TreeRow
            node={node}
            depth={row.depth}
            expanded={expanded.has(row.folder.id)}
            renaming={renaming?.id === row.folder.id}
            dragging={drag?.id === row.folder.id}
            dropInto={over?.mode === "into" && over.parentId === row.folder.id}
            onActivate={() => toggle(row.folder.id)}
            onMenu={(x, y) => setMenu({ x, y, node })}
            onQuickAdd={() => startDraft("request", row.folder.collection_id, row.folder.id)}
            onBeginDrag={(e) => beginDrag(e, node)}
            onRename={(name) => void commitRename(node, name)}
            onCancelRename={() => setRenaming(null)}
            suppressClick={takeSuppressedClick}
          />
        );
      }
      case "request": {
        const node: NodeRef = {
          kind: "request",
          id: row.request.id,
          collectionId: row.request.collection_id,
          parentId: row.request.folder_id,
          name: row.request.name,
        };
        return (
          <TreeRow
            node={node}
            depth={row.depth}
            protocol={row.request.protocol}
            method={row.request.method}
            renaming={renaming?.id === row.request.id}
            dragging={drag?.id === row.request.id}
            dropInto={false}
            onActivate={() => useApiTabsStore.getState().openRequest(row.request.id)}
            onMenu={(x, y) => setMenu({ x, y, node })}
            onBeginDrag={(e) => beginDrag(e, node)}
            onRename={(name) => void commitRename(node, name)}
            onCancelRename={() => setRenaming(null)}
            suppressClick={takeSuppressedClick}
          />
        );
      }
      case "draft":
        return (
          <DraftRow
            kind={row.draft.kind}
            depth={row.depth}
            onSubmit={(name) => void submitDraft(name)}
            onCancel={() => setDraft(null)}
          />
        );
      case "empty":
        // Only a collection announces that it's empty; an empty folder just shows nothing, the
        // way every file explorer does.
        return (
          <p
            style={{ paddingLeft: treeIndent(row.depth) }}
            className="py-0.5 text-badge text-[var(--cf-text-muted)]"
          >
            {t("api.noRequests")}
          </p>
        );
    }
  };

  /**
   * Where the insertion line sits — a pixel offset into the virtualized canvas — or null when
   * the pointer isn't aiming at a between-gap. The matching logic is the old in-flow
   * `dropLine`'s, inverted: recover the rendered gap the store index points at (skipping the
   * collapsed duplicate beside the dragged row), then anchor it to the flat row that used to
   * follow it in flow — or to the bottom of the container's last row for a trailing gap.
   */
  const dropLinePosition = (): { top: number; depth: number } | null => {
    if (!drag || !over || over.mode !== "between") return null;
    const siblings =
      drag.kind === "folder"
        ? childFolders(over.collectionId, over.parentId)
        : childRequests(over.collectionId, over.parentId);
    const at = siblings.findIndex((n) => n.id === drag.id);
    let gap = -1;
    for (let g = 0; g <= siblings.length; g++) {
      // The gaps either side of the dragged row are the same slot; drawing the lower one would
      // put two lines on screen for one destination.
      if (at >= 0 && g === at + 1) continue;
      if (storeIndex(g, at) === over.index) {
        gap = g;
        break;
      }
    }
    if (gap < 0) return null;

    const measurements = virtualizer.measurementsCache;
    const startOf = (id: string): number | null => {
      const index = rowIndexById.get(id);
      return index === undefined ? null : (measurements[index]?.start ?? null);
    };

    // The container header row: the collection itself, or the folder the gap lives in.
    const headerId = over.parentId ?? over.collectionId;
    const headerIndex = rowIndexById.get(headerId);
    if (headerIndex === undefined) return null;
    const headerRow = rows[headerIndex];
    if (!headerRow) return null;
    const depth = headerRow.depth + 1;

    if (gap < siblings.length) {
      const top = startOf(siblings[gap]!.id);
      return top === null ? null : { top, depth };
    }
    // Trailing folder gap: folders render above requests, so it sits where the requests begin.
    if (drag.kind === "folder") {
      const firstRequest = childRequests(over.collectionId, over.parentId)[0];
      if (firstRequest) {
        const top = startOf(firstRequest.id);
        return top === null ? null : { top, depth };
      }
    }
    // Nothing follows inside the container: the line sits under its last visible row.
    let last = headerIndex;
    while (last + 1 < rows.length && rows[last + 1]!.depth > headerRow.depth) last += 1;
    const end = measurements[last]?.end;
    return end === undefined ? null : { top: end, depth };
  };

  const line = dropLinePosition();

  return (
    <div className="flex h-full min-h-0 flex-col">
      <VirtualizedTree
        rows={rows}
        virtualizer={virtualizer}
        scrollRef={scrollRef}
        role="tree"
        renderRow={(row) => renderRow(row)}
        overlay={
          line && (
            <div
              style={{ top: line.top - 1, left: treeIndent(line.depth) }}
              className="pointer-events-none absolute right-2 z-10 h-[2px] rounded-full bg-[var(--cf-accent)]"
            />
          )
        }
      >
        {/* Nothing to window at all — a different state from "this container is empty", which is a
            row inside the tree. `undefined` hands rendering back to the rows. */}
        {collections.length === 0 ? <EmptyState icon={Boxes} title={t("api.noCollections")} /> : undefined}
      </VirtualizedTree>

      {menu && (
        <ContextMenu x={menu.x} y={menu.y} items={menuItems(menu.node)} onClose={() => setMenu(null)} />
      )}

      {drag && origin && (
        <DragGhost
          ghostRef={ghostRef}
          x={origin.x}
          y={origin.y}
          label={drag.name || t("api.untitledRequest")}
        />
      )}
    </div>
  );
}
