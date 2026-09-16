import { useEffect, useState } from "react";
import { Plug, Plus, Trash2 } from "lucide-react";
import { Button } from "../common/Button";
import { Checkbox } from "../common/Checkbox";
import { IconButton } from "../common/IconButton";
import { Select } from "../common/Select";
import {
  dbmlDeleteConnection,
  dbmlListConnections,
  dbmlSaveConnection,
  dbmlTestConnection,
} from "../../lib/ipc/commands";
import { apiPickFile } from "../../lib/ipc/apiCommands";
import { connectionRefusal } from "../../lib/dbml/connectionError";
import { confirmAction } from "../../state/confirmStore";
import { pushErrorToast, useToastStore } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import { DBML_DRIVERS, type DbmlConnection, type DbmlDriver } from "../../types/domain";
import type { TranslationKey } from "../../lib/i18n/translations";

/** The label each driver is listed under, and whether it is a file rather than a server. */
const DRIVERS: Record<DbmlDriver, { label: TranslationKey; defaultPort: number | null }> = {
  postgres: { label: "dbml.db.postgres", defaultPort: 5432 },
  sqlserver: { label: "dbml.db.sqlserver", defaultPort: 1433 },
  mysql: { label: "dbml.db.mysql", defaultPort: 3306 },
  sqlite: { label: "dbml.db.sqlite", defaultPort: null },
};

/** A connection being edited. Distinct from the saved shape by carrying a password. */
interface Draft {
  id: string | null;
  name: string;
  driver: DbmlDriver;
  host: string;
  port: string;
  database: string;
  username: string;
  filePath: string;
  useTls: boolean;
  password: string;
}

const EMPTY: Draft = {
  id: null,
  name: "",
  driver: "postgres",
  host: "localhost",
  port: "5432",
  database: "",
  username: "",
  filePath: "",
  useTls: false,
  password: "",
};

function draftOf(connection: DbmlConnection): Draft {
  return {
    id: connection.id,
    name: connection.name,
    driver: connection.driver,
    host: connection.host ?? "",
    port: connection.port === null ? "" : String(connection.port),
    database: connection.database ?? "",
    username: connection.username ?? "",
    filePath: connection.file_path ?? "",
    useTls: connection.use_tls,
    // Always blank: the sidecar cannot return it, and a placeholder that looks like a password
    // would invite somebody to "clear" it (DBML-024).
    password: "",
  };
}

const FIELD =
  "cf-focusable w-full rounded-control border border-[var(--cf-border)] bg-[var(--cf-bg)] px-2 py-1.5 text-body text-[var(--cf-text)] outline-none disabled:opacity-60";

/**
 * Picking, defining and testing a database connection (DBML-023).
 *
 * Its own component because it is a form with a lifecycle, and the import dialog around it already
 * has one. It owns the list and the draft; what it tells its parent is only **which connection is
 * ready to read from**, which is the single thing the import needs.
 *
 * The password field is write-only in both directions: never populated, and sent blank means "leave
 * what is stored", so editing a port does not require retyping the secret.
 */
