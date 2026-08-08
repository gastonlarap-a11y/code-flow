import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { save } from "../../lib/bridge/dialog";
import { Camera, Check, Copy, Download, Hash, Minus, Plus, SquareDashed, X } from "lucide-react";
import { Button } from "../common/Button";
import { IconButton } from "../common/IconButton";
import { Tooltip } from "../common/Tooltip";
import { useDialog } from "../../lib/useDialog";
import {
  SNAP_BACKGROUNDS,
  canvasToPngBlob,
  renderCodeSnap,
  suggestedSnapName,
  type SnapBackgroundId,
} from "../../lib/codeSnap";
import { writeFileBytes } from "../../lib/ipc/commands";
import { pushErrorToast, useToastStore } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";
import type { CodeTheme } from "../../lib/codeThemes";

const MIN_PADDING = 0;
const MAX_PADDING = 96;
const FONT_SIZES = [11, 12, 13, 14, 16, 18];

/** Backdrop labels, keyed explicitly rather than built from the preset id — a computed key can't
 * be checked against the translation table, and a missing one would surface as a raw string in
 * the UI instead of a compile error. */
const BACKGROUND_LABELS: Record<SnapBackgroundId, TranslationKey> = {
  sunset: "codesnap.bgSunset",
  ocean: "codesnap.bgOcean",
  violet: "codesnap.bgViolet",
  candy: "codesnap.bgCandy",
  slate: "codesnap.bgSlate",
  theme: "codesnap.bgTheme",
  none: "codesnap.bgNone",
};

export interface CodeSnapTarget {
  code: string;
  language: string;
  /** Repo-relative path, shown in the card's title bar and used to name the exported file. */
  path: string;
  startLine: number;
  endLine: number;
}

/**
 * The code-screenshot composer — CodeSnap's job: pick a backdrop, then copy or save the image.
 *
 * The preview *is* the export. `renderCodeSnap` paints one canvas that the dialog displays
 * scaled-to-fit with CSS and that the copy/save buttons read pixels straight out of, so there is
 * no second rendering path that could disagree with what was on screen.
 */
