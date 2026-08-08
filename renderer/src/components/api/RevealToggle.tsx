import { Eye, EyeOff } from "lucide-react";
import { Button } from "../common/Button";
import { useT } from "../../state/languageStore";

/**
 * "Show / hide this secret", once.
 *
 * The same twenty-pixel eye was written three times — twice in `AuthPanel` (the field itself and the
 * JWT preview) and once per row in `EnvironmentModal` — and all three said what state they were in
 * *only* by swapping the glyph. That is the failure §II.6 row 8 names: a control whose state is
 * carried by an icon alone is unreadable to anyone who does not already know which eye means which.
 *
 * So the label is text, not a tooltip. It names the **action** rather than the state — "Show value",
 * not "Visible" — because that string is also the button's accessible name, and a button's name has
 * to describe what pressing it does. The glyph stays as the thing you spot from across the row.
 */
export function RevealToggle({
  revealed,
  onToggle,
  className,
}: {
  revealed: boolean;
  onToggle: () => void;
  /** Layout only — margins inside the field row that owns this. */
  className?: string;
}) {
  const t = useT();

  return (
    <Button
      variant="ghost"
      size="sm"
      icon={revealed ? EyeOff : Eye}
      onClick={onToggle}
      {...(className ? { className } : {})}
    >
      {t(revealed ? "api.secret.mask" : "api.secret.reveal")}
    </Button>
  );
}
