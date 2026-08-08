import { CloudUpload, Download, RefreshCw, Upload } from "lucide-react";
import { useRepoStore } from "../../state/repoStore";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { useFetchTimerStore } from "../../state/fetchTimerStore";
import { usePreferencesStore } from "../../state/preferencesStore";
import { useT } from "../../state/languageStore";
import { ringGeometry } from "../../lib/ui/progressRing";
import { canPublish, canPull, canPush, fetchNow, pullNow, pushNow } from "../../lib/gitActions";
import { useShortcutHint } from "../../lib/useShortcutHint";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";

/**
 * Fetch, and then either publish or pull/push — moved up from the status bar unchanged in behaviour.
 *
 * Availability still comes from `lib/gitActions`, so these buttons and the keyboard shortcuts that
 * do the same thing cannot disagree about when there is nothing to do, and the tooltips still carry
 * the *reason* a disabled one is disabled ("nothing to pull" is not something a label can say).
 *
 * What changed is the room: a footer spanned the window, a header shares it with context on one side
 * and the window controls on the other. Fetch gives up its label — it is the most frequent of the
 * three and the word adds least — and becomes an `IconButton`, which is what the design rules
 * require of an icon-only control: the label is a required `TranslationKey` feeding both the tooltip
 * and the `aria-label`, so it is still named for a screen reader. Pull, push and publish keep their
 * text, because "push" and "publish" are different promises and an arrow does not distinguish them.
 */
/** Radius and stroke of the countdown ring, sized to sit just outside a 24px icon button without
 * changing its hit target. */
const RING_RADIUS = 11;
const RING_STROKE = 1.5;

/**
 * The fetch button with the time to the next automatic fetch drawn around it.
 *
 * The countdown existed already but only as tooltip text, which meant it was invisible unless you
 * happened to hover the one button it describes — a timer nobody can see is a timer that surprises
 * people when it fires. The ring fills as the interval elapses.
 *
 * `IconButton` has no slot for decoration, so the ring is a sibling behind it, the same way the
 * navigation rail hangs its badge dot off a positioned wrapper. It is `aria-hidden`: the seconds are
 * already in the button's accessible name, and a second announcement of the same number is noise.
 */
function FetchCountdown({
  remaining,
  total,
  pending,
  disabled,
}: {
  remaining: number;
  total: number;
  pending: boolean;
  disabled: boolean;
}) {
  const { dashArray, dashOffset } = ringGeometry(RING_RADIUS, remaining, total);
  const size = (RING_RADIUS + RING_STROKE) * 2;

  return (
    <span className="relative flex items-center justify-center">
      <svg
        aria-hidden
        width={size}
        height={size}
        viewBox={`0 0 ${size} ${size}`}
        className="pointer-events-none absolute"
        // Starts at twelve o'clock and fills clockwise; an SVG circle starts at three otherwise.
        style={{ transform: "rotate(-90deg)" }}
      >
        <circle
          cx={size / 2}
          cy={size / 2}
          r={RING_RADIUS}
          fill="none"
          stroke="var(--cf-accent)"
          strokeWidth={RING_STROKE}
          strokeLinecap="round"
          strokeDasharray={dashArray}
          strokeDashoffset={dashOffset}
          opacity={0.85}
        />
      </svg>
      <IconButton
        label="statusbar.nextFetch"
        labelParams={{ n: remaining }}
        icon={RefreshCw}
        pending={pending}
        disabled={disabled}
        onClick={fetchNow}
      />
    </span>
  );
}

export function HeaderGitActions() {
  const project = useWorkspaceStore((s) => s.activeProject());
  const branches = useRepoStore((s) => s.branches);
  const remoteOp = useRepoStore((s) => s.remoteOp);
  const remainingSeconds = useFetchTimerStore((s) => s.remainingSeconds);
  // The countdown's *total* is the preference itself — `fetchTimerStore` only tracks what is left,
  // and the ring needs both. Read here rather than duplicated into that store.
  const autoFetchSeconds = usePreferencesStore((s) => s.autoFetchSeconds);
  const t = useT();
  const hint = useShortcutHint();

  if (!project) return null;

  const current = branches.find((b) => b.is_head);
  const ahead = current?.ahead ?? 0;
  const behind = current?.behind ?? 0;
  const pullEnabled = canPull(current);
  const pushEnabled = canPush(current);
  const publishable = canPublish(current);

  return (
    <div className="flex items-center gap-0.5">
      {/* When auto-fetch is counting down, the countdown *is* the label — `nextFetch` names the
          action and carries the seconds — so the tooltip stays useful without a second element to
          read. With the timer off it falls back to the plain name plus the live binding. */}
      {remainingSeconds !== null ? (
        <FetchCountdown
          remaining={remainingSeconds}
          total={autoFetchSeconds}
          pending={remoteOp === "fetch"}
          disabled={remoteOp !== null}
        />
      ) : (
        <IconButton
          label="statusbar.fetch"
          icon={RefreshCw}
          shortcut="git.fetch"
          pending={remoteOp === "fetch"}
          disabled={remoteOp !== null}
          onClick={fetchNow}
        />
      )}

      {publishable ? (
        <Button
          variant="primary"
          size="sm"
          icon={CloudUpload}
          pending={remoteOp === "push"}
          disabled={remoteOp !== null}
          tooltip={hint("git.push", t("statusbar.publishTo"))}
          onClick={pushNow}
        >
          {t("statusbar.publish")}
        </Button>
      ) : (
        <>
          <Button
            variant="ghost"
            size="sm"
            icon={Download}
            pending={remoteOp === "pull"}
            disabled={remoteOp !== null || !pullEnabled}
            tooltip={pullEnabled ? hint("git.pull", t("statusbar.pullFrom")) : t("statusbar.nothingToPull")}
            onClick={pullNow}
          >
            {t("statusbar.pull")}
            {behind > 0 && <span className="font-semibold tabular-nums">↓{behind}</span>}
          </Button>
          <Button
            variant="primary"
            size="sm"
            icon={Upload}
            pending={remoteOp === "push"}
            disabled={remoteOp !== null || !pushEnabled}
            tooltip={pushEnabled ? hint("git.push", t("statusbar.pushTo")) : t("statusbar.nothingToPush")}
            onClick={pushNow}
          >
            {t("statusbar.push")}
            {ahead > 0 && <span className="font-semibold tabular-nums">↑{ahead}</span>}
          </Button>
        </>
      )}
    </div>
  );
}
