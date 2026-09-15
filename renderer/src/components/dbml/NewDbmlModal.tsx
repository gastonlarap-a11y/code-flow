import { useState } from "react";
import { Database } from "lucide-react";
import { Modal } from "../common/Modal";
import { Button } from "../common/Button";
import { useT } from "../../state/languageStore";
import { useDbmlStore } from "../../state/dbmlStore";
import type { TranslationKey } from "../../lib/i18n/translations";

/** Why the name was refused, as the key that says so in the user's language. */
const ERROR_KEYS = {
  empty: "dbml.error.empty",
  escapes: "dbml.error.escapes",
  absolute: "dbml.error.absolute",
  invalidChar: "dbml.error.invalidChar",
  exists: "dbml.error.exists",
} as const satisfies Record<string, TranslationKey>;

/**
 * Names a new `.dbml` document and creates it.
 *
 * The validation is `lib/dbml/documentPath.ts`, called through the store — pure, tested, and the
 * same rules whichever path reaches it. What is left here is showing the refusal under the field.
 */
export function NewDbmlModal({ rootPath, onClose }: { rootPath: string; onClose: () => void }) {
  const t = useT();
  const createDocument = useDbmlStore((s) => s.createDocument);
  const [name, setName] = useState("");
  const [error, setError] = useState<keyof typeof ERROR_KEYS | null>(null);
  const [creating, setCreating] = useState(false);

  const submit = async () => {
    setCreating(true);
    const failure = await createDocument(rootPath, name);
    setCreating(false);
    if (failure === null) {
      onClose();
      return;
    }
    setError(failure);
  };

  return (
    <Modal title="dbml.newDocumentTitle" icon={Database} size="sm" onClose={onClose}>
      <form
        className="flex flex-col gap-3"
        onSubmit={(e) => {
          e.preventDefault();
          void submit();
        }}
      >
        <label className="flex flex-col gap-1.5">
          <span className="text-relaxed text-[var(--cf-text)]">{t("dbml.nameLabel")}</span>
          <input
            autoFocus
            value={name}
            onChange={(e) => {
              setName(e.target.value);
              // Clears on edit: a refusal that outlives the thing it refused reads as the field
              // still being wrong.
              setError(null);
            }}
            placeholder={t("dbml.namePlaceholder")}
            className="cf-focusable w-full rounded-control border border-[var(--cf-border)] bg-[var(--cf-bg)] px-2 py-1.5 text-body text-[var(--cf-text)] outline-none"
          />
          {/* The extension is appended when missing, so the hint is not a rule to obey. */}
          <span className="text-badge text-[var(--cf-text-muted)]">.dbml</span>
        </label>

        {error !== null && (
          <p role="alert" className="text-body text-[var(--cf-danger)]">
            {t(ERROR_KEYS[error])}
          </p>
        )}

        <div className="flex justify-end gap-2">
          <Button variant="secondary" type="button" onClick={onClose}>
            {t("dbml.cancel")}
          </Button>
          <Button variant="primary" type="submit" pending={creating}>
            {t("dbml.create")}
          </Button>
        </div>
      </form>
    </Modal>
  );
}
