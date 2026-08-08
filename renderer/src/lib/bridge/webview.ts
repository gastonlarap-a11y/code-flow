import type { UnlistenFn } from "./host";

/**
 * Drag-and-drop file paths, rebuilt on DOM events.
 *
 * This is the one bypass whose *mechanism* changes rather than its wiring. A native webview handler suppresses DOM
 * drop events and delivers paths through a webview channel instead; Electron delivers the DOM
 * events normally, but a `File` in the renderer has no path — the preload recovers it through
 * `webUtils.getPathForFile`. The payload shape is reproduced exactly, so `ImportModal` keeps its
 * logic.
 *
 * Listeners are attached to the document rather than to a specific element because 1.7.2
 * accepts a drop anywhere while the modal is open, with no hit-testing.
 */

export type DragDropPayload =
  | { type: "enter"; paths: string[]; position: { x: number; y: number } }
  | { type: "over"; position: { x: number; y: number } }
  | { type: "drop"; paths: string[]; position: { x: number; y: number } }
  | { type: "leave" };

export interface DragDropEvent {
  payload: DragDropPayload;
}

interface CurrentWebview {
  onDragDropEvent(handler: (event: DragDropEvent) => void): Promise<UnlistenFn>;
}

export function getCurrentWebview(): CurrentWebview {
  return {
    onDragDropEvent(handler) {
      // `dragenter` and `dragleave` fire for every element the pointer crosses, so a naive
      // handler flickers the drop target. Counting depth is what makes "leave" mean "left the
      // window" rather than "moved between two children".
      let depth = 0;

      const paths = (event: DragEvent): string[] => {
        const files = event.dataTransfer?.files;
        if (!files) return [];
        return Array.from(files)
          .map((file) => window.codeflow?.pathForFile(file) ?? "")
          .filter((path) => path.length > 0);
      };

      const position = (event: DragEvent) => ({ x: event.clientX, y: event.clientY });

      const onEnter = (event: DragEvent) => {
        event.preventDefault();
        depth += 1;
        if (depth === 1) handler({ payload: { type: "enter", paths: [], position: position(event) } });
      };

      const onOver = (event: DragEvent) => {
        // Without this the browser treats the drop as a navigation and opens the file.
        event.preventDefault();
        handler({ payload: { type: "over", position: position(event) } });
      };

      const onLeave = (event: DragEvent) => {
        event.preventDefault();
        depth = Math.max(0, depth - 1);
        if (depth === 0) handler({ payload: { type: "leave" } });
      };

      const onDrop = (event: DragEvent) => {
        event.preventDefault();
        depth = 0;
        handler({ payload: { type: "drop", paths: paths(event), position: position(event) } });
      };

      document.addEventListener("dragenter", onEnter);
      document.addEventListener("dragover", onOver);
      document.addEventListener("dragleave", onLeave);
      document.addEventListener("drop", onDrop);

      return Promise.resolve(() => {
        document.removeEventListener("dragenter", onEnter);
        document.removeEventListener("dragover", onOver);
        document.removeEventListener("dragleave", onLeave);
        document.removeEventListener("drop", onDrop);
      });
    },
  };
}
