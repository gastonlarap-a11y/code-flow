import { useState } from "react";
import { Download, FileCode } from "lucide-react";
import { Modal } from "../common/Modal";
import { Button } from "../common/Button";
import { Select } from "../common/Select";
import { DbConnectionPanel } from "./DbConnectionPanel";
import { importSql, type SqlImportDialect } from "../../lib/dbml/importers/sql";
import { importPrisma } from "../../lib/dbml/importers/prisma";
import { emitDbml } from "../../lib/dbml/emitDbml";
import { connectionRefusal } from "../../lib/dbml/connectionError";
import { dbmlIntrospectDatabase } from "../../lib/ipc/commands";
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
type Source = SqlImportDialect | "prisma" | "database";

const SOURCES: readonly { value: Source; label: TranslationKey; extensions: string[] }[] = [
  { value: "postgres", label: "dbml.import.postgres", extensions: ["sql"] },
  { value: "mysql", label: "dbml.import.mysql", extensions: ["sql"] },
  { value: "mssql", label: "dbml.import.mssql", extensions: ["sql"] },
  { value: "prisma", label: "dbml.import.prisma", extensions: ["prisma"] },
  { value: "database", label: "dbml.import.database", extensions: [] },
];

/** The refusals the document name can earn, as the key that says so. */
const NAME_ERRORS = {
  empty: "dbml.error.empty",
  escapes: "dbml.error.escapes",
  absolute: "dbml.error.absolute",
  invalidChar: "dbml.error.invalidChar",
  exists: "dbml.error.exists",
} as const satisfies Record<string, TranslationKey>;

/** Turns whatever the pasted text is into DBML. Not reached for `database`, which has no text. */
function convert(text: string, source: Exclude<Source, "database">): string {
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
  const [connectionId, setConnectionId] = useState<string | null>(null);

  const active = SOURCES.find((option) => option.value === source) ?? SOURCES[0]!;
  /** What "there is something to import" means, which differs by source. */
  const hasInput = source === "database" ? connectionId !== null : text.trim().length > 0;

  /** Reads the chosen database and writes what it found as DBML. */
  const readDatabase = async (): Promise<string> => {
    if (connectionId === null) throw new Error(t("dbml.db.connection"));

    const snapshot = await dbmlIntrospectDatabase(connectionId);
    const dbml = emitDbml(snapshot);
    if (dbml.trim().length === 0) throw new Error(t("dbml.import.emptyDatabase"));

    return dbml;
  };

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
      // Produced before the file is named on disk: a script that cannot be read, or a database that
      // refuses, should say so rather than leave an empty document behind.
      const dbml = source === "database" ? await readDatabase() : convert(text, source);
      const failure = await createDocument(rootPath, name, dbml);
      if (failure === null) {
        onClose();
        return;
      }
      setNameError(failure);
    } catch (e) {
      // A database that refused is its own sentence, not a stack: the driver already said what is
      // wrong, and wrapping it in "could not import" twice buries it.
      const refusal = connectionRefusal(e);
      pushErrorToast(
        refusal === null ? t("dbml.import.failed", { error: String(e) }) : t("dbml.db.testFailed", { error: refusal }),
      );
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
            disabled={busy || !hasInput || name.trim().length === 0}
            onClick={() => void submit()}
          >
            {busy && source === "database" ? t("dbml.import.reading") : t("dbml.import.action")}
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
          {/* A database has no file to open — its own panel owns everything that source needs. */}
          {source !== "database" && (
            <Button variant="secondary" icon={FileCode} disabled={busy} onClick={() => void pick()}>
              {t("dbml.import.pickFile")}
            </Button>
          )}
        </div>

        {source === "database" ? (
          <DbConnectionPanel onReady={setConnectionId} disabled={busy} />
        ) : (
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
        )}

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
          <span className="text-badge text-[var(--cf-text-muted)]">
            {t(source === "database" ? "dbml.import.hintDatabase" : "dbml.import.hint")}
          </span>
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
