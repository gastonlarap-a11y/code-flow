/**
 * Pins the contracts `codegen.ts` states in its own header:
 *
 * 1. Every generator is a pure formatter over `ResolvedRequest` — the value arrives fully
 *    resolved, so a literal `{{foo}}` in a header or URL is opaque text, never re-interpolated.
 * 2. `wire()` is the one normalizer all 34 targets share: the body's Content-Type folds into the
 *    headers (explicit header wins), multipart drops it so the client picks its own boundary, and
 *    the cookie jar folds into `Cookie` — because a snippet that omitted it would send a different
 *    request than Send does.
 * 3. A signature computed on the wire (Digest, AWS SigV4) cannot be reproduced: `backendAuth`
 *    becomes a translated comment in the target's own syntax, never a fabricated header.
 * 4. Escaping is per language family, deliberately not shared — each family's quoting rules are
 *    asserted against output captured from the real generators, not derived from the helpers.
 *
 * Expected strings were probed from the actual generators first and hard-coded here, so a change
 * in quoting behaviour fails loudly instead of being mirrored by a re-implemented helper.
 */

import { beforeEach, describe, expect, test } from "vitest";
import { SNIPPET_TARGETS, defaultSnippetOptions, generateSnippet } from "./codegen";
import { useLanguageStore } from "../../state/languageStore";
import type { NetworkOptions, ResolvedRequest, SnippetOptions } from "../../types/api";

function networkOptions(): NetworkOptions {
  return {
    timeout_ms: 30000,
    follow_redirects: true,
    max_redirects: 5,
    verify_ssl: true,
    keep_auth_on_redirect: false,
    proxy_url: "",
    client_cert_path: "",
    client_cert_password: "",
    ca_cert_path: "",
    cookies: [],
    max_response_bytes: 0,
  };
}

function req(overrides: Partial<ResolvedRequest> = {}): ResolvedRequest {
  return {
    protocol: "http",
    method: "GET",
    url: "https://api.example.com/users",
    headers: [],
    body: { kind: "none" },
    backendAuth: null,
    options: networkOptions(),
    ...overrides,
  };
}

const OPTS: SnippetOptions = { multiline: true, indentWith: "  ", includeBoilerplate: true };

beforeEach(() => {
  // `translate()` reads this store imperatively (default "en"). Pinning it keeps the
  // backendAuth-note tests below independent of execution order — the same bug family the
  // repoStore suite already paid for once.
  useLanguageStore.setState({ language: "en" });
});

describe("the registry", () => {
  test("exposes 34 targets with unique ids", () => {
    expect(SNIPPET_TARGETS).toHaveLength(34);
    expect(new Set(SNIPPET_TARGETS.map((t) => t.id)).size).toBe(34);
  });

  test("every target renders a plain GET to non-empty code", () => {
    const empty = SNIPPET_TARGETS.filter((t) => generateSnippet(t.id, req(), OPTS) === "").map((t) => t.id);
    expect(empty).toEqual([]);
  });

  test("defaultSnippetOptions is multiline with two-space indent and boilerplate", () => {
    expect(defaultSnippetOptions()).toEqual({ multiline: true, indentWith: "  ", includeBoilerplate: true });
  });
});

describe("generateSnippet renders nothing dishonest", () => {
  test("an unknown target id yields the empty string", () => {
    expect(generateSnippet("cobol-cics", req(), OPTS)).toBe("");
  });

  test.each(["websocket", "socketio", "grpc", "mqtt"] as const)(
    "the %s protocol yields the empty string — no HTTP client in the list speaks it",
    (protocol) => {
      expect(generateSnippet("shell-curl", req({ protocol }), OPTS)).toBe("");
    },
  );

  test("graphql renders like http — it travels over the same transport", () => {
    expect(generateSnippet("shell-curl", req({ protocol: "graphql" }), OPTS).length).toBeGreaterThan(0);
  });
});

