import { useState } from "react";
import { Upload } from "lucide-react";
import { Modal } from "../common/Modal";
import { Button } from "../common/Button";
import { Select } from "../common/Select";
import { parseDbmlModel } from "../../lib/dbml/parse";
import { exportSql } from "../../lib/dbml/exporters/sql";
import { toPrisma } from "../../lib/dbml/exporters/prisma";
import { saveTextFile } from "../../lib/ipc/commands";
import { pushErrorToast, useToastStore } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";

/**
 * Exports the open document (DBML-014, DBML-015).
 *
 * The four targets are listed flat rather than as a format plus a provider: "Prisma" alone is not a
 * choice a person can act on, since the schema it writes differs by database, and two dependent
 * selects for four combinations is more dialog than the decision deserves.
 */
type Target = "postgres" | "mssql" | "prisma-postgresql" | "prisma-sqlserver";

const TARGETS: readonly { value: Target; label: TranslationKey; suffix: string }[] = [
  { value: "postgres", label: "dbml.export.postgres", suffix: "postgres.sql" },
  { value: "mssql", label: "dbml.export.mssql", suffix: "mssql.sql" },
  { value: "prisma-postgresql", label: "dbml.export.prismaPostgres", suffix: "postgresql.prisma" },
  { value: "prisma-sqlserver", label: "dbml.export.prismaSqlServer", suffix: "sqlserver.prisma" },
];

/** The document's name without its extension, so `orders.dbml` suggests `orders.postgres.sql`. */
function baseName(relPath: string): string {
  const file = relPath.split("/").pop() ?? relPath;
  return file.replace(/\.dbml$/i, "") || "schema";
}

function render(source: string, target: Target): string {
  if (target === "postgres" || target === "mssql") return exportSql(source, target);

  const parsed = parseDbmlModel(source);
  if (!parsed.ok) throw new Error(parsed.error);

  return toPrisma(parsed.model, target === "prisma-postgresql" ? "postgresql" : "sqlserver");
}

export function ExportDbmlModal({
  source,
  relPath,
  onClose,
}: {
  source: string;
  relPath: string;
  onClose: () => void;
}) {
  const t = useT();
  const pushToast = useToastStore((s) => s.pushToast);
  const [target, setTarget] = useState<Target>("postgres");
  const [saving, setSaving] = useState(false);

  const save = async () => {
    setSaving(true);
    try {
      const suffix = TARGETS.find((option) => option.value === target)?.suffix ?? "sql";
      // Rendered before the dialog opens: a document that cannot be exported should say so instead
      // of asking where to put a file it will never write.
      const contents = render(source, target);

      const path = await saveTextFile(`${baseName(relPath)}.${suffix}`, contents);
      if (path !== null) {
        pushToast(t("dbml.export.done", { path }), "success");
        onClose();
      }
    } catch (e) {
      pushErrorToast(t("dbml.export.failed", { error: String(e) }));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal
      title="dbml.export.title"
      subtitle={relPath}
      icon={Upload}
      size="sm"
      dismissible={!saving}
      onClose={onClose}
    >
      <div className="flex flex-col gap-4">
        <label className="flex flex-col gap-1.5">
          <span className="text-relaxed text-[var(--cf-text)]">{t("dbml.export.format")}</span>
          <Select
            value={target}
            onChange={(value) => setTarget(value as Target)}
            ariaLabel={t("dbml.export.format")}
            options={TARGETS.map((option) => ({ value: option.value, label: t(option.label) }))}
          />
        </label>

        <p className="text-badge text-[var(--cf-text-muted)]">{t("dbml.export.hint")}</p>

        <div className="flex justify-end gap-2">
          <Button variant="secondary" onClick={onClose} disabled={saving}>
            {t("dbml.cancel")}
          </Button>
          <Button variant="primary" icon={Upload} pending={saving} onClick={() => void save()}>
            {t("dbml.export.save")}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
