import { beforeEach, describe, expect, test, vi } from "vitest";
import type { ApiResponse, ApiVariable, ResolvedRequest } from "../../types/api";
import type { SandboxScopes } from "./sandbox";

/**
 * The `pm.*` runtime the user's own pre-request and post-response scripts run against.
 *
 * Three things here are contracts, and none of them fails loudly:
 *
 * 1. **Failure is contained.** A script that throws, asserts wrongly, or dies inside a
 *    `pm.sendRequest` callback still has to produce a `ScriptOutcome` with everything collected so
 *    far intact. The module states it plainly: a broken test script must never be the reason a
 *    response can't be displayed. Lose that and a typo in an assertion hides the server's answer,
 *    which reads as "the API is down".
 * 2. **The surface is absent, never stubbed.** Where a Postman API isn't implemented, calling it
 *    throws `pm.foo is not a function` rather than quietly returning `undefined` — a script that
 *    silently does nothing is worse than one that stops.
 * 3. **Variable precedence decides what goes on the wire.** `local → data → environment →
 *    collection → global`. Get that order wrong and the request is sent against the wrong
 *    environment, successfully.
 *
 * This is deliberately *not* a security sandbox and these tests do not treat it as one: the scripts
 * run in the webview's realm via `new Function`, at the trust level of the terminal this app
 * already embeds. See the module header.
 */

vi.mock("../ipc/apiCommands", () => ({ apiSendHttp: vi.fn() }));

const { apiSendHttp } = vi.mocked(await import("../ipc/apiCommands"));
const { runPreRequestScript, runPostResponseScript } = await import("./sandbox");

const variable = (key: string, currentValue: string): ApiVariable => ({
  id: key,
  key,
  initialValue: "",
  currentValue,
  secret: false,
  enabled: true,
  description: "",
});

/** The same key in three scopes, so precedence has something to choose between. */
function scopes(local: Record<string, string> = {}): SandboxScopes {
  return {
    local,
    data: {},
    environment: [variable("who", "env"), variable("onlyEnv", "e")],
    collection: [variable("who", "col"), variable("onlyCol", "c")],
    global: [variable("who", "glob")],
  };
}

function request(): ResolvedRequest {
  return {
    protocol: "http",
    method: "GET",
    url: "https://x.test/a?b=1",
    headers: [["A", "1"]],
    body: { kind: "text", text: '{"a":1}', contentType: "application/json" },
    backendAuth: null,
    options: {} as ResolvedRequest["options"],
  };
}

function response(): ApiResponse {
  return {
    status: 201,
    status_text: "Created",
    http_version: "1.1",
    headers: [
      ["Content-Type", "application/json"],
      ["X-Trace", "t1"],
    ],
    body_text: '{"id":42,"tags":["a"]}',
    body_base64: null,
    size_bytes: 22,
    duration_ms: 12,
    timings: {} as ApiResponse["timings"],
    redirects: [],
    set_cookies: [],
    sent: {} as ApiResponse["sent"],
    tests: [],
    consoleLines: [],
    visualizer: null,
    error: null,
  };
}

const after = (code: string, scope: SandboxScopes = scopes()) =>
  runPostResponseScript(code, { request: request(), response: response(), scopes: scope });

const logs = (outcome: { console: { text: string }[] }) => outcome.console.map((line) => line.text);

beforeEach(() => {
  vi.resetAllMocks();
});

describe("a script that breaks does not take the response with it", () => {
  test("what ran before the throw is kept, and the error is reported beside it", async () => {
    const outcome = await after(`
      pm.test("first", function () { pm.expect(1).to.equal(1); });
      throw new Error("boom");
    `);

    expect(outcome.error).toContain("Error: boom");
    // The point of the guarantee: the passing assertion above survives the throw below it.
    expect(outcome.tests).toMatchObject([{ name: "first", passed: true }]);
  });

  // `new Function` throws while compiling, before a single line runs.
  test("a script that does not even parse is reported, not thrown", async () => {
    const outcome = await after("this is not javascript");

    expect(outcome.error).toContain("SyntaxError");
    expect(outcome.tests).toEqual([]);
  });

  // The failure mode the header names explicitly.
  test("a throw inside a sendRequest callback is logged and the script carries on", async () => {
    apiSendHttp.mockResolvedValueOnce({ status: 200, headers: [], body_text: "{}" } as never);

    const outcome = await after(`
      pm.sendRequest("https://y.test", function () { throw new Error("in callback"); });
      pm.test("still runs", function () { pm.expect(1).to.equal(1); });
    `);

    expect(outcome.error).toBe(null);
    expect(logs(outcome)[0]).toContain("pm.sendRequest callback threw");
    expect(outcome.tests).toMatchObject([{ name: "still runs", passed: true }]);
  });

  // An assertion that fails is a result, not a defect in the script — the two must not be
  // conflated, or a red test would look like a broken runtime.
  test("a failing assertion is a test result, not a script error", async () => {
    const outcome = await after(`pm.test("wrong", function () { pm.expect(2).to.equal(3); });`);

    expect(outcome.error).toBe(null);
    expect(outcome.tests[0]).toMatchObject({ name: "wrong", passed: false });
    expect(outcome.tests[0]?.error).toContain("3");
  });

  test("the failure message names what was expected and what arrived", async () => {
    const outcome = await after(`pm.test("s", function () { pm.response.to.have.status(404); });`);

    expect(outcome.tests[0]?.error).toBe("expected response to have status code 404 but got 201");
  });
});

