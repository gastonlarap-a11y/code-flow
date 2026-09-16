import { LayoutGrid, Scan, ZoomIn, ZoomOut } from "lucide-react";
import { IconButton } from "../common/IconButton";
import type { DbmlCanvasHandle } from "./DbmlCanvas";

/** One click of the zoom buttons. */
const ZOOM_STEP = 1.2;

/**
 * The floating cluster that drives a `DbmlCanvas`: zoom, fit, and optionally re-arrange.
 *
 * Extracted the moment a second surface grew a canvas — the schema module and the Editor's `.dbml`
 * preview — because the alternative is the same four buttons typed twice, which is how two control
 * clusters end up a pixel apart and one of them missing a label.
 *
 * `onArrange` is optional rather than always present: re-arranging means *forgetting the positions
 * a person saved*, and the preview has none to forget (DBML-018).
 */
export function DbmlViewportControls({
  canvas,
  onArrange,
}: {
  canvas: React.RefObject<DbmlCanvasHandle | null>;
  onArrange?: () => void;
}) {
  return (
    <div className="absolute bottom-2 right-2 flex items-center gap-0.5 rounded-control border border-[var(--cf-border)] bg-[var(--cf-surface)] p-0.5 shadow-sm">
      <IconButton label="dbml.zoomOut" icon={ZoomOut} size="sm" onClick={() => canvas.current?.zoomBy(1 / ZOOM_STEP)} />
      <IconButton label="dbml.zoomIn" icon={ZoomIn} size="sm" onClick={() => canvas.current?.zoomBy(ZOOM_STEP)} />
      <IconButton label="dbml.fitView" icon={Scan} size="sm" onClick={() => canvas.current?.fit()} />
      {onArrange && <IconButton label="dbml.arrange" icon={LayoutGrid} size="sm" onClick={onArrange} />}
    </div>
  );
}