export function DbConnectionPanel({
  onReady,
  disabled,
}: {
  /** The connection to read from, or null while none is chosen or saved. */
  onReady: (connectionId: string | null) => void;
  disabled: boolean;
}) {
  const t = useT();
  const pushToast = useToastStore((s) => s.pushToast);

  const [connections, setConnections] = useState<DbmlConnection[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    void (async () => {
      try {
        const saved = await dbmlListConnections();
        setConnections(saved);
        // Opening straight into the form when there is nothing saved saves a click that has only
        // one possible outcome.
        if (saved.length === 0) setDraft(EMPTY);
        else {
          setSelected(saved[0]!.id);
          onReady(saved[0]!.id);
        }
      } catch (e) {
        pushErrorToast(String(e));
      }
    })();
    // Once, on mount: `onReady` is a fresh closure on every render of the parent.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const choose = (id: string) => {
    setSelected(id);
    setDraft(null);
    onReady(id);
  };

  const edit = () => {
    const found = connections.find((c) => c.id === selected);
    if (found) setDraft(draftOf(found));
  };

  const save = async (): Promise<string | null> => {
    if (draft === null) return selected;
    setBusy(true);
    try {
      const port = draft.port.trim().length > 0 ? Number(draft.port) : null;
      const saved = await dbmlSaveConnection({
        id: draft.id,
        name: draft.name.trim().length > 0 ? draft.name.trim() : t("dbml.db.untitled"),
        driver: draft.driver,
        host: draft.host.trim() || null,
        port: port !== null && Number.isFinite(port) ? port : null,
        database: draft.database.trim() || null,
        username: draft.username.trim() || null,
        file_path: draft.filePath.trim() || null,
        use_tls: draft.useTls,
        password: draft.password.length > 0 ? draft.password : null,
      });

      setConnections(await dbmlListConnections());
      setSelected(saved.id);
      setDraft(null);
      onReady(saved.id);
      return saved.id;
    } catch (e) {
      pushErrorToast(String(e));
      return null;
    } finally {
      setBusy(false);
    }
  };

  const test = async () => {
    // Saved first: the sidecar reads the password out of the credential store by connection id, so
    // there is nothing to test until the connection exists.
    const id = await save();
    if (id === null) return;

    setBusy(true);
    try {
      await dbmlTestConnection(id);
      pushToast(t("dbml.db.testOk"), "success");
    } catch (e) {
      const refusal = connectionRefusal(e);
      pushErrorToast(refusal === null ? String(e) : t("dbml.db.testFailed", { error: refusal }));
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    const found = connections.find((c) => c.id === selected);
    if (!found) return;
    if (!(await confirmAction(t("dbml.db.deleteConfirm", { name: found.name }), true, t("dbml.db.delete")))) return;

    setBusy(true);
    try {
      await dbmlDeleteConnection(found.id);
      const left = await dbmlListConnections();
      setConnections(left);
      setSelected(left[0]?.id ?? null);
      onReady(left[0]?.id ?? null);
      if (left.length === 0) setDraft(EMPTY);
    } catch (e) {
      pushErrorToast(String(e));
    } finally {
      setBusy(false);
    }
  };

  const locked = disabled || busy;

  return (
    <div className="flex flex-col gap-3">
      {connections.length > 0 && (
        <div className="flex items-end gap-2">
          <label className="flex min-w-0 flex-1 flex-col gap-1.5">
            <span className="text-relaxed text-[var(--cf-text)]">{t("dbml.db.connection")}</span>
            <Select
              value={selected ?? ""}
              onChange={choose}
              ariaLabel={t("dbml.db.connection")}
              options={connections.map((c) => ({ value: c.id, label: `${c.name} · ${t(DRIVERS[c.driver].label)}` }))}
            />
          </label>
          <Button variant="secondary" disabled={locked || selected === null} onClick={edit}>
            {t("dbml.db.edit")}
          </Button>
          <IconButton
            label="dbml.db.delete"
            icon={Trash2}
            disabled={locked || selected === null}
            onClick={() => void remove()}
          />
          <IconButton label="dbml.db.add" icon={Plus} disabled={locked} onClick={() => setDraft(EMPTY)} />
        </div>
      )}

      {draft !== null && <DraftForm draft={draft} onChange={setDraft} disabled={locked} />}

      <div className="flex items-center justify-between gap-2">
        <span className="text-badge text-[var(--cf-text-muted)]">{t("dbml.db.passwordHint")}</span>
        <div className="flex gap-2">
          {draft !== null && (
            <Button variant="secondary" disabled={locked} onClick={() => void save()}>
              {t("dbml.db.save")}
            </Button>
          )}
          <Button variant="secondary" icon={Plug} pending={busy} disabled={locked} onClick={() => void test()}>
            {t("dbml.db.test")}
          </Button>
        </div>
      </div>
    </div>
  );
}

/** The fields, which differ for the one engine that is a file rather than a server. */
function DraftForm({
  draft,
  onChange,
  disabled,
}: {
  draft: Draft;
  onChange: (next: Draft) => void;
  disabled: boolean;
}) {
  const t = useT();
  const set = (patch: Partial<Draft>) => onChange({ ...draft, ...patch });

  const pick = async () => {
    const path = await apiPickFile(["db", "sqlite", "sqlite3", "db3"]);
    if (path !== null) set({ filePath: path, name: draft.name || (path.split(/[\\/]/).pop() ?? "") });
  };

  return (
    <div className="flex flex-col gap-3 rounded-md border border-[var(--cf-border)] p-3">
      <div className="flex gap-2">
        <label className="flex min-w-0 flex-1 flex-col gap-1.5">
          <span className="text-relaxed text-[var(--cf-text)]">{t("dbml.db.name")}</span>
          <input
            value={draft.name}
            onChange={(e) => set({ name: e.target.value })}
            disabled={disabled}
            placeholder={t("dbml.db.namePlaceholder")}
            className={FIELD}
          />
        </label>
        <label className="flex w-48 flex-col gap-1.5">
          <span className="text-relaxed text-[var(--cf-text)]">{t("dbml.db.engine")}</span>
          <Select
            value={draft.driver}
            onChange={(value) => {
              const driver = value as DbmlDriver;
              const port = DRIVERS[driver].defaultPort;
              set({ driver, port: port === null ? "" : String(port) });
            }}
            ariaLabel={t("dbml.db.engine")}
            options={DBML_DRIVERS.map((driver) => ({ value: driver, label: t(DRIVERS[driver].label) }))}
          />
        </label>
      </div>

      {draft.driver === "sqlite" ? (
        <div className="flex items-end gap-2">
          <label className="flex min-w-0 flex-1 flex-col gap-1.5">
            <span className="text-relaxed text-[var(--cf-text)]">{t("dbml.db.file")}</span>
            <input
              value={draft.filePath}
              onChange={(e) => set({ filePath: e.target.value })}
              disabled={disabled}
              placeholder="/ruta/a/base.db"
              className={`${FIELD} font-mono`}
            />
          </label>
          <Button variant="secondary" disabled={disabled} onClick={() => void pick()}>
            {t("dbml.import.pickFile")}
          </Button>
        </div>
      ) : (
        <>
          <div className="flex gap-2">
            <label className="flex min-w-0 flex-1 flex-col gap-1.5">
              <span className="text-relaxed text-[var(--cf-text)]">{t("dbml.db.host")}</span>
              <input
                value={draft.host}
                onChange={(e) => set({ host: e.target.value })}
                disabled={disabled}
                className={FIELD}
              />
            </label>
            <label className="flex w-28 flex-col gap-1.5">
              <span className="text-relaxed text-[var(--cf-text)]">{t("dbml.db.port")}</span>
              <input
                value={draft.port}
                onChange={(e) => set({ port: e.target.value.replace(/\D/g, "") })}
                disabled={disabled}
                inputMode="numeric"
                className={FIELD}
              />
            </label>
          </div>

          <div className="flex gap-2">
            <label className="flex min-w-0 flex-1 flex-col gap-1.5">
              <span className="text-relaxed text-[var(--cf-text)]">{t("dbml.db.database")}</span>
              <input
                value={draft.database}
                onChange={(e) => set({ database: e.target.value })}
                disabled={disabled}
                className={FIELD}
              />
            </label>
            <label className="flex min-w-0 flex-1 flex-col gap-1.5">
              <span className="text-relaxed text-[var(--cf-text)]">{t("dbml.db.username")}</span>
              <input
                value={draft.username}
                onChange={(e) => set({ username: e.target.value })}
                disabled={disabled}
                autoComplete="off"
                className={FIELD}
              />
            </label>
          </div>

          <div className="flex items-end gap-2">
            <label className="flex min-w-0 flex-1 flex-col gap-1.5">
              <span className="text-relaxed text-[var(--cf-text)]">{t("dbml.db.password")}</span>
              <input
                type="password"
                value={draft.password}
                onChange={(e) => set({ password: e.target.value })}
                disabled={disabled}
                autoComplete="new-password"
                placeholder={draft.id === null ? "" : t("dbml.db.passwordKept")}
                className={FIELD}
              />
            </label>
            {/* The checkbox carries no label of its own; wrapping it is what associates the two. */}
            <label className="flex shrink-0 cursor-pointer items-center gap-2 pb-2">
              <Checkbox
                checked={draft.useTls}
                onChange={(checked) => set({ useTls: checked })}
                disabled={disabled}
              />
              <span className="text-body text-[var(--cf-text)]">{t("dbml.db.tls")}</span>
            </label>
          </div>
        </>
      )}
    </div>
  );
}