describe("wire(): the header set every generator prints", () => {
  test("an explicit Content-Type header beats the body's own content type", () => {
    const out = generateSnippet(
      "shell-curl",
      req({
        method: "POST",
        headers: [["Content-Type", "application/vnd.custom+json"]],
        body: { kind: "text", text: "{}", contentType: "application/json" },
      }),
      OPTS,
    );
    expect(out).toContain("--header 'Content-Type: application/vnd.custom+json'");
    expect(out).not.toContain("application/json'");
  });

  test("a text body with no declared header gains its own Content-Type", () => {
    const out = generateSnippet(
      "shell-curl",
      req({ method: "POST", body: { kind: "text", text: "{}", contentType: "application/json" } }),
      OPTS,
    );
    expect(out).toContain("--header 'Content-Type: application/json'");
  });

  test("a urlencoded body implies application/x-www-form-urlencoded", () => {
    const out = generateSnippet(
      "shell-curl",
      req({ method: "POST", body: { kind: "urlencoded", pairs: [["q", "v"]] } }),
      OPTS,
    );
    expect(out).toContain("--header 'Content-Type: application/x-www-form-urlencoded'");
  });

  test("a file body with no content type falls back to application/octet-stream", () => {
    const out = generateSnippet(
      "shell-curl",
      req({ method: "POST", body: { kind: "file", path: "/tmp/data.bin", contentType: "" } }),
      OPTS,
    );
    expect(out).toContain("--header 'Content-Type: application/octet-stream'");
    expect(out).toContain("--data-binary '@/tmp/data.bin'");
  });

  test("multipart drops a declared Content-Type so the client owns its boundary", () => {
    const out = generateSnippet(
      "shell-curl",
      req({
        method: "POST",
        headers: [["Content-Type", "multipart/form-data; boundary=stale"]],
        body: { kind: "formdata", parts: [{ name: "k", value: "v", file_path: null, content_type: null }] },
      }),
      OPTS,
    );
    expect(out).not.toContain("--header 'Content-Type");
    expect(out).toContain("--form 'k=v'");
  });

  test("the cookie jar folds into a Cookie header — the transport sends it, so the snippet must too", () => {
    const out = generateSnippet(
      "shell-curl",
      req({ options: { ...networkOptions(), cookies: [["sid", "abc"], ["theme", "dark"]] } }),
      OPTS,
    );
    expect(out).toContain("--header 'Cookie: sid=abc; theme=dark'");
  });

  test("an explicit Cookie header wins over the jar — no duplicate header", () => {
    const out = generateSnippet(
      "shell-curl",
      req({ headers: [["Cookie", "manual=1"]], options: { ...networkOptions(), cookies: [["sid", "abc"]] } }),
      OPTS,
    );
    expect(out).toContain("--header 'Cookie: manual=1'");
    expect(out).not.toContain("sid=abc");
  });
});

describe("urlencoded serialisation matches the transport", () => {
  test("curl uses per-pair --data-urlencode while every name is shell-safe", () => {
    const out = generateSnippet(
      "shell-curl",
      req({ method: "POST", body: { kind: "urlencoded", pairs: [["q", "a b!"], ["safe_name", "v"]] } }),
      OPTS,
    );
    expect(out).toContain("--data-urlencode 'q=a b!'");
    expect(out).toContain("--data-urlencode 'safe_name=v'");
    expect(out).not.toContain("--data-raw");
  });

  test("one unsafe name pushes the whole body through pre-encoded --data-raw", () => {
    // `--data-urlencode name=content` leaves the name raw, so "weird key" cannot travel that way.
    // The fallback also pins formEncode: space → +, and !'()~ percent-encoded beyond what
    // encodeURIComponent does.
    const out = generateSnippet(
      "shell-curl",
      req({ method: "POST", body: { kind: "urlencoded", pairs: [["weird key", "a b!'()~"]] } }),
      OPTS,
    );
    expect(out).toContain("--data-raw 'weird+key=a+b%21%27%28%29%7E'");
    expect(out).not.toContain("--data-urlencode");
  });
});

