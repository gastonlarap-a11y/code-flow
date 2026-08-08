/**
 * Pins the scope/auth walks that `state/apiStore.ts` (and later the split stores) delegate to.
 *
 * The contracts, in the order the panels depend on them:
 * 1. `buildVariableContext` picks the active non-global environment, the Globals row and the
 *    request's collection — and a corrupt variables blob degrades to `[]`, never to a throw.
 * 2. `ancestorAuth` walks folder → parent → … → collection, most specific first, and a cycle in
 *    `parent_id` terminates instead of hanging the renderer.
 * 3. `effectiveAuthChain` / `authChainForTab` put the request's (or draft's) own auth first, and
 *    an unknown id yields `[]` — the panel shows "no auth", it doesn't crash.
 */

import { describe, expect, test } from "vitest";
import {
  ancestorAuth,
  authChainForTab,
  buildVariableContext,
  effectiveAuthChain,
  parseSpec,
  parseVariables,
} from "./authChain";
import { defaultRequestSpec } from "../../types/api";
import type { ApiCollection, ApiEnvironment, ApiFolder, ApiRequestRow, ApiVariable } from "../../types/api";
import type { ApiTab } from "../../state/apiTabsStore";

function variable(key: string, value: string): ApiVariable {
  return { id: key, key, initialValue: "", currentValue: value, secret: false, enabled: true, description: "" };
}

function environment(overrides: Partial<ApiEnvironment>): ApiEnvironment {
  return {
    id: "env-1",
    workspace_id: "ws",
    name: "Env",
    variables: "[]",
    is_global: false,
    sort_order: 0,
    created_at: "",
    ...overrides,
  };
}

function collection(overrides: Partial<ApiCollection>): ApiCollection {
  return {
    id: "col-1",
    workspace_id: "ws",
    name: "Col",
    description: "",
    auth: "",
    pre_script: "",
    post_script: "",
    variables: "[]",
    sort_order: 0,
    created_at: "",
    updated_at: "",
    ...overrides,
  };
}

function folder(overrides: Partial<ApiFolder>): ApiFolder {
  return {
    id: "f-1",
    collection_id: "col-1",
    parent_id: null,
    name: "Folder",
    description: "",
    auth: "",
    pre_script: "",
    post_script: "",
    sort_order: 0,
    created_at: "",
    ...overrides,
  };
}

function requestRow(overrides: Partial<ApiRequestRow>): ApiRequestRow {
  return {
    id: "r-1",
    collection_id: "col-1",
    folder_id: null,
    name: "Req",
    protocol: "http",
    method: "GET",
    url: "https://x.test",
    spec: "",
    sort_order: 0,
    created_at: "",
    updated_at: "",
    ...overrides,
  };
}

const bearer = (token: string) => JSON.stringify({ type: "bearer", bearer: { token } });

describe("buildVariableContext", () => {
  test("assembles environment, collection and globals for the given collection", () => {
    const envs = [
      environment({ id: "env-1", variables: JSON.stringify([variable("host", "dev")]) }),
      environment({ id: "globals", is_global: true, variables: JSON.stringify([variable("org", "acme")]) }),
    ];
    const cols = [collection({ id: "col-1", variables: JSON.stringify([variable("path", "/v1")]) })];
    const ctx = buildVariableContext(envs, cols, "env-1", "col-1");
    expect(ctx.environment.map((v) => v.key)).toEqual(["host"]);
    expect(ctx.collection.map((v) => v.key)).toEqual(["path"]);
    expect(ctx.global.map((v) => v.key)).toEqual(["org"]);
    expect(ctx.collectionId).toBe("col-1");
    expect(ctx.local).toEqual({});
    expect(ctx.data).toEqual({});
  });

  test("the Globals row can never double as the active environment", () => {
    const envs = [environment({ id: "globals", is_global: true, variables: JSON.stringify([variable("k", "v")]) })];
    const ctx = buildVariableContext(envs, [], "globals", null);
    expect(ctx.environment).toEqual([]);
    expect(ctx.global.map((v) => v.key)).toEqual(["k"]);
  });

  test("no active environment and no collection degrade to empty scopes, not a throw", () => {
    const ctx = buildVariableContext([], [], null, null);
    expect(ctx.environment).toEqual([]);
    expect(ctx.collection).toEqual([]);
    expect(ctx.global).toEqual([]);
  });

  test("a corrupt variables blob parses to [], never throws", () => {
    const ctx = buildVariableContext(
      [environment({ id: "env-1", variables: "{not json" })],
      [collection({ id: "col-1", variables: '"a string, not an array"' })],
      "env-1",
      "col-1",
    );
    expect(ctx.environment).toEqual([]);
    expect(ctx.collection).toEqual([]);
  });
});