describe("an API that is not implemented is absent, not a stub", () => {
  // A stub returning undefined would let the script run to completion having done nothing, which
  // is the failure nobody can debug.
  test("calling something that does not exist stops the script", async () => {
    const outcome = await after("pm.doesNotExist();");

    expect(outcome.error).toContain("pm.doesNotExist is not a function");
  });

  test("the pm surface is exactly what is implemented", async () => {
    const outcome = await after(`console.log(Object.keys(pm).sort().join(","));`);

    expect(logs(outcome)[0]).toBe(
      "collectionVariables,environment,execution,expect,globals,iterationData," +
        "request,response,sendRequest,test,variables,visualizer",
    );
  });

  // The lodash shim is a subset on purpose; a script reaching past it fails at the call rather
  // than at the assertion three lines later.
  test("the lodash shim is a named subset", async () => {
    const outcome = await after(`console.log(Object.keys(_).sort().join(","));`);

    expect(logs(outcome)[0]).toBe("cloneDeep,filter,find,get,isEqual,keys,map,merge,omit,pick,set,values");
  });

  test("something outside the shim throws instead of doing nothing", async () => {
    const outcome = await after("_.isEmpty([]);");

    expect(outcome.error).toContain("_.isEmpty is not a function");
  });

  // Async because the digests come from WebCrypto rather than a bundled 200 KB library — the one
  // deviation from Postman the module documents.
  test("CryptoJS is real, and asynchronous", async () => {
    const outcome = await after(`console.log((await CryptoJS.MD5("abc")).toString());`);

    expect(logs(outcome)[0]).toBe("900150983cd24fb0d6963f7d28e17f72");
  });
});

describe("which value a variable resolves to", () => {
  test("environment beats collection beats global", async () => {
    const outcome = await after(`
      console.log(pm.variables.get("who"), pm.environment.get("who"),
                  pm.collectionVariables.get("who"), pm.globals.get("who"));
    `);

    expect(logs(outcome)[0]).toBe("env env col glob");
  });

  test("a local value beats all of them", async () => {
    const outcome = await after(`console.log(pm.variables.get("who"));`, scopes({ who: "loc" }));

    expect(logs(outcome)[0]).toBe("loc");
  });

  test("a key only one scope defines still resolves", async () => {
    const outcome = await after(`console.log(pm.variables.get("onlyCol"));`);

    expect(logs(outcome)[0]).toBe("c");
  });

  // An unresolved reference stays as it was written rather than becoming "undefined" — the same
  // rule the importers hold to, and for the same reason: a literal is debuggable, a silent
  // substitution is not.
  test("replaceIn leaves a reference it cannot resolve alone", async () => {
    const outcome = await after(`console.log(pm.variables.replaceIn("{{who}}/{{onlyCol}}/{{nope}}"));`);

    expect(logs(outcome)[0]).toBe("env/c/{{nope}}");
  });
});

describe("what a script writes back", () => {
  test("setting an existing environment variable updates it in place", async () => {
    const outcome = await after(`pm.environment.set("who", "changed");`);

    expect(outcome.scopes.environment.map((v) => [v.key, v.currentValue])).toEqual([
      ["who", "changed"],
      ["onlyEnv", "e"],
    ]);
  });

  test("setting an unknown key appends it", async () => {
    const outcome = await after(`pm.environment.set("token", "t1");`);

    expect(outcome.scopes.environment.map((v) => v.key)).toContain("token");
    expect(outcome.scopes.environment.find((v) => v.key === "token")?.currentValue).toBe("t1");
  });

  test("unset removes it", async () => {
    const outcome = await after(`pm.environment.unset("onlyEnv"); console.log(pm.environment.has("onlyEnv"));`);

    expect(logs(outcome)[0]).toBe("false");
    expect(outcome.scopes.environment.map((v) => v.key)).toEqual(["who"]);
  });

  // `pm.variables.set` is the one that surprises people: it writes to the local scope only, because
  // that is the scope it owns. Removing a variable from an environment is `pm.environment.unset`.
  test("pm.variables.set writes to the local scope, not to the environment", async () => {
    const outcome = await after(`pm.variables.set("tmp", "x");`);

    expect(outcome.scopes.local).toEqual({ tmp: "x" });
    expect(outcome.scopes.environment.map((v) => v.key)).toEqual(["who", "onlyEnv"]);
  });

  test("the runner is told where to jump next", async () => {
    expect((await after(`postman.setNextRequest("Second");`)).nextRequest).toBe("Second");
    expect((await after(`pm.execution.setNextRequest("Third");`)).nextRequest).toBe("Third");
  });

  test("a visualizer template is carried out with its data", async () => {
    const outcome = await after(`pm.visualizer.set("<b>{{x}}</b>", { x: 1 });`);

    expect(outcome.visualizer).toEqual({ template: "<b>{{x}}</b>", data: { x: 1 } });
  });

  test("console output keeps its level and renders objects", async () => {
    const outcome = await after(`console.log("hello", 1, { a: 2 }); console.warn("careful");`);

    expect(outcome.console.map((line) => line.level)).toEqual(["log", "warn"]);
    expect(outcome.console[0]?.text).toContain('"a": 2');
  });
});

