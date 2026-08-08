import { useEffect, useState } from "react";
import { formatElapsed } from "../../lib/ui/elapsed";
import { useT } from "../../state/languageStore";

/**
 * A running clock for a job that is still in flight.
 *
 * Ticks once a second and stops when it unmounts, which is when the run ends — nothing here keeps
 * a timer alive behind a finished job.
 */
export function ElapsedTime({ since, className = "" }: { since: number; className?: string }) {
  const t = useT();
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1_000);
    return () => clearInterval(timer);
  }, []);

  return (
    <span className={className}>{t("ai.elapsed", formatElapsed(now - since))}</span>
  );
}