describe("ancestorAuth walks most specific first", () => {
  test("folder, then its parents up to the root, then the collection", () => {
    const folders = [
      folder({ id: "leaf", parent_id: "mid", auth: bearer("leaf") }),
      folder({ id: "mid", parent_id: null, auth: bearer("mid") }),
    ];
    const cols = [collection({ id: "col-1", auth: bearer("col") })];
    const chain = ancestorAuth(folders, cols, "col-1", "leaf");
    expect(chain.map((a) => (a?.type === "bearer" ? a.bearer.token : null))).toEqual(["leaf", "mid", "col"]);
  });

  test("an empty auth blob contributes null, keeping the level visible to the resolver", () => {
    const chain = ancestorAuth([folder({ id: "f-1", auth: "" })], [collection({ id: "col-1", auth: "" })], "col-1", "f-1");
    expect(chain).toEqual([null, null]);
  });

  test("a parent_id cycle terminates instead of hanging", () => {
    const folders = [
      folder({ id: "a", parent_id: "b" }),
      folder({ id: "b", parent_id: "a" }),
    ];
    const chain = ancestorAuth(folders, [], null, "a");
    // One entry per distinct folder visited, plus the (missing) collection.
    expect(chain).toHaveLength(3);
  });
});

describe("effectiveAuthChain and authChainForTab", () => {
  test("the request's own auth comes first, from its spec", () => {
    const row = requestRow({
      id: "r-1",
      folder_id: "f-1",
      spec: JSON.stringify({ ...defaultRequestSpec("http"), auth: JSON.parse(bearer("own")) }),
    });
    const chain = effectiveAuthChain([row], [folder({ id: "f-1", auth: bearer("f") })], [collection({ id: "col-1" })], "r-1");
    expect(chain[0]?.type === "bearer" && chain[0].bearer.token).toBe("own");
    expect(chain).toHaveLength(3);
  });

  test("an unknown request id yields [], not a crash", () => {
    expect(effectiveAuthChain([], [], [], "nope")).toEqual([]);
  });

  test("a tab's chain is rooted in the unsaved draft, not the saved row", () => {
    const tab: ApiTab = {
      id: "tab-1",
      requestId: "r-1",
      draft: { ...defaultRequestSpec("http"), auth: JSON.parse(bearer("draft")) },
      name: "t",
      dirty: true,
      collectionId: "col-1",
      folderId: null,
    };
    const chain = authChainForTab([tab], [], [collection({ id: "col-1", auth: bearer("col") })], "tab-1");
    expect(chain.map((a) => (a?.type === "bearer" ? a.bearer.token : null))).toEqual(["draft", "col"]);
  });

  test("an unknown tab id yields []", () => {
    expect(authChainForTab([], [], [], "nope")).toEqual([]);
  });
});

describe("row-blob parsing survives corruption", () => {
  test("a corrupt spec opens as an empty request of its protocol", () => {
    const spec = parseSpec(requestRow({ protocol: "graphql", spec: "{broken" }));
    expect(spec.protocol).toBe("graphql");
    expect(spec).toEqual(defaultRequestSpec("graphql"));
  });

  test("parseVariables rejects a non-array JSON value", () => {
    expect(parseVariables('{"key":"v"}')).toEqual([]);
    expect(parseVariables(undefined)).toEqual([]);
  });
});
