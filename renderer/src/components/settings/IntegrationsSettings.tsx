import { useEffect, useState } from "react";
import { AzureDevOpsSettings } from "./AzureDevOpsSettings";
import { GitHubSettings } from "./GitHubSettings";
import { GroupCard } from "./GroupCard";
import { Chip } from "../common/Chip";
import { VCS_PROVIDERS, type ProviderCapability, type VcsProviderOption } from "../../lib/vcsProviders";
import { loadAdoConnections } from "../../lib/adoConnections";
import { loadGithubConnections } from "../../lib/githubConnections";
import { useUiStore } from "../../state/uiStore";
import { useT } from "../../state/languageStore";
import type { TranslationKey } from "../../lib/i18n/translations";

const CAPABILITY_LABELS: Record<ProviderCapability, TranslationKey> = {
  repos: "settings.capRepos",
  pullRequests: "settings.capPullRequests",
  workItems: "settings.capWorkItems",
};

/** How many accounts are configured for a provider, or `null` while that is still being read.
 * Only the two wired-up providers can answer; the rest have nothing to connect to yet. */
type ConnectionCounts = Partial<Record<VcsProviderOption["id"], number>>;

function ProviderRow({
  provider,
  connections,
  defaultOpen,
}: {
  provider: VcsProviderOption;
  connections: number | undefined;
  defaultOpen: boolean;
}) {
  const t = useT();

  const status = !provider.available
    ? t("settings.comingSoon")
    : connections === undefined
      ? ""
      : connections === 0
        ? t("settings.notConnected")
        : t("settings.connectedCount", { n: connections });

  return (
    <GroupCard
      icon={provider.icon}
      title={provider.label}
      subtitle={status}
      // Unavailable providers still list what they will do, but there is nothing to expand into.
      collapsible={provider.available}
      defaultOpen={defaultOpen}
      // In the header, not the body: what a provider can do is what you read to decide whether to
      // open it, and inside the collapsible part it vanished on exactly the two providers you use.
      headerExtra={
        <div className="flex flex-wrap gap-1.5">
          {provider.capabilities.map((capability) => (
            <Chip key={capability} tone={provider.available ? "accent" : "neutral"}>
              {t(CAPABILITY_LABELS[capability])}
            </Chip>
          ))}
        </div>
      }
    >
      {provider.id === "github" ? <GitHubSettings /> : provider.id === "azure" ? <AzureDevOpsSettings /> : null}
    </GroupCard>
  );
}

/**
 * One place that answers "what is this app connected to".
 *
 * It replaces the "Git hosting" section, which was a tab strip over two credential forms and was
 * still filed under the section id `azure` — a name that stopped being true the moment GitHub was
 * added to it. Tabs were also the wrong shape for what comes next: they say "pick one of these",
 * and the honest question is "which of these have you set up", which is a list with a state on
 * every row.
 *
 * Each row expands into the provider's own form, unchanged. What is new is the row itself: the
 * capability chips come from the registry, so Jira listing only work items is a fact about the
 * registry rather than a special case here, and the connection count is read once for the whole
 * section instead of only being visible after expanding a form.
 */
export function IntegrationsSettings() {
  const t = useT();
  const initialProvider = useUiStore((s) => s.settingsHostingProvider);
  const [counts, setCounts] = useState<ConnectionCounts>({});

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      // Failing to read a provider's connections leaves its row without a count rather than
      // without a row: the form underneath still works, and it reports its own errors.
      const [azure, github] = await Promise.all([
        loadAdoConnections().catch(() => null),
        loadGithubConnections().catch(() => null),
      ]);
      if (cancelled) return;
      setCounts({
        ...(azure ? { azure: azure.length } : {}),
        ...(github ? { github: github.length } : {}),
      });
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  return (
    <section>
      <h3 className="mb-1 text-title font-semibold">{t("settings.integrationsTitle")}</h3>
      <p className="mb-3 text-relaxed text-[var(--cf-text-muted)]">{t("settings.integrationsHint")}</p>

      <div className="flex flex-col gap-3">
        {VCS_PROVIDERS.map((provider) => (
          <ProviderRow
            key={provider.id}
            provider={provider}
            connections={counts[provider.id]}
            // A "needs a GitHub token" hint deep-links here; the row it meant is the one open.
            defaultOpen={provider.id === initialProvider}
          />
        ))}
      </div>
    </section>
  );
}
