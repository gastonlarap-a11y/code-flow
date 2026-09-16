import { useEffect, useMemo, useRef, useState } from "react";
import {
  Database,
  FilePlus2,
  LayoutGrid,
  RotateCw,
  Save,
  Scan,
  Sparkles,
  Upload,
  ZoomIn,
  ZoomOut,
} from "lucide-react";
import { Editor, OVERFLOW_SAFE_OPTIONS, monaco } from "../../lib/monacoEditor";
import { parseDbmlModel } from "../../lib/dbml/parse";
import { emptyModel, type DbmlSchemaModel } from "../../lib/dbml/model";
import { useDbmlStore } from "../../state/dbmlStore";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { useThemeStore } from "../../state/themeStore";
import { confirmAction } from "../../state/confirmStore";
import { useT } from "../../state/languageStore";
import { EmptyState } from "../common/EmptyState";
import { IconButton } from "../common/IconButton";
import { Button } from "../common/Button";
import { DbmlCanvas, type DbmlCanvasHandle } from "./DbmlCanvas";
import { NewDbmlModal } from "./NewDbmlModal";
import { ExportDbmlModal } from "./ExportDbmlModal";
import { DbmlAiModal } from "./DbmlAiModal";

/** One click of the zoom buttons. */
const ZOOM_STEP = 1.2;

/**
 * The schema designer: the `.dbml` documents of the open folder, one editor, and the diagram it
 * produces (DBML-002, DBML-008).
 *
 * `@dbml/core` is 15 MB — four times Monaco — so the whole view is behind a `lazy()` in `App.tsx`
 * and only a user who opens this module pays for it. The parse runs on the buffer rather than on
 * disk, which is what makes the diagram live.
 */
