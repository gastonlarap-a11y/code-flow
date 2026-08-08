import { host } from "./host";

/**
 * Native open and save dialogs, backed by Electron.
 *
 * Signatures match the plugin's exactly so the four callers change nothing but their import
 * line. That matters more than it looks: `ProvidersSection`, `ReviewMemoriesSettings` and
 * `SkillsSettings` all branch on `typeof result === "string"`, so a shim returning `undefined`
 * instead of `null`, or an array instead of a string, would fail silently rather than loudly.
 */

export interface OpenDialogOptions {
  title?: string;
  multiple?: boolean;
  directory?: boolean;
  defaultPath?: string;
  filters?: { name: string; extensions: string[] }[];
}

export interface SaveDialogOptions {
  title?: string;
  defaultPath?: string;
  filters?: { name: string; extensions: string[] }[];
}

/** Opens a file or directory picker. Resolves to `null` when the user cancels. */
export async function open(options: OpenDialogOptions = {}): Promise<string | string[] | null> {
  const dialog = host.dialog();
  const shared = { title: options.title, defaultPath: options.defaultPath, filters: options.filters };

  const selected = options.directory
    ? await dialog.openDirectory(shared)
    : await dialog.openFile(shared);

  if (selected === null) return null;

  // Every current caller passes `multiple: false` and checks for a string. The array form is
  // honoured anyway so the shim does not quietly narrow the plugin's contract.
  return options.multiple ? [selected] : selected;
}

/** Opens a save picker. Resolves to `null` when the user cancels. */
export function save(options: SaveDialogOptions = {}): Promise<string | null> {
  return host.dialog().save(options);
}
