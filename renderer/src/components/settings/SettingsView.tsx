import { useEffect } from "react";
import { useDialog } from "../../lib/useDialog";
import {
  Blocks,
  Bot,
  FolderGit2,
  GitBranch,
  Globe,
  Keyboard,
  PackagePlus,
  Palette,
  Plug,
  ShieldCheck,
  Workflow,
  X,
  Zap,
} from "lucide-react";
import { ThemeSettings } from "./ThemeSettings";
import { ProjectsSettings } from "./ProjectsSettings";
import { IntegrationsSettings } from "./IntegrationsSettings";
import { ClaudeSettings } from "./ClaudeSettings";
import { ReviewSettings } from "./ReviewSettings";
import { SddSettings } from "./SddSettings";
import { SkillsSettings } from "./SkillsSettings";
import { McpSettings } from "./McpSettings";
import { GitSettings } from "./GitSettings";
import { GeneralSettings } from "./GeneralSettings";
import { ShortcutsSettings } from "./ShortcutsSettings";
import { ApiSettingsBody } from "../api/ApiSettingsModal";
import { ActivePill } from "../common/ActivePill";
import { IconButton } from "../common/IconButton";
import { Tooltip } from "../common/Tooltip";
import { ResizeHandle } from "../common/ResizeHandle";
import { useLayoutStore } from "../../state/layoutStore";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { useUiStore, type SettingsSectionId } from "../../state/uiStore";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";

const NAV_MIN = 160;
const NAV_MAX = 320;

// Global settings apply across every workspace/project. Workspace settings — everything
// Claude reads when reviewing a PR (context, instructions, skills, MCP servers) — apply
// only to whichever workspace is currently active, per the user's explicit scoping model.
const GLOBAL_SECTIONS: { id: SettingsSectionId; labelKey: TranslationKey; icon: typeof Palette }[] = [
  { id: "general", labelKey: "settings.general", icon: Globe },
  { id: "appearance", labelKey: "settings.appearance", icon: Palette },
  { id: "keybindings", labelKey: "shortcuts.title", icon: Keyboard },
  { id: "projects", labelKey: "settings.projects", icon: FolderGit2 },
  { id: "git", labelKey: "settings.git", icon: GitBranch },
  // Not `Plug` — that one is MCPs, three rows down, and two plugs in one nav list name nothing.
  { id: "integrations", labelKey: "settings.integrationsSection", icon: Blocks },
  { id: "claude", labelKey: "settings.aiSection", icon: Bot },
  { id: "api", labelKey: "api.settings.title", icon: Zap },
];

const WORKSPACE_SECTIONS: { id: SettingsSectionId; labelKey: TranslationKey; icon: typeof Palette }[] = [
  { id: "review", labelKey: "settings.review", icon: ShieldCheck },
  { id: "sdd", labelKey: "settings.sdd", icon: Workflow },
  { id: "skills", labelKey: "settings.skills", icon: PackagePlus },
  { id: "mcps", labelKey: "settings.mcps", icon: Plug },
];

/**
 * One row of the settings nav, wearing the same selected treatment as the Graph/Changes/Editor
 * tabs: the accent fill is the shared [`ActivePill`], so picking a section slides it there rather
 * than repainting two backgrounds.
 *
 * Both groups share the one `layoutId`, deliberately — the pill travels between "Global" and the
 * workspace group as one continuous movement, which is exactly what the eye expects from a single
 * list of sections. It works because every row stays mounted for as long as the nav is open.
 */
function SectionButton({
  id,
  labelKey,
  icon: Icon,
  active,
  onSelect,
}: {
  id: SettingsSectionId;
  labelKey: TranslationKey;
  icon: typeof Palette;
  active: boolean;
  onSelect: (id: SettingsSectionId) => void;
}) {
  const t = useT();
  const label = t(labelKey);
  return (
    <Tooltip label={label} placement="right">
    <button
      onClick={() => onSelect(id)}
      aria-current={active ? "page" : undefined}
      // Selection changes colour and nothing else — no weight change, exactly like the tabs.
      // Bolding on select re-measures the text (here: 140px → 143px against 141px of room), which
      // wrapped "Workspaces & projects" onto a second line and made the row jump every time it was
      // picked. Colour plus the pill is already the whole signal.
      className={`cf-focusable relative mb-0.5 flex w-full items-center rounded-md px-2.5 py-1.5 text-left text-body transition-colors ${
        active
          ? "text-[var(--cf-accent)]"
          : "text-[var(--cf-text-muted)] hover:bg-black/[0.03] hover:text-[var(--cf-text)] dark:hover:bg-white/[0.04]"
      }`}
    >
      {active && <ActivePill layoutId="cf-settings-pill" />}
      {/* Above the pill, which is absolutely positioned over the whole button. Truncating rather
          than wrapping keeps every row exactly one line tall at any nav width — the nav is
          resizable down to 160px, and a longer translation shouldn't be able to reflow it
          either. The full label is on the tooltip. */}
      <span className="relative flex min-w-0 items-center gap-1.5">
        <Icon size={14} className="shrink-0" />
        <span className="truncate">{label}</span>
      </span>
    </button>
    </Tooltip>
  );
}

