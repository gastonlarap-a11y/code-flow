import { Check, Copy } from "lucide-react";
import { Button } from "./Button";
import { useCopy } from "../../lib/ui/useCopy";
import { useT } from "../../state/languageStore";

/**
 * The button an AI answer ends with: copies the whole thing, and says so.
 *
 * A labelled `Button` rather than an `IconButton`, which is the point of it existing. Every copy
 * affordance the AI panel had was a bare icon — one of them dimmed in a corner of the summary, and
 * only present when the review had found something — so a review with no findings offered no way to
 * copy it at all, and the ones that did offered nothing a reader would recognise as "copy the
 * answer". `.claude/rules/renderer.md` already says why that fails: a control nobody can find by
 * looking is not a control.
 *
 * `text` is the model's raw answer, not the rendered markdown: summary, findings and the run's
 * footer stamp, exactly as it sits on screen, and exactly what pastes back into a ticket or a chat.
 */
export function CopyAnswer({ text, className = "" }: { text: string; className?: string }) {
  const t = useT();
  const [copied, copy] = useCopy();

  return (
    <Button
      variant="ghost"
      size="sm"
      icon={copied ? Check : Copy}
      className={`shrink-0 ${className}`}
      onClick={() => copy(text)}
    >
      {t(copied ? "ai.answerCopied" : "ai.copyAnswer")}
    </Button>
  );
}
