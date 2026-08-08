/**
 * Variable-scope and auth-chain assembly, as pure functions over the store's own rows.
 *
 * Nothing here subscribes to anything: every function takes the lists it walks as arguments.
 * Reactive callers already select those slices out of the stores and rebuild in a `useMemo`
 * keyed on them; imperative callers snapshot the stores through the `get…` helpers exported by
 * `state/apiStore.ts`. Keeping the walk itself store-free is what lets it be tested without a
 * single mock.
 */

import { defaultRequestSpec } from "../../types/api";
import type {
  ApiCollection,
  ApiEnvironment,
  ApiFolder,
  ApiRequestRow,
  ApiRequestSpec,
  ApiVariable,
  AuthConfig,
} from "../../types/api";
import type { VariableContext } from "./variables";
import type { ApiTab } from "../../state/apiTabsStore";

function parseJson<T>(raw: string | null, fallback: T): T {
  if (raw === null || raw === "") return fallback;
  try {
    return JSON.parse(raw) as T;
  } catch {
    return fallback;
  }
}

/** A row whose `spec` is corrupt still opens, as an empty request of its protocol. */
export function parseSpec(row: ApiRequestRow): ApiRequestSpec {
  const fallback = defaultRequestSpec(row.protocol);
  return { ...fallback, ...parseJson<Partial<ApiRequestSpec>>(row.spec, {}) };
}

export function parseAuth(json: string): AuthConfig | null {
  return parseJson<AuthConfig | null>(json, null);
}

export function parseVariables(json: string | undefined): ApiVariable[] {
  const parsed = parseJson<ApiVariable[]>(json ?? null, []);
  return Array.isArray(parsed) ? parsed : [];
}

export function buildVariableContext(
  environments: ApiEnvironment[],
  collections: ApiCollection[],
  activeEnvironmentId: string | null,
  collectionId: string | null,
): VariableContext {
  const environment = environments.find((e) => e.id === activeEnvironmentId && !e.is_global);
  const globals = environments.find((e) => e.is_global);
  const collection = collections.find((c) => c.id === collectionId);
  return {
    local: {},
    data: {},
    environment: parseVariables(environment?.variables),
    collection: parseVariables(collection?.variables),
    global: parseVariables(globals?.variables),
    collectionId,
  };
}

/** Request auth first, then each folder up to the root, then the collection. */
export function ancestorAuth(
  folders: ApiFolder[],
  collections: ApiCollection[],
  collectionId: string | null,
  folderId: string | null,
): (AuthConfig | null)[] {
  const chain: (AuthConfig | null)[] = [];
  const seen = new Set<string>();
  let current = folderId;
  while (current !== null && !seen.has(current)) {
    seen.add(current);
    const folder = folders.find((f) => f.id === current);
    if (!folder) break;
    chain.push(parseAuth(folder.auth));
    current = folder.parent_id;
  }
  const collection = collections.find((c) => c.id === collectionId);
  chain.push(collection ? parseAuth(collection.auth) : null);
  return chain;
}

export function effectiveAuthChain(
  requests: ApiRequestRow[],
  folders: ApiFolder[],
  collections: ApiCollection[],
  requestId: string,
): (AuthConfig | null)[] {
  const row = requests.find((r) => r.id === requestId);
  if (!row) return [];
  return [parseSpec(row).auth, ...ancestorAuth(folders, collections, row.collection_id, row.folder_id)];
}

/** Same walk as `effectiveAuthChain` but rooted in the tab's unsaved draft. */
export function authChainForTab(
  tabs: ApiTab[],
  folders: ApiFolder[],
  collections: ApiCollection[],
  tabId: string,
): (AuthConfig | null)[] {
  const tab = tabs.find((t) => t.id === tabId);
  if (!tab) return [];
  return [tab.draft.auth, ...ancestorAuth(folders, collections, tab.collectionId, tab.folderId)];
}
