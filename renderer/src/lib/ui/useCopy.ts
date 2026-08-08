import { useState } from "react";
import { pushErrorToast } from "../../state/toastStore";
import { useT } from "../../state/languageStore";
import { host } from "../bridge/host";
import { currentPlatform } from "../platform";
import { manualCopyChord } from "./copyHint";

/**
 * Puts text on the clipboard through the shell when there is one, and through the web API otherwise.
 *
 * **The shell first, deliberately.** `navigator.clipboard.writeText` is not a reliable floor: it
 * resolves against `clipboard-sanitized-write`, a permission this app already got wrong once —
 * every copy button in it was dead for months and reported success anyway (`BOOT-029`) — and it
 * rejects with `Document is not focused`, or fails outright while another process holds the Windows
 * clipboard or a remote session has no redirection. `clipboard.writeText` in the main process is a
 * direct OS call with none of those failure modes. The web API stays as the fallback for a plain
 * `vite dev` in a browser, where there is no bridge at all.
 *
 * **The checkmark waits for the write to actually land**, on either path. It used to be a discarded
 * promise followed by an unconditional `setCopied(true)`, which is how twelve call sites reported a
 * success that had not happened.
 */
export function useCopy(): [boolean, (text: string) => void] {
  const [copied, setCopied] = useState(false);
  const t = useT();

  const write = (text: string): Promise<void> =>
    host.available() ? host.clipboardWrite(text) : navigator.clipboard.writeText(text);

  const copy = (text: string) => {
    void write(text).then(
      () => {
        setCopied(true);
        setTimeout(() => setCopied(false), 1500);
      },
      (e: unknown) =>
        pushErrorToast(
          `${t("common.copyFailed", { key: manualCopyChord(currentPlatform()) })} — ${String(e)}`,
        ),
    );
  };

  return [copied, copy];
}
