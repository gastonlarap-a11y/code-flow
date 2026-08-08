import { useEffect, useRef, useState } from "react";
import { Check, ChevronDown, Layers, Plus } from "lucide-react";
import { IconButton } from "../common/IconButton";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { useT } from "../../state/languageStore";
import { pushErrorToast } from "../../state/toastStore";

/**
 * The workspace switcher, in the header where it is always reachable.
 *
 * It used to live at the top of the sidebar, which was fine while the sidebar was always on screen.
 * Phase 3 moved that sidebar's content into the context panel, so this — the only control in the
 * app that *creates* a workspace — became unreachable from Home, from the API client, and from
 * anywhere with the context panel closed. The header is where the workspace already had a pill, so
 * the pill becomes the control instead of merely naming the thing.
 *
 * The pill matches its two neighbours: coloured icon, name, chevron. It was the only one of the
 * three without a chevron and the only one people could not find, which is not a coincidence — the
 * chevron is what says "this opens something".
 *
 * Renaming a workspace is still not possible anywhere: there is no `update_workspace_name` command
 * in the sidecar, so it is not an omission here.
 */
export function WorkspaceMenu() {
  const workspaces = useWorkspaceStore((s) => s.workspaces);
  const activeWorkspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);
  const setActiveWorkspace = useWorkspaceStore((s) => s.setActiveWorkspace);
  const addWorkspace = useWorkspaceStore((s) => s.addWorkspace);
  const [open, setOpen] = useState(false);
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState("");
  const rootRef = useRef<HTMLDivElement>(null);
  const t = useT();

  const active = workspaces.find((w) => w.id === activeWorkspaceId);

  /**
   * The two submits — Enter and the confirm button — share one body.
   *
   * They were duplicated, and neither caught anything: a rejected `create_workspace` left the input
   * open with the name still in it and said nothing, which reads as "the app refuses to create a
   * workspace" rather than "the backend call failed". The form is only cleared once the workspace
   * actually exists, so a failure leaves the typed name to retry with.
   */
  const submitNewWorkspace = async () => {
    const name = newName.trim();
    if (!name) return;
    try {
      await addWorkspace(name, "briefcase", "#6366f1");
      setNewName("");
      setCreating(false);
      setOpen(false);
    } catch (e) {
      pushErrorToast(t("toast.createWorkspaceFailed", { error: String(e) }));
    }
  };

  // The menu had no way out but picking something: no Escape, no click-outside. Both close it and
  // abandon a half-typed workspace name, which is what "I opened this by mistake" means here.
  useEffect(() => {
    if (!open) return;
    const dismiss = () => {
      setOpen(false);
      setCreating(false);
    };
    const onPointerDown = (event: MouseEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) dismiss();
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") dismiss();
    };
    window.addEventListener("mousedown", onPointerDown);
    window.addEventListener("keydown", onKeyDown);
    return () => {
      window.removeEventListener("mousedown", onPointerDown);
      window.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);

  if (!active) return null;

  return (
    <div ref={rootRef} className="relative">
      <button
        onClick={() => setOpen((v) => !v)}
        aria-haspopup="true"
        aria-expanded={open}
        className="cf-focusable flex h-7 shrink-0 items-center gap-1.5 rounded-control px-2 text-ui font-medium text-[var(--cf-text)] transition-colors hover:bg-black/[0.04] dark:hover:bg-white/[0.06]"
      >
        <Layers size={14} style={{ color: active.color }} aria-hidden />
        <span className="max-w-[110px] truncate">{active.name}</span>
        <ChevronDown size={12} className="shrink-0 text-[var(--cf-text-muted)]" aria-hidden />
      </button>

      {open && (
        <div className="absolute left-0 top-full z-30 mt-1 w-60 rounded-lg border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-1 shadow-[var(--cf-shadow)]">
          {workspaces.map((ws) => (
            <button
              key={ws.id}
              onClick={() => {
                setActiveWorkspace(ws.id);
                setOpen(false);
              }}
              aria-current={ws.id === activeWorkspaceId ? "true" : undefined}
              className={`cf-focusable flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-body ${
                ws.id === activeWorkspaceId
                  ? "bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]"
                  : "hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
              }`}
            >
              <span className="h-2 w-2 shrink-0 rounded-full" style={{ background: ws.color }} />
              <span className="truncate">{ws.name}</span>
            </button>
          ))}

          <div className="my-1 h-px bg-[var(--cf-border)]" role="separator" />

          {creating ? (
            <div className="flex items-center gap-1 px-1 py-1">
              <input
                autoFocus
                value={newName}
                onChange={(e) => setNewName(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter") {
                    void submitNewWorkspace();
                  } else if (e.key === "Escape") {
                    setCreating(false);
                  }
                }}
                placeholder={t("sidebar.workspaceName")}
                aria-label={t("sidebar.workspaceName")}
                className="min-w-0 flex-1 rounded-md border border-[var(--cf-border)] bg-transparent px-1.5 py-0.5 text-ui outline-none focus:border-[var(--cf-accent)]"
              />
              <IconButton
                label="sidebar.newWorkspace"
                icon={Check}
                disabled={!newName.trim()}
                onClick={() => void submitNewWorkspace()}
              />
            </div>
          ) : (
            <button
              onClick={() => setCreating(true)}
              className="cf-focusable flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-body text-[var(--cf-text-muted)] hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
            >
              <Plus size={14} aria-hidden />
              {t("sidebar.newWorkspace")}
            </button>
          )}
        </div>
      )}
    </div>
  );
}
