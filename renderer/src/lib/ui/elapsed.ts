/**
 * How long a run has been going, as the two parts the label interpolates.
 *
 * A run had no visible duration at all: "Working…" read the same at four seconds and at five
 * minutes, so the only way to tell a slow review from a wedged one was to wait longer. The clock is
 * the cheapest thing that makes the difference legible.
 */
export interface Elapsed extends Record<string, string> {
  minutes: string;
  seconds: string;
}

/** Formats a duration in milliseconds as `m:ss`, clamped at zero. */
export function formatElapsed(milliseconds: number): Elapsed {
  const total = Math.max(0, Math.floor(milliseconds / 1000));
  return {
    minutes: String(Math.floor(total / 60)),
    seconds: String(total % 60).padStart(2, "0"),
  };
}
