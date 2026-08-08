import { Cloud, GitFork, GitMerge, SquareKanban, type LucideIcon } from "lucide-react";

/**
 * What an integration can do for the app.
 *
 * Capabilities rather than a provider union, because the next providers do not all do the same
 * things: Jira has work items and no repositories, GitLab will have everything, and a UI that asks
 * "is this GitHub or Azure?" has to be rewritten for each one. Asking "does this do pull requests?"
 * does not.
 */
export type ProviderCapability = "repos" | "pullRequests" | "workItems";

export interface VcsProviderOption {
  id: "azure" | "github" | "gitlab" | "jira";
  label: string;
  icon: LucideIcon;
  capabilities: readonly ProviderCapability[];
  /** Whether it is wired up at all. Azure DevOps and GitHub are (auth, PR list/review/comment);
   * the other two are listed disabled with a "coming soon" badge so the shape of what is planned is
   * visible, which is the whole reason they are here before they work. */
  available: boolean;
}

export const VCS_PROVIDERS: VcsProviderOption[] = [
  { id: "azure", label: "Azure DevOps", icon: Cloud, capabilities: ["repos", "pullRequests", "workItems"], available: true },
  { id: "github", label: "GitHub", icon: GitFork, capabilities: ["repos", "pullRequests", "workItems"], available: true },
  { id: "gitlab", label: "GitLab", icon: GitMerge, capabilities: ["repos", "pullRequests", "workItems"], available: false },
  // The one that is not a forge, and the reason capabilities are a list: Jira hosts no code.
  { id: "jira", label: "Jira", icon: SquareKanban, capabilities: ["workItems"], available: false },
];

/**
 * The providers that can do something, in registry order.
 *
 * `available` is deliberately *not* part of the filter: the settings list shows what is planned as
 * well as what works, and a caller that wants only working providers says so.
 */
export function providersWith(capability: ProviderCapability): VcsProviderOption[] {
  return VCS_PROVIDERS.filter((provider) => provider.capabilities.includes(capability));
}
