import { useState } from "react";
import { Upload } from "lucide-react";
import { Button } from "../common/Button";
import { Checkbox } from "../common/Checkbox";
import { Select } from "../common/Select";
import { ApiModal } from "./ApiModal";
import { LabeledField } from "./LabeledField";
import { useApiTreeStore } from "../../state/apiTreeStore";
import { pushErrorToast, useToastStore } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import {
  exportNativeCollection,
  exportOpenApi,
  exportPostmanCollection,
} from "../../lib/api/exporters";
import { apiSaveFile } from "../../lib/ipc/apiCommands";

type Format = "postman" | "codeflow" | "openapi";

/** Extension per format, so the save dialog opens on a name the target tool will recognise. */
const SUFFIX: Record<Format, string> = {
  postman: "postman_collection.json",
  codeflow: "codeflow-api.json",
  openapi: "openapi.json",
};

export function ExportModal({ collectionId, onClose }: { collectionId: string; onClose: () => void }) {
  const t = useT();
  const collection = useApiTreeStore((s) => s.collections.find((c) => c.id === collectionId) ?? null);
  const folders = useApiTreeStore((s) => s.folders);
  const requests = useApiTreeStore((s) => s.requests);
  const pushToast = useToastStore((s) => s.pushToast);

  const [format, setFormat] = useState<Format>("postman");
  const [includeSecrets, setIncludeSecrets] = useState(false);
  const [saving, setSaving] = useState(false);

  // OpenAPI describes an interface, not an environment — it has no place to put a secret value,
  // so the toggle is meaningless rather than merely ignored.
  const secretsApply = format !== "openapi";

  const save = async () => {
    if (!collection) return;
    setSaving(true);
    try {
      const opts = { includeSecrets: secretsApply && includeSecrets };
      const contents =
        format === "postman"
          ? exportPostmanCollection(collection, folders, requests, opts)
          : format === "codeflow"
            ? exportNativeCollection(collection, folders, requests, opts)
            : exportOpenApi(collection, folders, requests);

      const path = await apiSaveFile(`${collection.name || "collection"}.${SUFFIX[format]}`, contents);
      if (path) {
        pushToast(t("api.export.done", { path }), "success");
        onClose();
      }
    } catch (e) {
      pushErrorToast(t("api.toast.exportFailed", { error: String(e) }));
    } finally {
      setSaving(false);
    }
  };

  return (
    <ApiModal
      icon={Upload}
      title={t("api.export.collection")}
      subtitle={collection?.name}
      busy={saving}
      onClose={onClose}
      footer={
        <>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={saving}>
            {t("common.cancel")}
          </Button>
          <span className="ml-auto" />
          {/* `pending` is the spinner *and* the disabling — the hand-rolled version swapped the
              glyph and left the button pressable. */}
          <Button
            variant="primary"
            size="sm"
            icon={Upload}
            pending={saving}
            disabled={!collection}
            onClick={() => void save()}
          >
            {t("common.save")}
          </Button>
        </>
      }
    >
      <div className="min-h-0 flex-1 overflow-auto p-4">
        <LabeledField label={t("api.export.format")}>
          <Select
            value={format}
            onChange={(value) => setFormat(value as Format)}
            ariaLabel={t("api.export.format")}
            options={[
              { value: "postman", label: t("api.export.postman") },
              { value: "codeflow", label: t("api.export.codeflow") },
              { value: "openapi", label: t("api.export.openapi") },
            ]}
          />
        </LabeledField>

        <label
          className={`mt-4 flex items-start gap-2 ${secretsApply ? "cursor-pointer" : "opacity-50"}`}
        >
          <Checkbox
            checked={secretsApply && includeSecrets}
            disabled={!secretsApply}
            onChange={setIncludeSecrets}
            className="mt-[1px]"
          />
          <span className="min-w-0">
            <span className="block text-ui text-[var(--cf-text)]">
              {t("api.export.includeSecrets")}
            </span>
            <span className="block text-badge leading-snug text-[var(--cf-text-muted)]">
              {t("api.export.secretsWarning")}
            </span>
          </span>
        </label>
      </div>
    </ApiModal>
  );
}
