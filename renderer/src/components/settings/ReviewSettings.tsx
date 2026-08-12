import { useState } from "react";
import { Database, MessageSquareText, ShieldCheck, SquarePen, Ticket, type LucideIcon } from "lucide-react";
import { WorkspacePromptEditor } from "./WorkspacePromptEditor";
import { ReviewContextEditor } from "./ReviewContextEditor";
import { ReviewMemoriesSettings } from "./ReviewMemoriesSettings";
import { Tabs, tabPanelProps } from "../common/Tabs";
import { useWorkspaceStore } from "../../state/workspaceStore";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";

type TabId = "standard" | "ticket" | "context" | "prDesc" | "memories";

const TABS: { id: TabId; labelKey: TranslationKey; icon: LucideIcon }[] = [
  { id: "standard", labelKey: "settings.reviewTabStandard", icon: ShieldCheck },
  { id: "ticket", labelKey: "settings.reviewTabTicket", icon: Ticket },
  { id: "context", labelKey: "settings.reviewTabContext", icon: MessageSquareText },
  { id: "prDesc", labelKey: "settings.reviewTabPrDesc", icon: SquarePen },
  { id: "memories", labelKey: "settings.reviewTabMemories", icon: Database },
];

/**
 * The single per-workspace "PR review" section — everything the analysis pipeline reads, gathered
 * behind sub-tabs instead of scattered across the settings menu: the review standard (methodology),
 * project review context, markdown instructions, the PR-description template, and the saved-review
 * memory manager. All of it is provider-independent, so it applies to whatever model each task runs.
 */
export function ReviewSettings() {
  const t = useT();
  const workspaceName = useWorkspaceStore((s) => {
    const id = s.activeWorkspaceId;
    return s.workspaces.find((w) => w.id === id)?.name ?? "";
  });
  const [tab, setTab] = useState<TabId>("standard");

  return (
    <section>
      <div className="mb-3">
        <h3 className="text-title font-semibold">
          {workspaceName ? t("settings.reviewTitleForProject", { name: workspaceName }) : t("settings.review")}
        </h3>
      </div>

      <Tabs
        options={TABS}
        activeId={tab}
        onSelect={setTab}
        layoutId="cf-review-tab"
        label={t("settings.review")}
        className="mb-4 flex-wrap border-b border-[var(--cf-border)]"
      />

      <div {...tabPanelProps("cf-review-tab", tab)}>
      {tab === "standard" && (
        <WorkspacePromptEditor
          kind="review_standard"
          hintKey="settings.reviewStandardHint"
          placeholderKey="settings.reviewStandardPlaceholder"
          resetConfirmKey="settings.reviewStandardResetConfirm"
        />
      )}
      {tab === "ticket" && (
        <WorkspacePromptEditor
          kind="ticket_review_standard"
          hintKey="settings.ticketStandardHint"
          placeholderKey="settings.ticketStandardPlaceholder"
          resetConfirmKey="settings.ticketStandardResetConfirm"
        />
      )}
      {tab === "context" && <ReviewContextEditor />}
      {tab === "prDesc" && (
        <WorkspacePromptEditor
          kind="pr_description"
          hintKey="settings.prDescHint"
          placeholderKey="settings.prDescPlaceholder"
          resetConfirmKey="settings.prDescResetConfirm"
          rows={12}
        />
      )}
      {tab === "memories" && <ReviewMemoriesSettings />}
      </div>
    </section>
  );
}
