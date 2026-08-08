import { useCallback, useEffect } from "react";
import { AlertTriangle, RotateCcw, X } from "lucide-react";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import { useT } from "../../state/languageStore";
import { useShortcutsStore, activeChords, bindingFor } from "../../state/shortcutsStore";
import {
  SHORTCUT_COMMANDS,
  SHORTCUT_GROUP_LABELS,
  reservedBy,
  type ShortcutGroup,
  type ShortcutId,
} from "../../lib/shortcuts";
import { chordKeycaps, eventToChord, isBindable } from "../../lib/keys";

const GROUP_ORDER: ShortcutGroup[] = ["general", "panels", "views", "navigation", "workspace", "git"];

/**
 * Captures the next chord the user presses and hands it back.
 *
 * Listens in the *capture* phase and swallows every key while active: the point is to record
 * combinations that are already bound to something (⌘B, Esc, ⌘,), which a bubble-phase listener
 * would only see after the app had already acted on them. The global shortcut handler separately
 * stands down while `recordingId` is set.
 */
function useChordRecorder(active: boolean, onCapture: (chord: string | null) => void, onCancel: () => void) {
  useEffect(() => {
    if (!active) return;
    const handler = (e: KeyboardEvent) => {
      if (e.key === "Shift" || e.key === "Control" || e.key === "Alt" || e.key === "Meta") return;
      e.preventDefault();
      e.stopPropagation();
      if (e.key === "Escape") {
        onCancel();
        return;
      }
      // Backspace clears the binding rather than being recorded as one — an action with no key
      // is a legitimate choice, and there's no other way to express it.
      if (e.key === "Backspace" || e.key === "Delete") {
        onCapture(null);
        return;
      }
      const chord = eventToChord(e);
      if (!chord || !isBindable(chord)) return;
      onCapture(chord);
    };
    window.addEventListener("keydown", handler, true);
    return () => window.removeEventListener("keydown", handler, true);
  }, [active, onCapture, onCancel]);
}

export function ShortcutsSettings() {
  const t = useT();
  const overrides = useShortcutsStore((s) => s.overrides);
  const recordingId = useShortcutsStore((s) => s.recordingId);
  const setRecording = useShortcutsStore((s) => s.setRecording);
  const setBinding = useShortcutsStore((s) => s.setBinding);
  const resetBinding = useShortcutsStore((s) => s.resetBinding);
  const resetAll = useShortcutsStore((s) => s.resetAll);

  const assigned = activeChords(overrides);

  const capture = useCallback(
    (chord: string | null) => {
      const id = useShortcutsStore.getState().recordingId;
      if (!id) return;
      // Assigning a chord that's already taken moves it: the previous owner is left unbound
      // rather than both firing, which would make one of them silently dead.
      if (chord) {
        const previous = activeChords(useShortcutsStore.getState().overrides).get(chord);
        if (previous && previous !== id) void setBinding(previous, null);
      }
      void setBinding(id, chord);
      setRecording(null);
    },
    [setBinding, setRecording],
  );
  const cancel = useCallback(() => setRecording(null), [setRecording]);

  useChordRecorder(recordingId !== null, capture, cancel);

  // Leaving the section mid-capture would otherwise keep the global handler disabled.
  useEffect(() => () => useShortcutsStore.getState().setRecording(null), []);

  const rowFor = (id: ShortcutId) => {
    const command = SHORTCUT_COMMANDS.find((c) => c.id === id)!;
    const chord = bindingFor(id, overrides);
    const recording = recordingId === id;
    const customized = id in overrides;
    const editorConflict = chord ? reservedBy(chord) : null;
    const duplicate = chord ? assigned.get(chord) !== id : false;

    return (
      <div key={id} className="flex items-center gap-3 border-b border-[var(--cf-border)]/60 py-1.5 last:border-0">
        <div className="min-w-0 flex-1">
          <p className="truncate text-body text-[var(--cf-text)]">{t(command.labelKey)}</p>
          {(editorConflict || duplicate) && !recording && (
            <p className="mt-0.5 flex items-center gap-1 text-badge text-[var(--cf-warning)]">
              <AlertTriangle size={11} />
              {duplicate
                ? t("shortcuts.conflict")
                : t("shortcuts.conflictEditor", { name: t(editorConflict!) })}
            </p>
          )}
        </div>

        <button
          onClick={() => setRecording(recording ? null : id)}
          aria-pressed={recording}
          aria-label={t("shortcuts.recordFor", { name: t(command.labelKey) })}
          className={`cf-focusable flex h-7 min-w-[120px] items-center justify-center gap-1 rounded-md border px-2 ${
            recording
              ? "border-[var(--cf-accent)] bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]"
              : "border-[var(--cf-border)] hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
          }`}
        >
          {recording ? (
            <span className="text-badge font-medium">{t("shortcuts.recording")}</span>
          ) : chord ? (
            chordKeycaps(chord).map((key, i) => (
              <kbd
                key={`${key}-${i}`}
                className="rounded border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] px-1.5 py-0.5 font-sans text-badge text-[var(--cf-text)]"
              >
                {key}
              </kbd>
            ))
          ) : (
            <span className="text-badge italic text-[var(--cf-text-muted)]">{t("shortcuts.unbound")}</span>
          )}
        </button>

        {/* `X` is right: this clears the binding out of the field, it does not delete a stored
            thing. `XCircle` is for a text input's own clear affordance. */}
        <IconButton label="shortcuts.clear" icon={X} size="md" disabled={!chord} onClick={() => void setBinding(id, null)} />
        <IconButton
          label="shortcuts.resetOne"
          icon={RotateCcw}
          size="md"
          disabled={!customized}
          onClick={() => void resetBinding(id)}
        />
      </div>
    );
  };

  return (
    <section>
      <h3 className="mb-1 text-title font-semibold">{t("shortcuts.title")}</h3>
      <p className="mb-1 text-relaxed text-[var(--cf-text-muted)]">{t("settings.keybindingsHint")}</p>
      <p className="mb-4 text-badge text-[var(--cf-text-muted)]">{t("shortcuts.recordHint")}</p>

      {GROUP_ORDER.map((group) => {
        const commands = SHORTCUT_COMMANDS.filter((c) => c.group === group);
        if (commands.length === 0) return null;
        return (
          <div key={group} className="mb-5">
            <p className="mb-1 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
              {t(SHORTCUT_GROUP_LABELS[group])}
            </p>
            {commands.map((command) => rowFor(command.id))}
          </div>
        );
      })}

      <div className="border-t border-[var(--cf-border)] pt-4">
        <Button
          variant="secondary"
          icon={RotateCcw}
          disabled={Object.keys(overrides).length === 0}
          onClick={() => void resetAll()}
        >
          {t("shortcuts.resetAll")}
        </Button>
      </div>
    </section>
  );
}
