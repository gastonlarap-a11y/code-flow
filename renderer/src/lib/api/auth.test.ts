import { beforeEach, describe, expect, test, vi } from "vitest";
import { defaultAuth } from "../../types/api";
import type { AuthConfig, OAuth2Auth } from "../../types/api";

/**
 * What ends up on the wire as credentials.
 *
 * Two things make this worth pinning. The first is that a mistake here does not throw: an auth
 * scheme that quietly contributes nothing produces a 401 the user reads as "my token is wrong",
 * and one that contributes the wrong header sends a credential to a host that was never meant to
 * see it. The second is `applyAuth`'s split — some schemes are applied in the webview, others are
 * handed to the sidecar as `backend` because they have to sign the real request. Which is which is
 * a contract with `HttpSend.cs`, and moving a scheme across that line silently stops it signing.
 */

// The transport, because `fetchOAuth2Token` deliberately does not use the webview's `fetch` — it
// would ignore the app's proxy, custom CA and TLS settings.
vi.mock("../ipc/apiCommands", () => ({ apiSendHttp: vi.fn() }));

const { apiSendHttp } = vi.mocked(await import("../ipc/apiCommands"));
const { resolveEffectiveAuth, applyAuth, isOAuth2TokenExpired, fetchOAuth2Token } = await import("./auth");

const req = { method: "GET", url: "https://example.com/", bodyText: "" };

/** `defaultAuth` fills every branch's fields, so a fixture only states what it is about. */
function auth<T extends AuthConfig["type"]>(type: T, fields: Partial<AuthConfig> = {}): AuthConfig {
  return { ...defaultAuth(type), ...fields };
}

const oauth2 = (fields: Partial<OAuth2Auth>): OAuth2Auth => ({ ...defaultAuth("oauth2").oauth2, ...fields });

beforeEach(() => {
  vi.resetAllMocks();
});

describe("resolving which auth applies", () => {
  // Request → folder(s) → collection.
  test("the first entry that is not inherit wins", () => {
    const chain = [auth("inherit"), auth("bearer"), auth("basic")];

    expect(resolveEffectiveAuth(chain).type).toBe("bearer");
  });

  test("a hole in the chain is skipped", () => {
    expect(resolveEffectiveAuth([null, null, auth("basic")]).type).toBe("basic");
  });

  // Falling off the end is `none`, not "keep looking" — an unconfigured chain must not inherit
  // from whatever the caller had lying around.
  test("a chain that configures nothing resolves to none", () => {
    expect(resolveEffectiveAuth([auth("inherit"), null]).type).toBe("none");
  });

  test("an empty chain resolves to none", () => {
    expect(resolveEffectiveAuth([]).type).toBe("none");
  });

  // `none` is a decision, so it stops the search the same way any other scheme does.
  test("an explicit none stops the search", () => {
    expect(resolveEffectiveAuth([auth("none"), auth("bearer")]).type).toBe("none");
  });
});

describe("schemes applied in the webview", () => {
  test("basic sends base64 of user:password", async () => {
    const result = await applyAuth(
      auth("basic", { basic: { username: "ada", password: "s3cret" } }),
      req,
    );

    expect(result.headers).toEqual([["Authorization", `Basic ${btoa("ada:s3cret")}`]]);
    expect(result.backend).toBe(null);
  });

  test("basic handles non-ASCII credentials", async () => {
    const result = await applyAuth(auth("basic", { basic: { username: "añó", password: "π" } }), req);

    // Base64 of the UTF-8 bytes, not of the code units — a `btoa` on the raw string throws here.
    expect(result.headers[0]?.[1]).toBe("Basic YcOxw7M6z4A=");
  });

  test("bearer sends the token with the fixed scheme name", async () => {
    const result = await applyAuth(auth("bearer", { bearer: { token: " abc " } }), req);

    expect(result.headers).toEqual([["Authorization", "Bearer abc"]]);
  });

  // An empty field is "not configured yet", not "send an empty credential".
  test("an empty bearer token contributes nothing", async () => {
    expect(await applyAuth(auth("bearer", { bearer: { token: "   " } }), req)).toEqual({
      headers: [],
      queryParams: [],
      backend: null,
    });
  });

  test("an api key goes in a header by default", async () => {
    const result = await applyAuth(
      auth("apikey", { apikey: { key: "X-Api-Key", value: "k1", addTo: "header" } }),
      req,
    );

    expect(result.headers).toEqual([["X-Api-Key", "k1"]]);
    expect(result.queryParams).toEqual([]);
  });

  test("an api key can go in the query string instead", async () => {
    const result = await applyAuth(
      auth("apikey", { apikey: { key: "api_key", value: "k1", addTo: "query" } }),
      req,
    );

    expect(result.queryParams).toEqual([["api_key", "k1"]]);
    expect(result.headers).toEqual([]);
  });

  test("an api key with no name contributes nothing", async () => {
    expect(
      (await applyAuth(auth("apikey", { apikey: { key: "", value: "k1", addTo: "header" } }), req)).headers,
    ).toEqual([]);
  });

  test("inherit and none contribute nothing", async () => {
    for (const type of ["inherit", "none"] as const) {
      expect(await applyAuth(auth(type), req)).toEqual({ headers: [], queryParams: [], backend: null });
    }
  });
});

