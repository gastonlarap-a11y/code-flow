import { Component, type ErrorInfo, type ReactNode } from "react";
import { AlertTriangle, RefreshCw, RotateCcw } from "lucide-react";
import { translate } from "../../state/languageStore";
import { isChunkLoadError } from "../../lib/lazyRetry";
import { Button } from "./Button";

interface Props {
  children: ReactNode;
  /** Names the part of the app that failed, so the message can say *what* broke rather than just
   * that something did. */
  area: string;
  /** Changing this remounts the subtree and clears the error — pass whatever selects the content,
   * so navigating away from a broken screen recovers by itself. */
  resetKey?: string;
}

interface State {
  error: Error | null;
}

/**
 * A crash in one part of the app stops being a crash of the whole app.
 *
 * `main.tsx` already nets unhandled promise rejections. Render was the other half and had nothing:
 * any component that threw took the entire tree down with it, and because the root unmounts on the
 * way out, what the user got was a black window — no message, no recovery, nothing to report. That
 * is a bug report we actually received ("the screen goes black when I open settings"), and it is
 * unanswerable without this: the error was never written down anywhere.
 *
 * A class is not a style choice. React 19 still exposes error catching only through
 * `getDerivedStateFromError`/`componentDidCatch`, with no hook or function equivalent — the
 * alternative is the `react-error-boundary` package, and a dependency plus two regenerated lockfiles
 * is a lot to pay for forty lines the framework already dictates the shape of.
 *
 * It also catches what `React.lazy` throws when a chunk fails to load, which is the most likely way
 * a screen that worked yesterday shows nothing today.
 */
export class ErrorBoundary extends Component<Props, State> {
  override state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  override componentDidCatch(error: Error, info: ErrorInfo) {
    // Console rather than a toast: the toast store may be part of what just failed, and the shell's
    // stdout is where the sidecar's own errors already land.
    console.error(`[codeflow] ${this.props.area} crashed`, error, info.componentStack);
  }

  override componentDidUpdate(prev: Props) {
    // Recover on navigation: a broken view should not keep its fallback once you have left it.
    if (this.state.error && prev.resetKey !== this.props.resetKey) this.setState({ error: null });
  }

  override render() {
    const { error } = this.state;
    if (!error) return this.props.children;

    // A chunk that never arrived cannot be retried in place: `lazy` caches the rejection for the
    // life of the module, so re-rendering the same component re-throws the same error without ever
    // asking the network again. Reloading is the only thing that can fix it, so it is the only
    // thing offered — a Retry button here would be a button that provably does nothing.
    const staleChunk = isChunkLoadError(error);

    return (
      <div className="flex h-full min-h-0 w-full flex-col items-center justify-center gap-3 p-6 text-center">
        <AlertTriangle size={24} className="text-[var(--cf-danger)]" aria-hidden />
        <p className="text-relaxed font-semibold text-[var(--cf-text)]">
          {translate(staleChunk ? "error.staleChunkTitle" : "error.boundaryTitle", {
            area: this.props.area,
          })}
        </p>
        {/* The message verbatim. A crash the user cannot quote is a crash nobody can fix. */}
        <p className="max-w-[560px] break-words font-mono text-body text-[var(--cf-text-muted)]">
          {staleChunk ? translate("error.staleChunkHint") : error.message}
        </p>
        {staleChunk ? (
          <Button variant="secondary" icon={RefreshCw} onClick={() => window.location.reload()}>
            {translate("error.boundaryReload")}
          </Button>
        ) : (
          <Button variant="secondary" icon={RotateCcw} onClick={() => this.setState({ error: null })}>
            {translate("error.boundaryRetry")}
          </Button>
        )}
      </div>
    );
  }
}
