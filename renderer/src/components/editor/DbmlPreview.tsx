import { forwardRef, useMemo } from "react";
import { parseDbml } from "../../lib/dbml";
import { DbmlDiagram } from "./DbmlDiagram";

// Parsing and drawing live together here so `EditorPane` can reach both through one `lazy()`.
//
// `@dbml/core` is 15 MB minified — four times Monaco — and `EditorPane` used to call `parseDbml`
// from its own render, which put all of it in the chunk that loads when the Editor opens, for
// every file. Only `.dbml` files ever need it, and now only they pay for it.
//
// `forwardRef` because split mode hands the preview a scroll ref to keep it in step with the
// editor beside it; `DbmlDiagram` already forwards one.
export const DbmlPreview = forwardRef<HTMLDivElement, { content: string; onScroll?: () => void }>(
  function DbmlPreview({ content, onScroll }, ref) {
    const schema = useMemo(() => parseDbml(content), [content]);

    return <DbmlDiagram ref={ref} schema={schema} onScroll={onScroll} />;
  },
);