export function DbmlView() {
  const t = useT();
  const project = useWorkspaceStore((s) => s.activeProject());
  const rootPath = project?.local_path ?? null;
  const monacoTheme = useThemeStore((s) => s.monacoTheme);

  const documents = useDbmlStore((s) => s.documents);
  const activePath = useDbmlStore((s) => s.activePath);
  const source = useDbmlStore((s) => s.source);
  const dirty = useDbmlStore((s) => s.dirty);
  const saving = useDbmlStore((s) => s.saving);
  const positions = useDbmlStore((s) => s.positions);
  const loadDocuments = useDbmlStore((s) => s.loadDocuments);
  const openDocument = useDbmlStore((s) => s.openDocument);
  const setSource = useDbmlStore((s) => s.setSource);
  const save = useDbmlStore((s) => s.save);
  const placeTable = useDbmlStore((s) => s.placeTable);
  const arrangeAll = useDbmlStore((s) => s.arrangeAll);
  const reset = useDbmlStore((s) => s.reset);

  const [creating, setCreating] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [asking, setAsking] = useState(false);
  const canvasRef = useRef<DbmlCanvasHandle>(null);

  // The module is repo-scoped, so its documents follow the selected project — including back to
  // nothing when the last one is closed.
  useEffect(() => {
    reset();
    if (rootPath !== null) void loadDocuments(rootPath);
  }, [rootPath, loadDocuments, reset]);

  const parsed = useMemo(() => parseDbmlModel(source), [source]);

  // The diagram keeps the last model that parsed. Mid-edit, the text is invalid more often than not;
  // unmounting the canvas on every one of those keystrokes would blank the diagram and reset the
  // zoom, so the error is shown over the last good picture instead.
  //
  // Adjusted during render rather than in an effect — React's documented pattern for state derived
  // from a changing value: an effect would paint one frame of the stale model first. The condition
  // settles immediately, because `parsed` is memoised on `source` and so its model keeps its identity.
  const [model, setModel] = useState<DbmlSchemaModel>(emptyModel);
  if (parsed.ok && parsed.model !== model) setModel(parsed.model);

  if (project === null || rootPath === null) {
    return <EmptyState icon={Database} title={t("dbml.noDocumentOpen")} />;
  }

  const arrange = async () => {
    // Nothing to lose when nothing was placed by hand, so nothing to confirm.
    const hasPlacements = Object.keys(positions).length > 0;
    if (hasPlacements && !(await confirmAction(t("dbml.arrangeConfirm"), false, t("dbml.arrange")))) return;
    await arrangeAll(project.id);
    // After the cleared positions have re-rendered the layout, so the fit measures the new one.
    requestAnimationFrame(() => canvasRef.current?.fit());
  };

  return (
    <div className="flex h-full flex-col overflow-hidden">
      <div className="flex h-9 shrink-0 items-center gap-1.5 border-b border-[var(--cf-border)] px-2">
        <span className="text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
          {t("dbml.documents")}
        </span>

        {/* A select rather than a tree: these are a handful of files, and the picker is not the
            feature. The file explorer stays available in the Editor module. */}
        <select
          value={activePath ?? ""}
          onChange={(e) => {
            const next = e.target.value;
            if (next.length > 0) void openDocument(project.id, rootPath, next);
          }}
          aria-label={t("dbml.documents")}
          className="cf-focusable min-w-0 max-w-[280px] flex-1 truncate rounded-control border border-[var(--cf-border)] bg-[var(--cf-bg)] px-1.5 py-1 text-ui text-[var(--cf-text)] outline-none"
        >
          <option value="" disabled>
            {t("dbml.noDocumentOpen")}
          </option>
          {documents.map((path) => (
            <option key={path} value={path}>
              {path}
            </option>
          ))}
        </select>

        {dirty && <span className="text-badge text-[var(--cf-warning)]">{t("dbml.unsaved")}</span>}

        <div className="ml-auto flex items-center gap-1">
          <Button
            variant="secondary"
            size="sm"
            icon={Save}
            disabled={!dirty || activePath === null}
            pending={saving}
            onClick={() => void save(rootPath)}
          >
            {t("dbml.save")}
          </Button>
          <IconButton
            label="dbml.ai.action"
            icon={Sparkles}
            size="sm"
            disabled={activePath === null}
            onClick={() => setAsking(true)}
          />
          <IconButton
            label="dbml.export.action"
            icon={Upload}
            size="sm"
            disabled={activePath === null}
            onClick={() => setExporting(true)}
          />
          <IconButton label="dbml.reload" icon={RotateCw} size="sm" onClick={() => void loadDocuments(rootPath)} />
          <IconButton label="dbml.newDocument" icon={FilePlus2} size="sm" onClick={() => setCreating(true)} />
        </div>
      </div>

      {activePath === null ? (
        <EmptyState
          icon={Database}
          title={documents.length === 0 ? t("dbml.noDocuments") : t("dbml.noDocumentOpen")}
          subtitle={documents.length === 0 ? t("dbml.noDocumentsHint") : undefined}
        />
      ) : (
        <div className="flex min-h-0 flex-1">
          <div className="min-w-0 flex-1 border-r border-[var(--cf-border)]">
            <Editor
              height="100%"
              // No DBML language in Monaco; `sql` gets the comment and string colouring close
              // enough, and `path` keeps one model per document so undo history survives a switch.
              language="sql"
              path={`dbml:/${activePath}`}
              value={source}
              theme={monacoTheme}
              onChange={(next) => setSource(next ?? "")}
              onMount={(editor) => {
                editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
                  void useDbmlStore.getState().save(rootPath);
                });
              }}
              options={{
                ...OVERFLOW_SAFE_OPTIONS,
                minimap: { enabled: false },
                fontSize: 12,
                automaticLayout: true,
                scrollBeyondLastLine: false,
                tabSize: 2,
                lineNumbersMinChars: 3,
                overviewRulerLanes: 0,
                padding: { top: 8, bottom: 8 },
              }}
            />
          </div>

          <div className="relative min-w-0 flex-1">
            {model.tables.length === 0 ? (
              <EmptyState icon={Database} title={t("dbml.noTables")} />
            ) : (
              <>
                <DbmlCanvas
                  ref={canvasRef}
                  model={model}
                  documentKey={activePath}
                  positions={positions}
                  onPlace={(tableKey, point) => void placeTable(project.id, tableKey, point)}
                />
                <div className="absolute bottom-2 right-2 flex items-center gap-0.5 rounded-control border border-[var(--cf-border)] bg-[var(--cf-surface)] p-0.5 shadow-sm">
                  <IconButton
                    label="dbml.zoomOut"
                    icon={ZoomOut}
                    size="sm"
                    onClick={() => canvasRef.current?.zoomBy(1 / ZOOM_STEP)}
                  />
                  <IconButton
                    label="dbml.zoomIn"
                    icon={ZoomIn}
                    size="sm"
                    onClick={() => canvasRef.current?.zoomBy(ZOOM_STEP)}
                  />
                  <IconButton label="dbml.fitView" icon={Scan} size="sm" onClick={() => canvasRef.current?.fit()} />
                  <IconButton label="dbml.arrange" icon={LayoutGrid} size="sm" onClick={() => void arrange()} />
                </div>
              </>
            )}

            {!parsed.ok && (
              <p
                role="status"
                className="absolute inset-x-2 top-2 rounded-md border border-[var(--cf-danger)] bg-[color-mix(in_oklab,var(--cf-danger)_10%,var(--cf-surface))] p-3 font-mono text-ui text-[var(--cf-danger)] shadow-sm"
              >
                {parsed.error}
              </p>
            )}
          </div>
        </div>
      )}

      {creating && <NewDbmlModal rootPath={rootPath} onClose={() => setCreating(false)} />}

      {exporting && activePath !== null && (
        <ExportDbmlModal source={source} relPath={activePath} onClose={() => setExporting(false)} />
      )}

      {/* An accepted proposal lands in the buffer, not on disk: the save button and Ctrl+Z keep
          owning it, which is the same bargain the editor's inline edit makes (DBML-017). */}
      {asking && activePath !== null && (
        <DbmlAiModal
          source={source}
          relPath={activePath}
          onApply={setSource}
          onClose={() => setAsking(false)}
        />
      )}
    </div>
  );
}
