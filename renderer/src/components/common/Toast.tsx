import { useEffect, useRef } from "react";
import { AlertTriangle, Check, CheckCircle2, Copy, Info, X } from "lucide-react";
import { IconButton } from "./IconButton";
import { useCopy } from "../../lib/ui/useCopy";
import { useToastStore, type Toast as ToastData } from "../../state/toastStore";

const DURATION_MS = 5000;

const ICONS = {
  error: AlertTriangle,
  success: CheckCircle2,
  info: Info,
};

const COLORS = {
  error: "var(--cf-danger)",
  success: "var(--cf-success)",
  info: "var(--cf-accent)",
};

function ToastItem({ toast }: { toast: ToastData }) {
  const dismiss = useToastStore((s) => s.dismissToast);
  const [copied, copy] = useCopy();
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const remainingRef = useRef(DURATION_MS);
  const startRef = useRef(Date.now());

  const clear = () => {
    if (timerRef.current) clearTimeout(timerRef.current);
    timerRef.current = null;
  };

  const start = (ms: number) => {
    clear();
    startRef.current = Date.now();
    remainingRef.current = ms;
    timerRef.current = setTimeout(() => dismiss(toast.id), ms);
  };

  useEffect(() => {
    start(DURATION_MS);
    return clear;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [toast.id]);

  const pause = () => {
    const elapsed = Date.now() - startRef.current;
    remainingRef.current = Math.max(0, remainingRef.current - elapsed);
    clear();
  };

  const resume = () => start(remainingRef.current || 300);

  const Icon = ICONS[toast.type];
  const color = COLORS[toast.type];
  const isError = toast.type === "error";

  return (
    <div
      onMouseEnter={pause}
      onMouseLeave={resume}
      // Focus pauses too, not just the pointer. Reaching the copy button by keyboard took longer
      // than the five seconds the toast lives, so the control existed and could not be used.
      onFocus={pause}
      onBlur={resume}
      // An error interrupts; a success or an info note does not. `alert` is announced the moment it
      // appears, `status` waits for a pause — the same split `UpdateAlert` makes.
      role={isError ? "alert" : "status"}
      className="cf-fade-in flex w-96 max-w-[calc(100vw-1.5rem)] items-start gap-2 rounded-lg border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] p-3 shadow-[var(--cf-shadow)]"
      style={{ borderLeftColor: color, borderLeftWidth: 3 }}
    >
      <Icon size={15} className="mt-0.5 shrink-0" style={{ color }} aria-hidden />
      {/* `select-text` is load-bearing: `index.css` opts the whole `body` out of text selection, and
          this is where nearly every error in the app is shown — including the global
          `unhandledrejection` net. Without it the message could not be selected, and because
          Electron only offers Copy in its context menu when a selection is possible, right-clicking
          offered nothing either. Reporting an error meant retyping it, which is the same bug
          `index.css` records having fixed for markdown and `AiErrorBanner` for AI failures. */}
      <p className="min-w-0 flex-1 max-h-40 select-text overflow-y-auto whitespace-pre-wrap break-words text-body leading-snug text-[var(--cf-text)]">
        {toast.message}
      </p>
      {/* Only on errors. A "Connected to GitHub" is not something anyone copies, and a second
          button on every notice is noise where it is not a way out. */}
      {isError && (
        <IconButton
          label={copied ? "common.copied" : "common.copy"}
          icon={copied ? Check : Copy}
          className="-mt-1 shrink-0"
          onClick={() => copy(toast.message)}
        />
      )}
      <IconButton label="common.close" icon={X} className="-mr-1 -mt-1 shrink-0" onClick={() => dismiss(toast.id)} />
    </div>
  );
}

/**
 * The live region is each toast, not this container, because the container unmounts itself when the
 * list empties — and a live region that is inserted at the same moment as its content is the one
 * case assistive tech is not required to announce. A `role` on the inserted node always is.
 */
export function ToastContainer() {
  const toasts = useToastStore((s) => s.toasts);
  if (toasts.length === 0) return null;

  return (
    <div className="pointer-events-none fixed right-3 top-14 z-50 flex flex-col gap-2">
      {toasts.map((toast) => (
        <div key={toast.id} className="pointer-events-auto">
          <ToastItem toast={toast} />
        </div>
      ))}
    </div>
  );
}
