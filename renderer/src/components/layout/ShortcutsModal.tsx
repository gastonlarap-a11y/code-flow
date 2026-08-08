import { useEffect } from "react";
import { Keyboard, Settings } from "lucide-react";
import { useT } from "../../state/languageStore";
import { useUiStore } from "../../state/uiStore";
import { useShortcutsStore, bindingFor } from "../../state/shortcutsStore";
import { SHORTCUT_COMMANDS, SHORTCUT_GROUP_LABELS, EDITOR_RESERVED, type ShortcutGroup } from "../../lib/shortcuts";
import { chordKeycaps } from "../../lib/keys";
import { Modal } from "../common/Modal";
import { Button } from "../common/Button";

const GROUP_ORDER: ShortcutGroup[] = ["general", "panels", "views", "navigation", "workspace", "git"];

export function Keycap({ children }: { children: string }) {
  return (
    <kbd className="rounded border border-[var(--cf-border)] bg-[var(--cf-surface-raised)] px-1.5 py-0.5 font-sans text-badge text-[var(--cf-text)]">
      {children}
    </kbd>
  );
}

function Row({ label, chord }: { label: string; chord: string | null }) {
  const t = useT();
  return (
    <div className="flex items-center gap-3">
      <span className="min-w-0 flex-1 truncate text-body text-[var(--cf-text)]">{label}</span>
      <span className="flex shrink-0 items-center gap-1">
        {chord ? (
          chordKeycaps(chord).map((key, i) => <Keycap key={`${key}-${i}`}>{key}</Keycap>)
        ) : (
          <span className="text-badge italic text-[var(--cf-text-muted)]">{t("shortcuts.unbound")}</span>
        )}
      </span>
    </div>
  );
}

/**
 * The cheat sheet. App actions are read live from the user's bindings, so it always reflects what
 * the keyboard actually does; the editor group below is fixed because half of it comes from
 * Monaco itself rather than from this app — which is exactly why it's worth writing down.
 */
export function ShortcutsModal({ onClose }: { onClose: () => void }) {
  const t = useT();
  const overrides = useShortcutsStore((s) => s.overrides);
  const openSettings = useUiStore((s) => s.openSettings);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <Modal
      title="shortcuts.title"
      icon={Keyboard}
      size="lg"
      scroll
      onClose={onClose}
      toolbar={
        <Button
          variant="ghost"
          size="sm"
          icon={Settings}
          onClick={() => {
            openSettings("keybindings");
            onClose();
          }}
        >
          {t("shortcuts.customize")}
        </Button>
      }
    >
      <div className="space-y-4">
          {GROUP_ORDER.map((group) => {
            const commands = SHORTCUT_COMMANDS.filter((c) => c.group === group);
            if (commands.length === 0) return null;
            return (
              <div key={group}>
                <p className="mb-1.5 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
                  {t(SHORTCUT_GROUP_LABELS[group])}
                </p>
                <div className="space-y-1">
                  {commands.map((command) => (
                    <Row
                      key={command.id}
                      label={t(command.labelKey)}
                      chord={bindingFor(command.id, overrides)}
                    />
                  ))}
                </div>
              </div>
            );
          })}

          <div>
            <p className="mb-1.5 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
              {t("shortcuts.groupEditor")}
            </p>
            <p className="mb-1.5 text-body text-[var(--cf-text-muted)]">{t("shortcuts.editorFixedHint")}</p>
            <div className="space-y-1">
              {EDITOR_RESERVED.map((entry) => (
                <Row key={entry.chord} label={t(entry.labelKey)} chord={entry.chord} />
              ))}
            </div>
          </div>
      </div>
    </Modal>
  );
}