describe("placing an OAuth 2 token", () => {
  test("uses the configured header prefix", async () => {
    const result = await applyAuth(
      auth("oauth2", { oauth2: oauth2({ accessToken: "t1", addTo: "header", headerPrefix: "Token" }) }),
      req,
    );

    expect(result.headers).toEqual([["Authorization", "Token t1"]]);
  });

  test("an empty prefix sends the bare token", async () => {
    const result = await applyAuth(
      auth("oauth2", { oauth2: oauth2({ accessToken: "t1", addTo: "header", headerPrefix: "  " }) }),
      req,
    );

    expect(result.headers).toEqual([["Authorization", "t1"]]);
  });

  // RFC 6750 §2.3 names the query form, and `OAuth2Auth` has no field to override it.
  test("the query form is always access_token", async () => {
    const result = await applyAuth(
      auth("oauth2", { oauth2: oauth2({ accessToken: "t1", addTo: "query" }) }),
      req,
    );

    expect(result.queryParams).toEqual([["access_token", "t1"]]);
  });

  // This runs on every keystroke that rebuilds the snippet preview, so it must never fetch.
  test("no token means nothing is sent, and no request is made", async () => {
    const result = await applyAuth(auth("oauth2", { oauth2: oauth2({ accessToken: "" }) }), req);

    expect(result).toEqual({ headers: [], queryParams: [], backend: null });
    expect(apiSendHttp).not.toHaveBeenCalled();
  });
});

describe("schemes handed to the sidecar to sign", () => {
  // These need the real method, URL and body, which only exist once the request is built — the
  // contract is that they contribute no header here and a `backend` descriptor instead.
  test("digest is deferred with its credentials", async () => {
    const result = await applyAuth(
      auth("digest", { digest: { username: "ada", password: "s3cret" } }),
      req,
    );

    expect(result.headers).toEqual([]);
    expect(result.backend).toEqual({ kind: "digest", username: "ada", password: "s3cret" });
  });

  test("aws sigv4 is deferred with the whole credential set", async () => {
    const result = await applyAuth(
      auth("awsv4", {
        awsv4: {
          accessKey: "AKIA",
          secretKey: "secret",
          sessionToken: "session",
          region: "us-east-1",
          service: "s3",
        },
      }),
      req,
    );

    expect(result.headers).toEqual([]);
    // snake_case: these names are read verbatim by `HttpSend.cs`.
    expect(result.backend).toEqual({
      kind: "awsv4",
      access_key: "AKIA",
      secret_key: "secret",
      session_token: "session",
      region: "us-east-1",
      service: "s3",
    });
  });
});

describe("deciding whether a stored token is dead", () => {
  const now = () => Math.floor(Date.now() / 1000);

  test("an expiry in the past is expired", () => {
    expect(isOAuth2TokenExpired(oauth2({ expiresAt: now() - 60 }))).toBe(true);
  });

  test("an expiry in the future is not", () => {
    expect(isOAuth2TokenExpired(oauth2({ expiresAt: now() + 600 }))).toBe(false);
  });

  // Zero means "the provider never said", which is not the same as "dead" — treating it as
  // expired would refetch a perfectly good token on every send.
  test("an unknown expiry is not expired", () => {
    expect(isOAuth2TokenExpired(oauth2({ expiresAt: 0 }))).toBe(false);
  });
});

describe("fetching a token", () => {
  const okResponse = (body: unknown) => ({
    status: 200,
    headers: [["content-type", "application/json"]],
    body_text: JSON.stringify(body),
  });

  test("goes through the sidecar transport, not the webview's fetch", async () => {
    apiSendHttp.mockResolvedValueOnce(okResponse({ access_token: "t1", expires_in: 3600 }) as never);

    const result = await fetchOAuth2Token(
      oauth2({ accessTokenUrl: "https://id.example.com/token", clientId: "c", clientSecret: "s" }),
    );

    expect(apiSendHttp).toHaveBeenCalledOnce();
    expect(result.accessToken).toBe("t1");
  });

  test("an expiry is turned into an absolute second", async () => {
    apiSendHttp.mockResolvedValueOnce(okResponse({ access_token: "t1", expires_in: 3600 }) as never);

    const result = await fetchOAuth2Token(oauth2({ accessTokenUrl: "https://id.example.com/token" }));

    expect(result.expiresAt).toBeGreaterThan(Math.floor(Date.now() / 1000));
  });

  // `expiresAt: 0` is the "unknown" the check above depends on.
  test("a response without expires_in reports an unknown expiry", async () => {
    apiSendHttp.mockResolvedValueOnce(okResponse({ access_token: "t1" }) as never);

    const result = await fetchOAuth2Token(oauth2({ accessTokenUrl: "https://id.example.com/token" }));

    expect(result.expiresAt).toBe(0);
  });

  test("a non-2xx answer is reported rather than parsed for a token", async () => {
    apiSendHttp.mockResolvedValueOnce({
      status: 401,
      headers: [],
      body_text: '{"error":"invalid_client"}',
    } as never);

    await expect(
      fetchOAuth2Token(oauth2({ accessTokenUrl: "https://id.example.com/token" })),
    ).rejects.toThrow();
  });

  test("a 200 that carries no token is an error, not an empty success", async () => {
    apiSendHttp.mockResolvedValueOnce(okResponse({ token_type: "Bearer" }) as never);

    await expect(
      fetchOAuth2Token(oauth2({ accessTokenUrl: "https://id.example.com/token" })),
    ).rejects.toThrow();
  });
});
