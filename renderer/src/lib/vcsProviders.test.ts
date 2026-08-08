import { describe, expect, it } from "vitest";
import { providersWith, VCS_PROVIDERS } from "./vcsProviders";

describe("providersWith", () => {
  it("finds the forges by a capability they all share", () => {
    expect(providersWith("repos").map((p) => p.id)).toEqual(["azure", "github", "gitlab"]);
  });

  // The case the capability list exists for: an integration that tracks work and hosts no code.
  it("includes Jira for work items and excludes it everywhere else", () => {
    expect(providersWith("workItems").map((p) => p.id)).toContain("jira");
    expect(providersWith("pullRequests").map((p) => p.id)).not.toContain("jira");
  });

  it("keeps registry order", () => {
    const ids = VCS_PROVIDERS.map((p) => p.id);
    const found = providersWith("workItems").map((p) => p.id);
    expect(found).toEqual(ids.filter((id) => found.includes(id)));
  });

  // Not filtered by `available`: the settings list shows what is planned next to what works.
  it("lists providers that are not wired up yet", () => {
    expect(providersWith("repos").some((p) => !p.available)).toBe(true);
  });
});
