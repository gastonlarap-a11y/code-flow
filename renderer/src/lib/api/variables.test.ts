import { describe, expect, test } from "vitest";
import type { ApiVariable } from "../../types/api";
import {
  emptyVariableContext,
  findUnresolved,
  lookupVariable,
  resolve,
  resolveKeyValues,
  type VariableContext,
} from "./variables";

/**
 * `{{variable}}` substitution, and which scope wins.
 *
 * The precedence order is the part worth guarding. Every request the API tester sends has its URL,
 * headers, body and auth run through here, so an inverted order does not fail — it sends a
 * perfectly well-formed request to the wrong host. "I ran it against staging" and "I ran it against
 * production" look identical afterwards.
 */

const variable = (key: string, value: string, initial = ""): ApiVariable => ({
  id: `${key}-${value}`,
  key,
  initialValue: initial,
  currentValue: value,
  enabled: true,
  secret: false,
  description: "",
});

/** Every scope defines `host`, each with a value naming itself. */
function allScopes(): VariableContext {
  return {
    ...emptyVariableContext(),
    local: { host: "local" },
    data: { host: "data" },
    environment: [variable("host", "environment")],
    collection: [variable("host", "collection")],
    global: [variable("host", "global")],
  };
}

describe("precedence", () => {
  test("local beats every other scope", () => {
    expect(lookupVariable("host", allScopes())).toEqual({ value: "local", scope: "local" });
  });

  test("the full order is local, data, environment, collection, global", () => {
    // Peeled one scope at a time, so this pins the whole chain rather than just its first link.
    const ctx = allScopes();
    const order = ["local", "data", "environment", "collection", "global"] as const;
    const seen: string[] = [];

    seen.push(lookupVariable("host", ctx)!.scope);
    ctx.local = {};
    seen.push(lookupVariable("host", ctx)!.scope);
    ctx.data = {};
    seen.push(lookupVariable("host", ctx)!.scope);
    ctx.environment = [];
    seen.push(lookupVariable("host", ctx)!.scope);
    ctx.collection = [];
    seen.push(lookupVariable("host", ctx)!.scope);

    expect(seen).toEqual([...order]);
  });

  test("a disabled variable does not define anything", () => {
    // Toggling a row off in the environment editor has to actually stop it winning, or the user
    // disables the production host and keeps hitting it.
    const ctx = {
      ...emptyVariableContext(),
      environment: [{ ...variable("host", "environment"), enabled: false }],
      global: [variable("host", "global")],
    };

    expect(lookupVariable("host", ctx)).toEqual({ value: "global", scope: "global" });
  });

  test("an empty current value falls back to the initial one rather than resolving to empty", () => {
    // "Not overridden" and "overridden to empty" are different things; collapsing them silently
    // sends a request with a blank host.
    const ctx = { ...emptyVariableContext(), environment: [variable("host", "", "https://staging")] };

    expect(lookupVariable("host", ctx)?.value).toBe("https://staging");
  });
});

describe("resolve", () => {
  test("an unknown variable is left literal, not blanked", () => {
    // The deliberate rule: a request that shows `{{token}}` in its URL is obviously unfinished,
    // while one that silently sent an empty string looks like it worked.
    expect(resolve("https://{{host}}/{{missing}}", allScopes())).toBe("https://local/{{missing}}");
  });

  test("a value that references another variable is expanded too", () => {
    const ctx = {
      ...emptyVariableContext(),
      environment: [variable("base", "https://{{host}}"), variable("host", "api.example.com")],
    };

    expect(resolve("{{base}}/v1", ctx)).toBe("https://api.example.com/v1");
  });

  test("two variables referencing each other stop instead of hanging", () => {
    // This runs on every keystroke in the URL bar. Without the depth cap it takes the UI thread
    // with it.
    const ctx = {
      ...emptyVariableContext(),
      environment: [variable("a", "{{b}}"), variable("b", "{{a}}")],
    };

    expect(resolve("{{a}}", ctx)).toContain("{{");
  });

  test("text with no token is returned untouched", () => {
    expect(resolve("https://example.com", allScopes())).toBe("https://example.com");
  });

  test("an empty token is not a variable", () => {
    expect(resolve("{{}}", allScopes())).toBe("{{}}");
  });

  test("a dynamic variable wins over a user variable of the same name", () => {
    const ctx = { ...emptyVariableContext(), environment: [variable("$guid", "not-a-guid")] };

    expect(resolve("{{$guid}}", ctx)).not.toBe("not-a-guid");
  });

  test("each occurrence of a dynamic variable is evaluated separately", () => {
    const [first, second] = resolve("{{$guid}} {{$guid}}", emptyVariableContext()).split(" ");

    expect(first).not.toBe(second);
  });
});

describe("resolveKeyValues", () => {
  test("resolves key and value, and does not skip disabled rows", () => {
    // Filtering is the caller's job — a row toggled back on must not need a second pass.
    const ctx = { ...emptyVariableContext(), environment: [variable("h", "X-Tenant"), variable("v", "acme")] };

    const rows = resolveKeyValues(
      [{ id: "row-1", key: "{{h}}", value: "{{v}}", enabled: false, description: "{{h}}" }],
      ctx,
    );

    expect(rows[0]).toMatchObject({ key: "X-Tenant", value: "acme", enabled: false });
    // Description is UI-only and deliberately left verbatim.
    expect(rows[0]?.description).toBe("{{h}}");
  });
});

describe("findUnresolved", () => {
  test("reports what is still missing, once each, in order", () => {
    expect(findUnresolved("{{a}}/{{b}}/{{a}}", emptyVariableContext())).toEqual(["a", "b"]);
  });

  test("a variable whose value references a missing one is reported", () => {
    // Runs the real resolution rather than a shallow scan, which is the only way to catch this.
    const ctx = { ...emptyVariableContext(), environment: [variable("base", "https://{{host}}")] };

    expect(findUnresolved("{{base}}/v1", ctx)).toEqual(["host"]);
  });

  test("nothing missing is an empty list", () => {
    expect(findUnresolved("https://{{host}}", allScopes())).toEqual([]);
  });
});
