import { useState } from "react";
import { Download, FileCode } from "lucide-react";
import { Modal } from "../common/Modal";
import { Button } from "../common/Button";
import { Select } from "../common/Select";
import { importSql, type SqlImportDialect } from "../../lib/dbml/importers/sql";
import { importPrisma } from "../../lib/dbml/importers/prisma";
import { apiPickFile, apiReadTextFile } from "../../lib/ipc/apiCommands";
import { useDbmlStore } from "../../state/dbmlStore";
import { pushErrorToast } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";

/**
 * Brings a schema in from SQL or from Prisma (DBML-022).
 *
 * **It creates a document rather than overwriting the open one.** "Import" and "replace what I am
 * looking at" are different asks, and only one of them is reversible — a new file leaves whatever
 * was open exactly where it was, and the picker switches to it when it is written.
 */
type Source = SqlImportDialect | "prisma";

const SOURCES: readonly { value: Source; label: TranslationKey; extensions: string[] }[] = [
  { value: "postgres", label: "dbml.import.postgres", extensions: ["sql"] },
  { value: "mysql", label: "dbml.import.mysql", extensions: ["sql"] },
  { value: "mssql", label: "dbml.import.mssql", extensions: ["sql"] },
  { value: "prisma", label: "dbml.import.prisma", extensions: ["prisma"] },
];

/** The refusals the document name can earn, as the key that says so. */
const NAME_ERRORS = {
  empty: "dbml.error.empty",
  escapes: "dbml.error.escapes",
  absolute: "dbml.error.absolute",
  invalidChar: "dbml.error.invalidChar",
  exists: "dbml.error.exists",
} as const satisfies Record<string, TranslationKey>;

function convert(text: string, source: Source): string {
  return source === "prisma" ? importPrisma(text) : importSql(text, source);
}

export function ImportDbmlModal({ rootPath, onClose }: { rootPath: string; onClose: () => void }) {
  const t = useT();
  const createDocument = useDbmlStore((s) => s.createDocument);

  const [source, setSource] = useState<Source>("postgres");
  const [text, setText] = useState("");
  const [name, setName] = useState("");
  const [nameError, setNameError] = useState<keyof typeof NAME_ERRORS | null>(null);
  const [busy, setBusy] = useState(false);

  const active = SOURCES.find((option) => option.value === source) ?? SOURCES[0]!;

  const pick = async () => {
    const path = await apiPickFile(active.extensions);
    if (path === null) return;
    try {
      setText(await apiReadTextFile(path));
      // Only when the field is still untouched: a name the user typed outweighs one derived here.
      if (name.trim().length === 0) {
        setName((path.split(/[\\/]/).pop() ?? "").replace(/\.(sql|prisma)$/i, ""));
      }
    } catch (e) {
      pushErrorToast(String(e));
    }
  };

  const submit = async () => {
    setBusy(true);
    setNameError(null);
    try {
      // Converted before the file is named on disk: a script that cannot be read should say so
      // rather than leave an empty document behind.
      const dbml = convert(text, source);
      const failure = await createDocument(rootPath, name, dbml);
      if (failure === null) {
        onClose();
        return;
      }
      setNameError(failure);
    } catch (e) {
      pushErrorToast(t("dbml.import.failed", { error: String(e) }));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal
      title="dbml.import.title"
      icon={Download}
      size="lg"
      scroll
      dismissible={!busy}
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={busy}>
            {t("dbml.cancel")}
          </Button>
          <Button
            variant="primary"
            icon={Download}
            pending={busy}
            disabled={busy || text.trim().length === 0 || name.trim().length === 0}
            onClick={() => void submit()}
          >
            {t("dbml.import.action")}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <div className="flex items-end gap-2">
          <label className="flex min-w-0 flex-1 flex-col gap-1.5">
            <span className="text-relaxed text-[var(--cf-text)]">{t("dbml.import.source")}</span>
            <Select
              value={source}
              onChange={(value) => setSource(value as Source)}
              ariaLabel={t("dbml.import.source")}
              options={SOURCES.map((option) => ({ value: option.value, label: t(option.label) }))}
            />
          </label>
          <Button variant="secondary" icon={FileCode} disabled={busy} onClick={() => void pick()}>
            {t("dbml.import.pickFile")}
          </Button>
        </div>

        <label className="flex flex-col gap-1.5">
          <span className="text-relaxed text-[var(--cf-text)]">{t("dbml.import.contents")}</span>
          <textarea
            value={text}
            onChange={(e) => setText(e.target.value)}
            disabled={busy}
            rows={10}
            spellCheck={false}
            placeholder={t("dbml.import.placeholder")}
            className="w-full resize-y rounded-md border border-[var(--cf-border)] bg-transparent px-2.5 py-1.5 font-mono text-body leading-relaxed outline-none focus:border-[var(--cf-accent)] disabled:opacity-60"
          />
        </label>

        <label className="flex flex-col gap-1.5">
          <span className="text-relaxed text-[var(--cf-text)]">{t("dbml.nameLabel")}</span>
          <input
            value={name}
            onChange={(e) => {
              setName(e.target.value);
              setNameError(null);
            }}
            disabled={busy}
            placeholder={t("dbml.namePlaceholder")}
            className="cf-focusable w-full rounded-control border border-[var(--cf-border)] bg-[var(--cf-bg)] px-2 py-1.5 text-body text-[var(--cf-text)] outline-none disabled:opacity-60"
          />
          <span className="text-badge text-[var(--cf-text-muted)]">{t("dbml.import.hint")}</span>
        </label>

        {nameError !== null && (
          <p role="alert" className="text-body text-[var(--cf-danger)]">
            {t(NAME_ERRORS[nameError])}
          </p>
        )}
      </div>
    </Modal>
  );
}