export function CodeSnapModal({
  target,
  theme,
  tabSize,
  onClose,
}: {
  target: CodeSnapTarget;
  /** The scheme the editor is currently showing, so the snapshot matches the code it came from. */
  theme: CodeTheme;
  tabSize: number;
  onClose: () => void;
}) {
  const t = useT();
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [background, setBackground] = useState<SnapBackgroundId>("sunset");
  const [padding, setPadding] = useState(32);
  const [fontSize, setFontSize] = useState(13);
  const [showLineNumbers, setShowLineNumbers] = useState(true);
  const [showWindowControls, setShowWindowControls] = useState(true);
  const [showTitle, setShowTitle] = useState(true);
  const [scale, setScale] = useState(2);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const [copied, setCopied] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const options = useMemo(
    () => ({
      code: target.code,
      language: target.language,
      theme,
      startLine: target.startLine,
      title: showTitle ? target.path : "",
      showLineNumbers,
      showWindowControls,
      background,
      padding,
      fontSize,
      scale,
      tabSize,
    }),
    [target, theme, showTitle, showLineNumbers, showWindowControls, background, padding, fontSize, scale, tabSize],
  );

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    setSize(renderCodeSnap(canvas, options));
  }, [options]);

  const copy = useCallback(async () => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    setBusy(true);
    try {
      const blob = await canvasToPngBlob(canvas);
      await navigator.clipboard.write([new ClipboardItem({ "image/png": blob })]);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch (e) {
      // Image clipboard write is the one operation here that a webview can refuse outright, so
      // the failure names the alternative rather than just reporting itself.
      pushErrorToast(`${t("codesnap.copyFailed")} — ${String(e)}`);
    } finally {
      setBusy(false);
    }
  }, [t]);

  const download = useCallback(async () => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const path = await save({
      defaultPath: suggestedSnapName(target.path, target.startLine, target.endLine),
      filters: [{ name: "PNG", extensions: ["png"] }],
    }).catch(() => null);
    if (!path) return;
    setBusy(true);
    try {
      const blob = await canvasToPngBlob(canvas);
      await writeFileBytes(path, new Uint8Array(await blob.arrayBuffer()));
      useToastStore.getState().pushToast(t("codesnap.saved"), "success");
    } catch (e) {
      pushErrorToast(String(e));
    } finally {
      setBusy(false);
    }
  }, [target, t]);

  const lineCount = target.endLine - target.startLine + 1;

  const { titleId, dialogProps } = useDialog();


  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-6" onClick={onClose}>
      <div
        {...dialogProps}
        onClick={(e) => e.stopPropagation()}
        className="flex max-h-[85vh] w-full max-w-3xl flex-col overflow-hidden rounded-xl border border-[var(--cf-border)] bg-[var(--cf-surface)] shadow-[var(--cf-shadow)]"
      >
        <div className="flex items-center gap-2 border-b border-[var(--cf-border)] px-4 py-2.5">
          <Camera size={14} className="text-[var(--cf-accent)]" />
          <h2 id={titleId} className="text-body font-semibold">{t("codesnap.title")}</h2>
          <span className="text-badge text-[var(--cf-text-muted)]">
            {t("codesnap.subtitle", { lines: lineCount, w: size.width, h: size.height })}
          </span>
          <IconButton label="common.close" icon={X} className="ml-auto" onClick={onClose} />
        </div>

        {/* Checkerboard so a transparent backdrop reads as transparent rather than as whatever
            colour the dialog happens to be. */}
        <div
          className="min-h-0 flex-1 overflow-auto p-4"
          style={{
            backgroundImage:
              "linear-gradient(45deg, color-mix(in oklab, var(--cf-border) 40%, transparent) 25%, transparent 25%, transparent 75%, color-mix(in oklab, var(--cf-border) 40%, transparent) 75%), linear-gradient(45deg, color-mix(in oklab, var(--cf-border) 40%, transparent) 25%, transparent 25%, transparent 75%, color-mix(in oklab, var(--cf-border) 40%, transparent) 75%)",
            backgroundSize: "16px 16px",
            backgroundPosition: "0 0, 8px 8px",
          }}
        >
          <canvas
            ref={canvasRef}
            // The backing store is `scale`× these dimensions; CSS pins it back to its true size
            // so the preview is a faithful 1:1 view of the exported image.
            style={{ width: size.width, height: size.height, maxWidth: "100%" }}
            className="mx-auto block"
          />
        </div>

        <div className="shrink-0 space-y-2 border-t border-[var(--cf-border)] p-3">
          <div className="flex flex-wrap items-center gap-1.5">
            {SNAP_BACKGROUNDS.map((preset) => (
              <Tooltip key={preset.id} label={t(BACKGROUND_LABELS[preset.id])}>
              <button
                onClick={() => setBackground(preset.id)}
                aria-label={t(BACKGROUND_LABELS[preset.id])}
                aria-pressed={background === preset.id}
                className={`h-6 w-6 rounded-md border ${
                  background === preset.id
                    ? "border-[var(--cf-accent)] ring-1 ring-[var(--cf-accent)]"
                    : "border-[var(--cf-border)]"
                }`}
                style={
                  preset.colors.length === 2
                    ? { backgroundImage: `linear-gradient(135deg, ${preset.colors[0]}, ${preset.colors[1]})` }
                    : preset.id === "theme"
                      ? { background: "var(--cf-surface-raised)" }
                      : undefined
                }
              >
                {preset.id === "none" && <SquareDashed size={12} className="mx-auto text-[var(--cf-text-muted)]" />}
              </button>
              </Tooltip>
            ))}

            <div className="mx-1 h-5 w-px bg-[var(--cf-border)]" />

            <Stepper
              label={t("codesnap.padding")}
              value={padding}
              onChange={(v) => setPadding(Math.min(MAX_PADDING, Math.max(MIN_PADDING, v)))}
              step={8}
            />
            <Stepper
              label={t("codesnap.fontSize")}
              value={fontSize}
              onChange={(v) => {
                // Snapped to the preset ladder rather than free-running, so the card keeps the
                // proportions the rest of the layout constants assume.
                const index = FONT_SIZES.indexOf(fontSize);
                const next = v > fontSize ? index + 1 : index - 1;
                setFontSize(FONT_SIZES[Math.min(FONT_SIZES.length - 1, Math.max(0, next))]!);
              }}
              step={1}
            />

            <div className="mx-1 h-5 w-px bg-[var(--cf-border)]" />

            <Toggle active={showLineNumbers} onClick={() => setShowLineNumbers((v) => !v)} title={t("codesnap.lineNumbers")}>
              <Hash size={12} />
            </Toggle>
            <Toggle
              active={showWindowControls}
              onClick={() => setShowWindowControls((v) => !v)}
              title={t("codesnap.windowControls")}
            >
              <span className="flex gap-[2px]">
                <span className="h-1.5 w-1.5 rounded-full bg-current" />
                <span className="h-1.5 w-1.5 rounded-full bg-current" />
                <span className="h-1.5 w-1.5 rounded-full bg-current" />
              </span>
            </Toggle>
            <Toggle active={showTitle} onClick={() => setShowTitle((v) => !v)} title={t("codesnap.showPath")}>
              <span className="text-badge font-semibold">A</span>
            </Toggle>

            <div className="mx-1 h-5 w-px bg-[var(--cf-border)]" />

            {[1, 2, 3].map((factor) => (
              <button
                key={factor}
                onClick={() => setScale(factor)}
                aria-pressed={scale === factor}
                aria-label={`${t("codesnap.scale")} ${factor}x`}
                className={`cf-focusable h-6 rounded px-1.5 text-badge font-medium ${
                  scale === factor
                    ? "bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]"
                    : "text-[var(--cf-text-muted)] hover:bg-black/[0.05] dark:hover:bg-white/[0.08]"
                }`}
              >
                {factor}x
              </button>
            ))}

            <div className="ml-auto flex items-center gap-1.5">
              <Button
                variant="secondary"
                size="sm"
                icon={copied ? Check : Copy}
                disabled={busy}
                onClick={() => void copy()}
              >
                {t("codesnap.copy")}
              </Button>
              <Button variant="primary" size="sm" icon={Download} pending={busy} onClick={() => void download()}>
                {t("codesnap.save")}
              </Button>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

