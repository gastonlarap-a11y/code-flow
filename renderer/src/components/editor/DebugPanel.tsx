import { useEffect, useRef, useState } from "react";
import {
  ChevronDown,
  Pause,
  ChevronRight,
  CornerDownRight,
  CornerRightUp,
  Play,
  Redo2,
  Eraser,
} from "lucide-react";
import { IconButton } from "../common/IconButton";
import { StopSquare } from "../../lib/ui/icons";
import { useDebugStore } from "../../state/debugStore";
import { DEBUG_ADAPTERS, adapterById, adapterForFile } from "../../lib/debugAdapters";
import type { DebugVariable } from "../../lib/ipc/commands";
import { useT } from "../../state/languageStore";

function fileName(path: string): string {
  return path.split(/[\\/]/).pop() ?? path;
}

/** One variable row. Objects expand one level at a time, on click — a deep graph fetched eagerly
 * is slow to produce and almost entirely unread. */
function VariableRow({ variable, depth }: { variable: DebugVariable; depth: number }) {
  const expanded = useDebugStore((s) => (variable.object_id ? s.expanded[variable.object_id] : undefined));
  const expand = useDebugStore((s) => s.expand);
  const expandable = Boolean(variable.object_id);

  return (
    <>
      <button
        onClick={() => variable.object_id && void expand(variable.object_id)}
        {...(expandable ? { "aria-expanded": Boolean(expanded) } : {})}
        style={{ paddingLeft: depth * 12 + 6 }}
        className="cf-focusable flex w-full items-baseline gap-1.5 py-1 pr-2 text-left hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
      >
        {expandable ? (
          expanded ? (
            <ChevronDown size={10} className="shrink-0 text-[var(--cf-text-muted)]" />
          ) : (
            <ChevronRight size={10} className="shrink-0 text-[var(--cf-text-muted)]" />
          )
        ) : (
          <span className="w-2.5 shrink-0" />
        )}
        <span className="shrink-0 font-mono text-badge text-[var(--cf-text)]">{variable.name}</span>
        <span className="truncate font-mono text-badge text-[var(--cf-text-muted)]">{variable.value}</span>
      </button>
      {expanded?.map((child) => (
        <VariableRow key={`${variable.object_id}-${child.name}`} variable={child} depth={depth + 1} />
      ))}
    </>
  );
}

/** Run and Debug: launch a program, stop where you asked, and look around.
 *
 * Node runs on the built-in backend (the runtime *is* the debugger, so nothing to install);
 * every other language drives an installed debug adapter over DAP — the same arrangement VS Code
 * has, where the adapter arrives in an extension. Both report through identical events, so this
 * panel never learns which one is behind a session.
 */
