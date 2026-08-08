import type { ReactNode } from "react";

/**
 * An eyebrow label above a control, wrapping it.
 *
 * This existed twice, byte for byte: once exported from `stream/shared.tsx` and once redeclared
 * privately inside `GrpcPanel.tsx`, which never imported the first. Giving it a file of its own also
 * settles the name collision that made the copy easy to write — `ApiModal`'s `Field` is an
 * `<input>`, this is a `<label>`, and they are not interchangeable.
 *
 * The `<label>` wraps its control rather than pointing at it with `htmlFor`, which associates the
 * two without either side needing an id.
 */
export function LabeledField({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-0.5 block text-badge font-semibold uppercase tracking-wide text-[var(--cf-text-muted)]">
        {label}
      </span>
      {children}
    </label>
  );
}
