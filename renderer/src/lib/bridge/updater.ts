import { host, invoke, listen } from "./host";

/**
 * The update surface `updateStore.ts` expects, shaped like the updater surface.
 *
 * This is the one bypass whose shape genuinely changes rather than its wiring, and the reason is
 * that neither half of 1.7.2's mechanism survives this codebase. the shell's plugin fetched a
 * minisign-signed `latest.json` from a public repository and replaced the binary itself; this
 * repository is private, so the manifest cannot be read anonymously, and the app is unsigned, so
 * there is no key to verify a replacement with.
 *
 * What is left is honest: the sidecar reads the release list with a credential the user already
 * has — the GitHub token in the OS keychain, or `gh auth token` — and hands over the artefact. No
 * token is embedded in the app and none is written next to it.
 */

export interface DownloadEvent {
  event: "Started" | "Progress" | "Finished";
  data: { contentLength?: number; chunkLength: number };
}

/** What `update_check` answers. `snake_case`, like every other shape the sidecar returns. */
interface Availability {
  available: boolean;
  current_version: string;
  version: string;
  notes: string;
  date: string;
  asset_name: string;
  asset_url: string;
  asset_size: number;
  install_kind: "auto" | "manual";
  reason: string;
}

interface Progress {
  downloaded: number;
  total: number;
  done: boolean;
}

/**
 * The fields the renderer actually reads, matching the plugin's names.
 *
 * `date` is the release timestamp the "what's new" modal shows; it is optional in the plugin and
 * stays optional here so the modal's own fallback runs rather than rendering "undefined".
 *
 * `installKind` is the one addition, and it exists because the two platforms genuinely differ:
 * Windows runs the installer and restarts into the new build, while macOS can only open the disk
 * image. Telling a Mac user to restart would be the same kind of untruth this file replaces.
 */
export interface Update {
  version: string;
  currentVersion: string;
  date?: string | undefined;
  body?: string | undefined;
  installKind: "auto" | "manual";
  downloadAndInstall(onProgress: (event: DownloadEvent) => void): Promise<void>;
}

/** Why a check could not answer, as a translation key suffix the panel maps to a sentence. */
export type UpdateUnavailableReason =
  | "no-credential"
  | "unauthorized"
  | "no-release"
  | "no-asset"
  | "unreachable";

/** Thrown when the check ran but could not reach an answer, so the UI can say which. */
export class UpdateCheckError extends Error {
  constructor(readonly reason: UpdateUnavailableReason) {
    super(reason);
    this.name = "UpdateCheckError";
  }
}

/** The running build's version, as the shell reported it to the sidecar at startup. */
export function getVersion(): Promise<string> {
  return invoke<string>("update_current_version");
}

/**
 * Checks for a newer release.
 *
 * Returns null when the app is current — which is the plugin's contract — and throws with a reason
 * when the check could not be made. Those are different answers: reporting "up to date" for a
 * request that never reached GitHub is exactly the failure this replaces.
 */
export async function check(): Promise<Update | null> {
  const found = await invoke<Availability>("update_check");

  if (!found.available) {
    if (found.reason) throw new UpdateCheckError(found.reason as UpdateUnavailableReason);
    return null;
  }

  return {
    version: found.version,
    currentVersion: found.current_version,
    date: found.date || undefined,
    body: found.notes || undefined,
    installKind: found.install_kind,
    downloadAndInstall: (onProgress) => download(found, onProgress),
  };
}

/**
 * Downloads the artefact, translating the sidecar's progress into the plugin's event shape.
 *
 * The sidecar reports how much has arrived in total; the store counts deltas. Converting here
 * rather than changing the store keeps `updateStore.ts` untouched, which is the point of this
 * whole file.
 */
async function download(
  found: Availability,
  onProgress: (event: DownloadEvent) => void,
): Promise<void> {
  let last = 0;
  let started = false;

  // Subscribed before the command is sent: the first progress event is published from inside
  // `update_download`, so a subscription set up afterwards would miss the start of the transfer.
  const unlisten = await listen<Progress>("update:progress", ({ payload }) => {
    if (!started) {
      started = true;
      onProgress({ event: "Started", data: { contentLength: payload.total, chunkLength: 0 } });
    }

    const chunkLength = Math.max(0, payload.downloaded - last);
    last = payload.downloaded;

    onProgress(
      payload.done
        ? { event: "Finished", data: { chunkLength } }
        : { event: "Progress", data: { chunkLength } },
    );
  });

  try {
    await invoke<unknown>("update_download", {
      assetUrl: found.asset_url,
      assetName: found.asset_name,
    });
  } finally {
    unlisten();
  }
}

/** Restarts the app after an update has been staged. */
export function relaunch(): Promise<void> {
  // Quitting is real even without the updater: it is the same path the tray's Quit item uses. On
  // Windows the NSIS installer is already waiting for this process to exit.
  return host.quit();
}