// One value exercising every character some family treats specially: a single quote, a literal
// backslash before a double quote, a newline, `$` (Kotlin/Dart interpolation), `#{` (Ruby
// interpolation) and an already-resolved-looking `{{foo}}` that must stay byte-identical.
const TRICKY = "pa'th\\\"x\n$v #{y} {{foo}}";

function trickyReq(): ResolvedRequest {
  return req({ headers: [["X-Tricky", TRICKY]] });
}

describe("escaping, one family at a time", () => {
  test("shell single quotes: only ' is special, closed-escaped-reopened as '\\''", () => {
    const out = generateSnippet("shell-curl", trickyReq(), OPTS);
    expect(out).toContain(String.raw`--header 'X-Tricky: pa'\''th\"x` + "\n" + String.raw`$v #{y} {{foo}}'`);
  });

  test("PowerShell single quotes: ' doubles, everything else stays literal", () => {
    const out = generateSnippet("powershell-restmethod", trickyReq(), OPTS);
    expect(out).toContain(String.raw`$headers.Add('X-Tricky', 'pa''th\"x` + "\n" + String.raw`$v #{y} {{foo}}')`);
  });

  test("JavaScript single quotes: \\ and ' escaped, newline becomes \\n", () => {
    const out = generateSnippet("javascript-fetch", trickyReq(), OPTS);
    expect(out).toContain(String.raw`'X-Tricky': 'pa\'th\\"x\n$v #{y} {{foo}}'`);
  });

  test("PHP single quotes: only \\ and ' escaped — the newline stays literal on purpose", () => {
    const out = generateSnippet("php-curl", trickyReq(), OPTS);
    expect(out).toContain(String.raw`'X-Tricky: pa\'th\\"x` + "\n" + String.raw`$v #{y} {{foo}}',`);
  });

  test("Kotlin double quotes add \\$ on top of the C escapes", () => {
    const out = generateSnippet("kotlin-okhttp", trickyReq(), OPTS);
    expect(out).toContain(String.raw`.addHeader("X-Tricky", "pa'th\\\"x\n\$v #{y} {{foo}}")`);
  });

  test("Ruby double quotes escape #{ but leave a bare $ alone", () => {
    const out = generateSnippet("ruby-nethttp", trickyReq(), OPTS);
    expect(out).toContain(String.raw`request["X-Tricky"] = "pa'th\\\"x\n$v \#{y} {{foo}}"`);
  });
});

describe("{{var}} is opaque — codegen formats resolved values, it never interpolates", () => {
  test("a templated-looking URL survives byte-identical", () => {
    const out = generateSnippet("shell-curl", req({ url: "https://{{base_url}}/users" }), OPTS);
    expect(out).toContain("--url 'https://{{base_url}}/users'");
  });

  test("a templated-looking header value survives in every family probed above", () => {
    for (const id of ["shell-curl", "javascript-fetch", "python-requests", "go-native"]) {
      expect(generateSnippet(id, trickyReq(), OPTS), id).toContain("{{foo}}");
    }
  });
});

describe("backendAuth becomes a comment, never a fabricated signature", () => {
  const digest = { kind: "digest", username: "u", password: "p" } as const;

  test("shell targets prepend the digest note as a # comment on the first line", () => {
    const out = generateSnippet("shell-curl", req({ backendAuth: digest }), OPTS);
    expect(out.split("\n")[0]).toBe(
      "# CodeFlow adds Digest authentication when sending — this snippet is unauthenticated.",
    );
  });

  test("awsv4 gets its own note", () => {
    const out = generateSnippet(
      "python-requests",
      req({
        backendAuth: {
          kind: "awsv4",
          access_key: "AK",
          secret_key: "SK",
          session_token: "",
          region: "us-east-1",
          service: "execute-api",
        },
      }),
      OPTS,
    );
    expect(out.split("\n")[0]).toBe(
      "# CodeFlow signs this request with AWS Signature V4 when sending — this snippet is unsigned.",
    );
  });

  test("PHP is the exception: the note lands after <?php, or the browser would echo it as text", () => {
    const out = generateSnippet("php-curl", req({ backendAuth: digest }), OPTS);
    const [first, second] = out.split("\n");
    expect(first).toBe("<?php");
    expect(second).toBe("// CodeFlow adds Digest authentication when sending — this snippet is unauthenticated.");
  });

  test("the note is translated through the language store", () => {
    useLanguageStore.setState({ language: "es" });
    const out = generateSnippet("shell-curl", req({ backendAuth: digest }), OPTS);
    expect(out.split("\n")[0]).toBe(
      "# CodeFlow añade autenticación Digest al enviar — este fragmento va sin autenticar.",
    );
  });

  test("no backendAuth, no note", () => {
    expect(generateSnippet("shell-curl", req(), OPTS)).not.toContain("#");
  });
});

