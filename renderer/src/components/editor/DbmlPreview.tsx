import { useMemo, useRef, useState } from "react";
import { Table2 } from "lucide-react";
import { parseDbmlModel } from "../../lib/dbml/parse";
import type { Point } from "../../lib/dbml/layout";
import { DbmlCanvas, type DbmlCanvasHandle } from "../dbml/DbmlCanvas";
import { DbmlViewportControls } from "../dbml/DbmlViewportControls";
import { EmptyState } from "../common/EmptyState";
import { useT } from "../../state/languageStore";

/**
 * The `.dbml` quick look inside the Editor (DBML-018).
 *
 * Parsing and drawing live together here so `EditorPane` can reach both through one `lazy()`.
 * `@dbml/core` is 15 MB minified — four times Monaco — and `EditorPane` used to call the parser from
 * its own render, which put all of it in the chunk that loads when the Editor opens, for every file.
 * Only `.dbml` files ever need it, and now only they pay for it.
 *
 * **It draws with the schema module's canvas, not a second diagram.** There used to be two: this one
 * laid cards out in a `flex-wrap` and joined their *centres* with straight dashed lines, while the
 * module next door had auto-layout, orthogonal routes anchored to the column they name, and
 * relationships that explain themselves on hover. Keeping the lesser one meant a `.dbml` file looked
 * different depending on which door you opened it through.
 */
export function DbmlPreview({ content, path }: { content: string; path: string }) {
  const t = useT();
  const parsed = useMemo(() => parseDbmlModel(content), [content]);
  const canvasRef = useRef<DbmlCanvasHandle>(null);

  // Dragging works and is forgotten when the tab closes: persistence is keyed on a project and a
  // document (DBML-005), and that is the schema module's job. The auto-layout is deterministic, so
  // reopening the file gives back the same picture rather than a shuffled one.
  const [positions, setPositions] = useState<Record<string, Point>>({});

  if (!parsed.ok) {
    return (
      <div className="h-full overflow-auto p-4">
        <p className="rounded-md border border-[var(--cf-danger)] bg-[color-mix(in_oklab,var(--cf-danger)_10%,transparent)] p-3 font-mono text-ui text-[var(--cf-danger)]">
          {parsed.error}
        </p>
      </div>
    );
  }

  if (parsed.model.tables.length === 0) {
    return <EmptyState icon={Table2} title={t("editor.dbmlEmpty")} />;
  }

  return (
    <div className="relative h-full">
      <DbmlCanvas
        ref={canvasRef}
        model={parsed.model}
        documentKey={path}
        positions={positions}
        onPlace={(tableKey, point) => setPositions((current) => ({ ...current, [tableKey]: point }))}
      />
      <DbmlViewportControls canvas={canvasRef} />
    </div>
  );
}