export function SettingsView() {
  const open = useUiStore((s) => s.settingsOpen);
  const closeSettings = useUiStore((s) => s.closeSettings);
  const section = useUiStore((s) => s.settingsSection);
  const setSection = useUiStore((s) => s.openSettings);
  const navWidth = useLayoutStore((s) => s.sizes.settingsNavWidth);
  const setSize = useLayoutStore((s) => s.setSize);
  const commitSize = useLayoutStore((s) => s.commitSize);
  const activeWorkspaceId = useWorkspaceStore((s) => s.activeWorkspaceId);
  const activeWorkspaceName = useWorkspaceStore(
    (s) => s.workspaces.find((w) => w.id === activeWorkspaceId)?.name,
  );
  const t = useT();

  // Above the early return below, and `open` is the argument that makes it work. This component
  // renders `null` while settings are closed, so a hook called after that return runs on some
  // renders and not others — React counts a different number each time and tears the tree down,
  // which is a black window rather than an error anyone can read.
  //
  // `open` also has to be passed rather than left at its default: the focus trap keys its effect
  // on it, and a constant `true` would run once at mount, when the panel ref is still null, and
  // never again. The trap would then do nothing, silently.
  const { titleId, dialogProps } = useDialog({ active: open });

  // Closable via Escape, but deliberately NOT by clicking the backdrop — settings can hold
  // unsaved in-progress input, and an accidental outside click shouldn't discard it.
  useEffect(() => {
    if (!open) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") closeSettings();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [open, closeSettings]);

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/20">
      <div
        {...dialogProps}
        onClick={(e) => e.stopPropagation()}
        className="flex h-[640px] max-h-[85vh] w-[880px] max-w-[92vw] flex-col overflow-hidden rounded-2xl border border-[var(--cf-border)] bg-[var(--cf-surface)] shadow-[var(--cf-shadow)]"
      >
        <div className="flex shrink-0 items-center justify-between border-b border-[var(--cf-border)] px-4 py-2.5">
          {/* A real heading, not a styled paragraph: it is what `aria-labelledby` points at, and
              the section headings below it are `<h3>` — which needs an `<h2>` above them. */}
          <h2 id={titleId} className="text-relaxed font-semibold">
            {t("statusbar.settings")}
          </h2>
          <IconButton label="common.close" icon={X} onClick={closeSettings} />
        </div>

        <div className="flex min-h-0 flex-1">
          <nav style={{ width: navWidth }} className="shrink-0 overflow-y-auto border-r border-[var(--cf-border)] p-3">
            <p className="mb-1 px-2.5 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
              {t("settings.globalGroup")}
            </p>
            {GLOBAL_SECTIONS.map((item) => (
              <SectionButton key={item.id} {...item} active={section === item.id} onSelect={setSection} />
            ))}

            <p className="mb-1 mt-4 px-2.5 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
              {activeWorkspaceName
                ? t("settings.workspaceGroup", { name: activeWorkspaceName })
                : t("settings.workspaceGroupGeneric")}
            </p>
            {WORKSPACE_SECTIONS.map((item) => (
              <SectionButton key={item.id} {...item} active={section === item.id} onSelect={setSection} />
            ))}
          </nav>
          <ResizeHandle
            axis="x"
            value={navWidth}
            min={NAV_MIN}
            max={NAV_MAX}
            onChange={(w) => setSize("settingsNavWidth", w)}
            onCommit={(w) => commitSize("settingsNavWidth", w)}
          />
          <div className="flex-1 overflow-auto p-6">
            <div className="mx-auto max-w-xl">
              {section === "appearance" && <ThemeSettings />}
              {section === "general" && <GeneralSettings />}
              {section === "keybindings" && <ShortcutsSettings />}
              {section === "projects" && <ProjectsSettings />}
              {section === "git" && <GitSettings />}
              {section === "integrations" && <IntegrationsSettings />}
              {section === "claude" && <ClaudeSettings />}
              {section === "api" && <ApiSettingsBody />}
              {section === "review" && <ReviewSettings />}
              {section === "sdd" && <SddSettings />}
              {section === "skills" && <SkillsSettings />}
              {section === "mcps" && <McpSettings />}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