describe("SnippetOptions shape the shell family", () => {
  test("multiline: false joins the command on one line with no continuations", () => {
    const out = generateSnippet("shell-curl", req({ headers: [["A", "1"]] }), { ...OPTS, multiline: false });
    expect(out).toBe("curl --request GET --url 'https://api.example.com/users' --location --header 'A: 1'");
  });

  test("indentWith is used verbatim for continuation lines, tabs included", () => {
    const out = generateSnippet("shell-curl", req(), { ...OPTS, indentWith: "\t" });
    expect(out).toContain(" \\\n\t--url");
  });

  test("includeBoilerplate: false drops python-requests' trailing print", () => {
    const on = generateSnippet("python-requests", req(), OPTS);
    const off = generateSnippet("python-requests", req(), { ...OPTS, includeBoilerplate: false });
    expect(on).toContain("print(response.text)");
    expect(off).not.toContain("print(response.text)");
  });

  test("includeBoilerplate: false drops wget's --output-document", () => {
    const off = generateSnippet("shell-wget", req(), { ...OPTS, includeBoilerplate: false });
    expect(off).not.toContain("--output-document");
  });
});

describe("body modes beyond the plain cases", () => {
  test("curl multipart: text parts as name=value, file parts as name=@path with their type", () => {
    const out = generateSnippet(
      "shell-curl",
      req({
        method: "POST",
        body: {
          kind: "formdata",
          parts: [
            { name: "note", value: "hi", file_path: null, content_type: null },
            { name: "avatar", value: null, file_path: "/tmp/a.png", content_type: "image/png" },
          ],
        },
      }),
      OPTS,
    );
    expect(out).toContain("--form 'note=hi'");
    expect(out).toContain("--form 'avatar=@/tmp/a.png;type=image/png'");
  });

  test("wget owns up to not speaking multipart instead of sending half a request", () => {
    const out = generateSnippet(
      "shell-wget",
      req({
        method: "POST",
        body: { kind: "formdata", parts: [{ name: "k", value: "v", file_path: null, content_type: null }] },
      }),
      OPTS,
    );
    expect(out.split("\n")[0]).toBe("# wget cannot build a multipart/form-data body — this snippet omits it.");
  });

  test("httpie pipes a text body through printf and redirects a file body", () => {
    const text = generateSnippet(
      "shell-httpie",
      req({ method: "POST", body: { kind: "text", text: "{}", contentType: "application/json" } }),
      OPTS,
    );
    expect(text.startsWith("printf '%s' '{}' | http")).toBe(true);
    const file = generateSnippet(
      "shell-httpie",
      req({ method: "POST", body: { kind: "file", path: "/tmp/b.bin", contentType: "" } }),
      OPTS,
    );
    expect(file.endsWith("< '/tmp/b.bin'")).toBe(true);
  });

  test("hand-assembled multipart announces the fixed CodeFlow boundary", () => {
    const out = generateSnippet(
      "http-raw",
      req({
        method: "POST",
        body: { kind: "formdata", parts: [{ name: "k", value: "v", file_path: null, content_type: null }] },
      }),
      OPTS,
    );
    expect(out).toContain("multipart/form-data; boundary=----CodeFlowFormBoundary");
    expect(out).toContain('Content-Disposition: form-data; name="k"');
  });
});