describe("what a post-response script can ask about the response", () => {
  test("the response facade exposes what Postman scripts reach for", async () => {
    const outcome = await after(`
      console.log(pm.response.code, pm.response.status, pm.response.responseTime,
                  pm.response.headers.get("X-Trace"), pm.response.text().length);
    `);

    expect(logs(outcome)[0]).toBe("201 Created 12 t1 22");
  });

  test("json() parses the body", async () => {
    const outcome = await after(`pm.test("id", function () { pm.expect(pm.response.json().id).to.equal(42); });`);

    expect(outcome.tests[0]?.passed).toBe(true);
  });

  test("the assertion helpers a snippet would use all hold", async () => {
    const outcome = await after(`
      pm.test("ok", function () { pm.response.to.be.ok; });
      pm.test("header", function () { pm.response.to.have.header("X-Trace"); });
      pm.test("json body", function () { pm.response.to.have.jsonBody(); });
      pm.test("one of", function () { pm.expect(pm.response.code).to.be.oneOf([200, 201, 204]); });
      pm.test("chai shapes", function () {
        pm.expect([1]).to.be.an("array");
        pm.expect(1).to.be.above(0);
        pm.expect(2).to.be.below(3);
        pm.expect(true).to.be.true;
        pm.expect({ a: 1 }).to.eql({ a: 1 });
      });
    `);

    expect(outcome.tests.every((t) => t.passed)).toBe(true);
    expect(outcome.tests).toHaveLength(5);
  });

  test("a schema mismatch says which property was missing", async () => {
    const outcome = await after(`
      pm.test("schema", function () {
        pm.response.to.have.jsonSchema({ type: "object", required: ["nope"] });
      });
    `);

    expect(outcome.tests[0]?.passed).toBe(false);
    expect(outcome.tests[0]?.error).toContain("missing required property 'nope'");
  });

  test("an await in the middle does not lose the assertions after it", async () => {
    const outcome = await after(`
      await new Promise((r) => setTimeout(r, 1));
      pm.test("late", function () { pm.expect(true).to.be.true; });
    `);

    expect(outcome.tests).toMatchObject([{ name: "late", passed: true }]);
  });
});

describe("a pre-request script preparing the request", () => {
  // Applied in place, because what goes on the wire is the object the caller already holds.
  test("upserting a header adds a new one and replaces an existing one", async () => {
    const req = request();

    await runPreRequestScript(
      `pm.request.headers.upsert("X-T", "1"); pm.request.headers.upsert("A", "2");`,
      { request: req, scopes: scopes() },
    );

    expect(req.headers).toEqual([
      ["A", "2"],
      ["X-T", "1"],
    ]);
  });

  test("adding a query parameter rewrites the URL the request will use", async () => {
    const req = request();

    await runPreRequestScript(`pm.request.url.query.upsert("k", "v");`, { request: req, scopes: scopes() });

    expect(req.url).toBe("https://x.test/a?b=1&k=v");
  });

  // The whole reason the API exists: fetch a token, then put it on the request going out. This is
  // the app's own "Pre-request: fetch a token first" snippet in miniature.
  test("a token fetched mid-script can be stored and used on the outgoing request", async () => {
    apiSendHttp.mockResolvedValueOnce({
      status: 200,
      headers: [["Content-Type", "application/json"]],
      body_text: '{"access_token":"t-from-send"}',
    } as never);
    const req = request();

    const outcome = await runPreRequestScript(
      `
      const res = await pm.sendRequest({ url: "https://auth.test/token", method: "POST" });
      pm.environment.set("authToken", res.json().access_token);
      pm.request.headers.upsert("Authorization", "Bearer " + pm.environment.get("authToken"));
    `,
      { request: req, scopes: scopes() },
    );

    expect(outcome.error).toBe(null);
    expect(req.headers).toContainEqual(["Authorization", "Bearer t-from-send"]);
    expect(outcome.scopes.environment.find((v) => v.key === "authToken")?.currentValue).toBe("t-from-send");
  });

  // Sending goes through the sidecar, not the webview's own fetch, so proxy/CA/TLS settings apply.
  test("sendRequest goes through the transport rather than the webview", async () => {
    apiSendHttp.mockResolvedValueOnce({ status: 200, headers: [], body_text: "{}" } as never);

    await after(`await pm.sendRequest({ url: "https://y.test", method: "POST" });`);

    expect(apiSendHttp).toHaveBeenCalledOnce();
  });
});