export function DebugPanel({
  repoPath,
  suggestedProgram,
  onOpenFrame,
}: {
  repoPath: string;
  /** The active editor file, offered as the thing to run when it's a script. */
  suggestedProgram: string | null;
  onOpenFrame: (file: string, line: number) => void;
}) {
  const t = useT();
  const status = useDebugStore((s) => s.status);
  const frames = useDebugStore((s) => s.frames);
  const selectedFrame = useDebugStore((s) => s.selectedFrame);
  const variables = useDebugStore((s) => s.variables);
  const consoleLines = useDebugStore((s) => s.console);
  const error = useDebugStore((s) => s.error);
  const breakpoints = useDebugStore((s) => s.breakpoints);
  const [program, setProgram] = useState("");
  const [adapterId, setAdapterId] = useState("node");
  /** Overrides the preset's binary — for an adapter that isn't on PATH, or a custom one. */
  const [adapterCommand, setAdapterCommand] = useState("");
  const [expression, setExpression] = useState("");
  const consoleRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    useDebugStore.getState().init();
  }, []);

  // The active file is the default program, but only until the user types their own.
  const [touched, setTouched] = useState(false);
  useEffect(() => {
    if (touched || !suggestedProgram) return;
    setProgram(suggestedProgram);
    // The file decides the language: opening a .py and hitting play should not try Node.
    const matched = adapterForFile(suggestedProgram);
    if (matched) {
      setAdapterId(matched.id);
      setAdapterCommand(matched.command ?? "");
    }
  }, [suggestedProgram, touched]);

  useEffect(() => {
    consoleRef.current?.scrollTo({ top: consoleRef.current.scrollHeight });
  }, [consoleLines.length]);

  const running = status !== "idle";
  const paused = status === "paused";
  const store = useDebugStore.getState();
  const breakpointCount = Object.values(breakpoints).reduce((sum, lines) => sum + lines.length, 0);

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="shrink-0 border-b border-[var(--cf-border)] p-2">
        <div className="flex items-center gap-1">
          <input
            value={program}
            onChange={(e) => {
              setTouched(true);
              setProgram(e.target.value);
            }}
            placeholder={t("debug.programPlaceholder")}
            disabled={running}
            className="min-w-0 flex-1 rounded-md border border-[var(--cf-border)] bg-[var(--cf-bg)] px-1.5 py-1 font-mono text-badge outline-none disabled:opacity-60"
          />
          {/* Which debugger runs it. Node is built in; the rest drive an installed adapter. */}
          <select
            value={adapterId}
            onChange={(e) => {
              setAdapterId(e.target.value);
              setAdapterCommand(adapterById(e.target.value).command ?? "");
            }}
            disabled={running}
            className="shrink-0 rounded-md border border-[var(--cf-border)] bg-[var(--cf-bg)] px-1 py-1 text-badge outline-none disabled:opacity-60"
          >
            {DEBUG_ADAPTERS.map((adapter) => (
              <option key={adapter.id} value={adapter.id}>
                {adapter.label}
              </option>
            ))}
          </select>
          {running ? (
            <IconButton label="debug.stop" icon={StopSquare} variant="danger" onClick={() => void store.stop()} />
          ) : (
            /* `Play` runs the program and, below, resumes a paused one. The two are mutually
               exclusive on screen and both mean "execute", so they share the glyph on purpose. */
            <IconButton
              label="debug.start"
              icon={Play}
              variant="success"
              disabled={!program.trim()}
              onClick={() =>
                program.trim() &&
                void store.start(repoPath, program.trim(), adapterById(adapterId), adapterCommand)
              }
            />
          )}
        </div>

        {running && (
          <div className="mt-1.5 flex items-center gap-1">
            <IconButton
              label={paused ? "debug.continue" : "debug.pauseRun"}
              icon={paused ? Play : Pause}
              onClick={() => (paused ? void store.resume() : void store.pause())}
            />
            <IconButton label="debug.stepOver" icon={Redo2} disabled={!paused} onClick={() => void store.step("over")} />
            <IconButton
              label="debug.stepInto"
              icon={CornerDownRight}
              disabled={!paused}
              onClick={() => void store.step("into")}
            />
            <IconButton
              label="debug.stepOut"
              icon={CornerRightUp}
              disabled={!paused}
              onClick={() => void store.step("out")}
            />
            <span className="ml-auto text-badge text-[var(--cf-text-muted)]">
              {paused ? t("debug.paused") : t("debug.runningState")}
            </span>
          </div>
        )}

        {!running && adapterById(adapterId).command !== null && (
          <div className="mt-1.5">
            <input
              value={adapterCommand}
              onChange={(e) => setAdapterCommand(e.target.value)}
              placeholder={t("debug.adapterPlaceholder")}
              className="w-full rounded-md border border-[var(--cf-border)] bg-[var(--cf-bg)] px-1.5 py-1 font-mono text-badge outline-none"
            />
            <p className="mt-0.5 text-badge text-[var(--cf-text-muted)]">
              {t("debug.adapterHint", { install: adapterById(adapterId).install })}
            </p>
          </div>
        )}

        {!running && (
          <p className="mt-1.5 text-badge text-[var(--cf-text-muted)]">
            {breakpointCount > 0 ? t("debug.breakpointCount", { n: breakpointCount }) : t("debug.noBreakpoints")}
          </p>
        )}
        {error && <p className="mt-1.5 text-badge text-[var(--cf-danger)]">{error}</p>}
      </div>

      <div className="min-h-0 flex-1 overflow-auto">
        {paused && (
          <>
            <p className="px-2 py-1 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
              {t("debug.callStack")}
            </p>
            {frames.map((frame, index) => (
              <button
                key={frame.id}
                onClick={() => {
                  void store.selectFrame(index);
                  if (frame.file.includes("/") || frame.file.includes("\\")) onOpenFrame(frame.file, frame.line);
                }}
                className={`flex w-full items-baseline gap-1.5 px-2 py-0.5 text-left ${
                  index === selectedFrame ? "bg-[var(--cf-accent-soft)]" : "hover:bg-black/[0.03] dark:hover:bg-white/[0.04]"
                }`}
              >
                <span className="shrink-0 font-mono text-badge text-[var(--cf-text)]">{frame.name}</span>
                <span className="truncate text-badge text-[var(--cf-text-muted)]">
                  {fileName(frame.file)}:{frame.line}
                </span>
              </button>
            ))}

            <p className="px-2 pt-2 text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
              {t("debug.variables")}
            </p>
            {variables.length === 0 ? (
              <p className="px-2 py-1 text-badge text-[var(--cf-text-muted)]">{t("debug.noVariables")}</p>
            ) : (
              variables.map((variable) => (
                <VariableRow key={variable.name} variable={variable} depth={0} />
              ))
            )}
          </>
        )}
      </div>

      <div className="flex h-[38%] shrink-0 flex-col border-t border-[var(--cf-border)]">
        <div className="flex items-center gap-1 px-2 py-1">
          <span className="text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
            {t("debug.console")}
          </span>
          {/* `Eraser`, not `Trash2`: the console is a live view and clearing it destroys nothing
              that was stored (icon dictionary, §II.3). */}
          <IconButton label="debug.clearConsole" icon={Eraser} className="ml-auto" onClick={() => store.clearConsole()} />
        </div>
        <div ref={consoleRef} className="min-h-0 flex-1 overflow-auto px-2 pb-1 font-mono text-badge">
          {consoleLines.map((line, index) => (
            <div
              key={index}
              className={`whitespace-pre-wrap break-all ${
                line.kind === "error" || line.kind === "stderr"
                  ? "text-[var(--cf-danger)]"
                  : line.kind === "input"
                    ? "text-[var(--cf-accent)]"
                    : "text-[var(--cf-text-muted)]"
              }`}
            >
              {line.kind === "input" ? "› " : ""}
              {line.text}
            </div>
          ))}
        </div>
        <input
          value={expression}
          onChange={(e) => setExpression(e.target.value)}
          onKeyDown={(e) => {
            if (e.key !== "Enter" || !expression.trim()) return;
            e.preventDefault();
            void store.evaluate(expression.trim());
            setExpression("");
          }}
          // Only meaningful while paused: an expression needs a frame to be evaluated in.
          disabled={!paused}
          placeholder={paused ? t("debug.evaluatePlaceholder") : t("debug.evaluateDisabled")}
          className="shrink-0 border-t border-[var(--cf-border)] bg-transparent px-2 py-1 font-mono text-badge outline-none disabled:opacity-60"
        />
      </div>
    </div>
  );
}
