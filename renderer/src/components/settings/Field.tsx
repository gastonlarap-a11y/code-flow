import { useId, type ReactNode } from "react";

/**
 * Every text input in Settings, and the border and focus ring they share.
 *
 * The exact string used to live in `ProvidersSection` as `inputClass` and was then re-typed by hand
 * in `AzureDevOpsSettings`, `GitHubSettings` and `modelPicker` — four copies of one decision. It is
 * a constant rather than a component because several of these inputs are `<textarea>`s and one is a
 * file path with a browse button glued to it; what they share is the box, not the element.
 */
export const FIELD_INPUT =
  "w-full rounded-md border border-[var(--cf-border)] bg-transparent px-2.5 py-1.5 text-body " +
  "outline-none focus:border-[var(--cf-accent)] disabled:opacity-50";

/** What a field hands its control so the pair is associated rather than merely adjacent. */
export interface FieldControlProps {
  id: string;
  "aria-describedby"?: string;
}

/**
 * A labelled setting: label on top, control, optional hint below.
 *
 * The label is passed to the control instead of just sitting above it. Settings had **zero**
 * `htmlFor` against 23 inputs and 12 labels — four of those labels are siblings of their input, so
 * they look like labels and name nothing, and about twenty inputs had only a placeholder. A
 * placeholder is not a name: it disappears the moment you type.
 *
 * `children` is a function rather than an element so the id can reach the control without
 * `cloneElement` guessing which descendant is the input — several fields wrap theirs in a row with
 * a button beside it.
 *
 * `action` puts a control (e.g. "refresh models") on the right of the label line.
 */
export function Field({
  label,
  hint,
  action,
  children,
}: {
  label: string;
  hint?: ReactNode;
  action?: ReactNode;
  children: (control: FieldControlProps) => ReactNode;
}) {
  const id = useId();
  const hintId = `${id}-hint`;
  // Built conditionally: `exactOptionalPropertyTypes` rejects an explicit `undefined` here.
  const control: FieldControlProps = hint ? { id, "aria-describedby": hintId } : { id };

  return (
    <div>
      <div className="mb-1 flex items-center justify-between gap-2">
        <label htmlFor={id} className="block text-relaxed font-medium text-[var(--cf-text)]">
          {label}
        </label>
        {action}
      </div>
      {children(control)}
      {hint && (
        <p id={hintId} className="mt-1 text-body leading-snug text-[var(--cf-text-muted)]">
          {hint}
        </p>
      )}
    </div>
  );
}