/**
 * A 24px toggle whose face is arbitrary content — three dots for the window controls, a letter for
 * the path. Not `IconButton`: that one renders exactly one lucide glyph, and two of these three do
 * not have one.
 */
function Toggle({
  active,
  onClick,
  title,
  children,
}: {
  active: boolean;
  onClick: () => void;
  title: string;
  children: React.ReactNode;
}) {
  return (
    <Tooltip label={title}>
    <button
      onClick={onClick}
      aria-label={title}
      aria-pressed={active}
      className={`cf-focusable flex h-6 w-6 items-center justify-center rounded-md ${
        active
          ? "bg-[var(--cf-accent-soft)] text-[var(--cf-accent)]"
          : "text-[var(--cf-text-muted)] hover:bg-black/[0.05] dark:hover:bg-white/[0.08]"
      }`}
    >
      {children}
    </button>
    </Tooltip>
  );
}

function Stepper({
  label,
  value,
  onChange,
  step,
}: {
  label: string;
  value: number;
  onChange: (value: number) => void;
  step: number;
}) {
  return (
    <Tooltip label={label}>
      <div className="flex items-center gap-0.5 rounded-md border border-[var(--cf-border)] px-1">
        <button
          onClick={() => onChange(value - step)}
          aria-label={`${label} −`}
          className="cf-focusable flex h-6 w-6 items-center justify-center text-[var(--cf-text-muted)] hover:text-[var(--cf-text)]"
        >
          <Minus size={12} />
        </button>
        <span className="w-6 text-center font-mono text-badge text-[var(--cf-text-muted)]">{value}</span>
        <button
          onClick={() => onChange(value + step)}
          aria-label={`${label} +`}
          className="cf-focusable flex h-6 w-6 items-center justify-center text-[var(--cf-text-muted)] hover:text-[var(--cf-text)]"
        >
          <Plus size={12} />
        </button>
      </div>
    </Tooltip>
  );
}
