import { describe, expect, test } from "vitest";
import { detectFormat, importAny, looksLikeCurl, parseCurl } from "./importers";
import type { ApiRequestSpec, ImportedItem } from "../../types/api";

/**
 * The import panel, and the two invariants this module states about itself (`:20-30`).
 *
 * Both of them fail quietly, and what they produce is written to SQLite as the user's own
 * collections:
 *
 * 1. **Nothing throws.** A half-mapped collection is useful; an exception costs the whole dialog.
 *    Anything unmappable becomes a warning and the rest still comes through.
 * 2. **`{{var}}` is opaque.** No importer interpolates, decodes or re-encodes a value that could
 *    hold a template. A `{{token}}` that gets URL-decoded stops being a variable reference and
 *    becomes a literal — the request still sends, against the wrong thing, and nobody finds out
 *    until someone reads the response.
 *
 * The scope here is those two plus `parseCurl`, which is the path most imports actually take and
 * is a 36-flag state machine over a hand-written shell tokenizer.
 */

/** Rows carry a generated `id`, so assertions compare the pair that means something. */
const pairs = (rows: { key: string; value: string }[]) => rows.map((r) => [r.key, r.value]);

function curl(command: string): ApiRequestSpec {
  const spec = parseCurl(command);
  if (!spec) throw new Error(`expected a spec for: ${command}`);
  return spec;
}

/** Recursive: OpenAPI groups by path segment, so its requests sit a folder down. */
const requests = (items: ImportedItem[]): ImportedItem[] =>
  items.flatMap((item) => (item.kind === "request" ? [item] : requests(item.items)));

// The smallest document each sniffer recognises, so a test says which key did the recognising
// instead of burying it in a 200-line export.
const MINIMAL = {
  postman: '{"info":{},"item":[{"name":"R","request":"https://example.com"}]}',
  openapi: '{"openapi":"3.0.0","paths":{"/x":{"get":{}}}}',
  har: '{"log":{"entries":[{"request":{"method":"GET","url":"https://example.com"}}]}}',
  insomnia:
    '{"_type":"export","resources":[{"_id":"wrk_1","_type":"workspace","name":"W"},' +
    '{"_id":"req_1","_type":"request","parentId":"wrk_1","name":"R","method":"GET","url":"https://example.com"}]}',
  codeflow: '{"format":"codeflow-api","collection":{"name":"C"},"requests":[{"name":"R","spec":"{}"}]}',
} as const;

describe("recognising what was pasted", () => {
  for (const [format, doc] of Object.entries(MINIMAL)) {
    test(`${format} is recognised by its own marker key`, () => {
      expect(detectFormat(doc)).toBe(format);
    });
  }

  test("a cURL command is recognised", () => {
    expect(detectFormat("curl https://example.com")).toBe("curl");
  });

  // curl is checked before anything is parsed as JSON, so a body that happens to contain a
  // Postman-shaped document cannot steal the command.
  test("curl wins over whatever its body contains", () => {
    expect(detectFormat(`curl -d '${MINIMAL.postman}' https://example.com`)).toBe("curl");
  });

  test("a curl path or curl.exe still counts", () => {
    expect(looksLikeCurl("/usr/bin/curl https://x.test")).toBe(true);
    expect(looksLikeCurl("curl.exe https://x.test")).toBe(true);
    expect(looksLikeCurl("curlyfries https://x.test")).toBe(false);
  });

  // Recognised on purpose even though it cannot be read, so the failure can explain itself
  // instead of arriving as "unrecognized format".
  test("a YAML OpenAPI document is recognised so the error can say why", () => {
    expect(detectFormat("openapi: 3.0.0\npaths: {}")).toBe("openapi");
  });

  test("nothing is recognised in an empty or unparseable input", () => {
    expect(detectFormat("")).toBe(null);
    expect(detectFormat("   ")).toBe(null);
    expect(detectFormat("{bad")).toBe(null);
  });

  test("a valid JSON object that is none of the six is not guessed at", () => {
    expect(detectFormat('{"x":1}')).toBe(null);
  });
});

describe("nothing throws, whatever was pasted", () => {
  // Each of these used to be a way to lose the dialog. The assertion is that a result comes back
  // at all — the warning is how the user learns what happened.
  for (const [label, input] of [
    ["plain prose", "hello world"],
    ["an empty string", ""],
    ["a truncated document", '{"info":{},"item":[],'],
    ["a top-level array", "[1,2]"],
    ["a top-level null", "null"],
    ["a bare number", "42"],
  ] as const) {
    test(`${label} produces a result, not an exception`, () => {
      const result = importAny(input);

      expect(result.collections).toEqual([]);
      expect(result.warnings.length).toBeGreaterThan(0);
    });
  }

  test("an unreadable input says what it expected instead", () => {
    expect(importAny("hello world").warnings[0]).toContain("Unrecognized format");
  });

  test("a YAML OpenAPI is told to convert it rather than left guessing", () => {
    expect(importAny("openapi: 3.0.0\npaths: {}").warnings[0]).toContain("Convert it to JSON");
  });

  test("a document of the right format but the wrong shape still returns", () => {
    const result = importAny('{"log":{"entries":[]}}');

    expect(result.format).toBe("har");
    expect(result.collections).toEqual([]);
    expect(result.warnings.length).toBeGreaterThan(0);
  });

  // One line per distinct problem is a report; one per occurrence is a wall, and the same
  // unmapped construct usually repeats once per request.
  test("the same problem twice is reported once", () => {
    const result = importAny("curl --frobnicate https://a.test\ncurl --frobnicate https://b.test");

    expect(result.warnings).toEqual(["Ignored unsupported cURL option: --frobnicate"]);
  });

  test("each minimal document yields at least one request", () => {
    for (const [format, doc] of Object.entries(MINIMAL)) {
      const result = importAny(doc);

      expect(result.format, format).toBe(format);
      expect(requests(result.collections[0]?.items ?? []).length, format).toBeGreaterThan(0);
    }
  });
});

describe("a {{template}} survives the trip", () => {
  // The whole point: `splitQuery` never decodes, so a variable in a query value arrives as it was
  // written — and a value that was already percent-encoded stays that way rather than being
  // decoded into something that looks like a template.
  test("a query value keeps both its template and its percent-encoding", () => {
    const spec = curl("curl 'https://x.test/a?token={{secret}}&b=c%20d'");

    expect(spec.url).toBe("https://x.test/a");
    expect(pairs(spec.params)).toEqual([
      ["token", "{{secret}}"],
      ["b", "c%20d"],
    ]);
  });

  // Otherwise `{{base_url}}/users` becomes `https://{{base_url}}/users`, and the variable can no
  // longer supply its own scheme.
  test("a URL that is entirely a variable gets no scheme prepended", () => {
    expect(curl("curl {{base_url}}/users").url).toBe("{{base_url}}/users");
  });

  // `--data-urlencode` genuinely has to percent-encode, which is the one place the rule needs a
  // carve-out rather than "don't touch it": the value is split on `{{…}}` spans and only the rest
  // is encoded.
  test("--data-urlencode encodes around a template, never through it", () => {
    const spec = curl("curl -X POST https://x.test --data-urlencode 'q={{token}} b' -d 'x=1'");

    expect(pairs(spec.body.urlencoded)).toEqual([
      ["q", "{{token}}%20b"],
      ["x", "1"],
    ]);
  });

  test("the same carve-out applies to a value with no name", () => {
    const spec = curl("curl -X POST https://x.test --data-urlencode '{{token}} b'");

    expect(spec.body.mode).toBe("raw");
    expect(spec.body.raw).toBe("{{token}}%20b");
  });

  // When every part is a named --data-urlencode the values are stored structurally instead, and
  // the encoding happens at send time — so nothing is encoded here at all.
  test("named parts are stored unencoded, because the sender encodes them", () => {
    const spec = curl("curl -X POST https://x.test --data-urlencode 'a={{v}} z' --data-urlencode 'b=2'");

    expect(pairs(spec.body.urlencoded)).toEqual([
      ["a", "{{v}} z"],
      ["b", "2"],
    ]);
  });

  test("a header value is never touched", () => {
    expect(pairs(curl("curl https://x.test -H 'X-Token: {{token}}'").headers)).toEqual([
      ["X-Token", "{{token}}"],
    ]);
  });

  test("credentials keep their template", () => {
    const spec = curl("curl -u 'ada:{{password}}' https://x.test");

    expect(spec.auth.basic).toEqual({ username: "ada", password: "{{password}}" });
  });
});

describe("reading a cURL command", () => {
  test("the plainest command is a GET at that URL", () => {
    const spec = curl("curl https://example.com");

    expect(spec.method).toBe("GET");
    expect(spec.url).toBe("https://example.com");
    expect(spec.body.mode).toBe("none");
  });

  test("-X sets the method", () => {
    expect(curl("curl -X delete https://x.test").method).toBe("DELETE");
  });

  test("-I is a HEAD", () => {
    expect(curl("curl -I https://x.test").method).toBe("HEAD");
  });

  // curl accepts a schemeless host; loopback gets http because nothing serves TLS there by default.
  test("a schemeless host gets https, and loopback gets http", () => {
    expect(curl("curl example.com/a").url).toBe("https://example.com/a");
    expect(curl("curl localhost:3000/a").url).toBe("http://localhost:3000/a");
    expect(curl("curl 127.0.0.1/a").url).toBe("http://127.0.0.1/a");
  });

  test("a command with no URL is not a request", () => {
    expect(parseCurl("curl -X POST -d x=1")).toBe(null);
  });

  test("headers come through, including the ones with their own flag", () => {
    const spec = curl("curl https://x.test -H 'A: 1' -A 'me/1.0' -e 'https://ref.test'");

    expect(pairs(spec.headers)).toEqual([
      ["A", "1"],
      ["User-Agent", "me/1.0"],
      ["Referer", "https://ref.test"],
    ]);
  });

  test("--compressed becomes the Accept-Encoding it stands for", () => {
    expect(pairs(curl("curl --compressed https://x.test").headers)).toEqual([
      ["Accept-Encoding", "gzip, deflate, br"],
    ]);
  });

  // A cookie jar is a file this app cannot read, so it is reported rather than silently dropped.
  test("a cookie pair is a header, a cookie file is a warning", () => {
    expect(pairs(curl("curl -b 'a=1' https://x.test").headers)).toEqual([["Cookie", "a=1"]]);
    expect(pairs(curl("curl -b cookies.txt https://x.test").headers)).toEqual([]);
    expect(importAny("curl -b cookies.txt https://x.test").warnings[0]).toContain("Cookies read from a file");
  });
});

describe("deciding what kind of body a cURL command has", () => {
  test("-F is multipart, and @ makes a part a file", () => {
    const spec = curl("curl -F name=ada -F file=@/tmp/a.txt https://x.test");

    expect(spec.body.mode).toBe("formdata");
    expect(spec.body.formdata.map((r) => [r.key, r.value, r.src])).toEqual([
      ["name", "ada", undefined],
      ["file", "", "/tmp/a.txt"],
    ]);
  });

  test("-T uploads a file and implies PUT", () => {
    const spec = curl("curl -T /tmp/a.bin https://x.test");

    expect(spec.method).toBe("PUT");
    expect(spec.body.mode).toBe("binary");
    expect(spec.body.binaryPath).toBe("/tmp/a.bin");
  });

  test("-X wins over the PUT that -T would have implied", () => {
    expect(curl("curl -X POST -T /tmp/a.bin https://x.test").method).toBe("POST");
  });

  test("a @file data part is a binary body", () => {
    const spec = curl("curl -X POST https://x.test -d @/tmp/payload.json");

    expect(spec.body.mode).toBe("binary");
    expect(spec.body.binaryPath).toBe("/tmp/payload.json");
  });

  // No Content-Type, but the body reads as a form — which is what curl itself would send it as.
  test("a form-shaped body with no content type is read as a form", () => {
    const spec = curl("curl -X POST https://x.test -d 'a=1&b=2'");

    expect(spec.body.mode).toBe("urlencoded");
    expect(pairs(spec.body.urlencoded)).toEqual([
      ["a", "1"],
      ["b", "2"],
    ]);
  });

  // An explicit Content-Type is the author's decision and overrides the guess.
  test("a declared content type overrules the guess", () => {
    const spec = curl("curl -X POST https://x.test -H 'Content-Type: text/plain' -d 'a=1&b=2'");

    expect(spec.body.mode).toBe("raw");
    expect(spec.body.raw).toBe("a=1&b=2");
  });

  test("a JSON body is kept raw and tagged as JSON", () => {
    const spec = curl(`curl -X POST https://x.test -d '{"a":1}'`);

    expect(spec.body.mode).toBe("raw");
    expect(spec.body.raw).toBe('{"a":1}');
    expect(spec.body.rawLanguage).toBe("json");
  });

  test("--json also declares the two headers it stands for", () => {
    const spec = curl(`curl --json '{"a":1}' https://x.test`);

    expect(spec.method).toBe("POST");
    expect(spec.body.raw).toBe('{"a":1}');
    expect(pairs(spec.headers)).toEqual([
      ["Content-Type", "application/json"],
      ["Accept", "application/json"],
    ]);
  });

  // `-G` moves the data to the query string, which is the whole reason the flag exists.
  test("-G turns the data into query parameters and leaves no body", () => {
    const spec = curl("curl -G --data-urlencode 'q=hi' https://x.test");

    expect(spec.method).toBe("GET");
    expect(pairs(spec.params)).toEqual([["q", "hi"]]);
    expect(spec.body.mode).toBe("none");
  });
});

describe("reading credentials out of a cURL command", () => {
  test("-u is basic auth by default", () => {
    expect(curl("curl -u ada:s3cret https://x.test").auth).toMatchObject({
      type: "basic",
      basic: { username: "ada", password: "s3cret" },
    });
  });

  test("--digest changes which scheme -u fills in", () => {
    expect(curl("curl --digest -u ada:s3cret https://x.test").auth).toMatchObject({
      type: "digest",
      digest: { username: "ada", password: "s3cret" },
    });
  });

  test("a password containing a colon survives the split", () => {
    expect(curl("curl -u 'ada:a:b' https://x.test").auth.basic).toEqual({
      username: "ada",
      password: "a:b",
    });
  });

  test("--oauth2-bearer is a bearer token", () => {
    expect(curl("curl --oauth2-bearer t1 https://x.test").auth).toMatchObject({
      type: "bearer",
      bearer: { token: "t1" },
    });
  });

  // The region and service come from the flag, the keys from `-u` — two flags, one config.
  test("--aws-sigv4 takes its region and service from the flag and its keys from -u", () => {
    expect(curl("curl --aws-sigv4 aws:amz:us-east-1:s3 -u AK:SK https://x.test").auth).toMatchObject({
      type: "awsv4",
      awsv4: { accessKey: "AK", secretKey: "SK", region: "us-east-1", service: "s3" },
    });
  });
});

describe("transport flags become request settings", () => {
  test("each one lands on the setting it means", () => {
    const spec = curl("curl -k -L --max-redirs 3 -m 2.5 --path-as-is https://x.test");

    expect(spec.settings).toMatchObject({
      verifySsl: false,
      followRedirects: true,
      maxRedirects: 3,
      // Seconds on the command line, milliseconds in the setting.
      timeoutMs: 2500,
      encodeUrl: false,
    });
  });

  test("--location-trusted also keeps credentials across the redirect", () => {
    expect(curl("curl --location-trusted https://x.test").settings).toMatchObject({
      followRedirects: true,
      keepAuthOnRedirect: true,
    });
  });

  // Untouched settings stay null rather than defaulting, so the request inherits whatever the
  // collection says instead of the command silently pinning a value.
  test("a flag that was not given leaves its setting unset", () => {
    expect(curl("curl https://x.test").settings).toMatchObject({
      verifySsl: null,
      followRedirects: null,
      maxRedirects: null,
      timeoutMs: null,
    });
  });

  // These are real curl options this app configures elsewhere; saying so beats importing a
  // request that quietly ignores them.
  test("per-request proxy and client certificates are reported, not swallowed", () => {
    expect(importAny("curl --proxy http://p:8080 https://x.test").warnings[0]).toContain(
      "Proxy settings are configured globally",
    );
    expect(importAny("curl --cert /tmp/c.pem https://x.test").warnings[0]).toContain(
      "Client certificates are configured",
    );
  });

  test("an unknown option is named rather than ignored", () => {
    expect(importAny("curl --frobnicate https://x.test").warnings[0]).toBe(
      "Ignored unsupported cURL option: --frobnicate",
    );
  });
});

describe("the shell tokenizer", () => {
  // How a command pasted out of DevTools arrives.
  test("a backslash-newline continuation is one command", () => {
    const spec = curl("curl https://x.test \\\n  -H 'A: 1' \\\n  -d 'x=1'");

    expect(pairs(spec.headers)).toEqual([["A", "1"]]);
    expect(pairs(spec.body.urlencoded)).toEqual([["x", "1"]]);
  });

  test("double quotes unescape, single quotes do not", () => {
    expect(pairs(curl('curl https://x.test -H "X: a\\"b"').headers)).toEqual([["X", 'a"b']]);
    expect(pairs(curl("curl https://x.test -H 'X: a\\nb'").headers)).toEqual([["X", "a\\nb"]]);
  });

  test("a $'…' string is unescaped the way the shell would", () => {
    expect(pairs(curl("curl https://x.test -H $'X: a\\tb'").headers)).toEqual([["X", "a\tb"]]);
  });

  // Half a command is still worth importing; refusing it would lose the rest.
  test("an unterminated quote takes the rest of the input rather than failing", () => {
    expect(pairs(curl("curl https://x.test -H 'X: oops").headers)).toEqual([["X", "oops"]]);
  });

  test("several commands in one paste become several requests", () => {
    const result = importAny("curl https://a.test\ncurl https://b.test");

    expect(requests(result.collections[0]?.items ?? [])).toHaveLength(2);
  });

  test("one command without a URL does not cost the others", () => {
    const result = importAny("curl -X POST\ncurl https://b.test");

    expect(requests(result.collections[0]?.items ?? [])).toHaveLength(1);
    expect(result.warnings).toContain("Skipped a command with no URL.");
  });

  // `--next` is curl's own way of chaining, and this importer does not follow it — so it says so
  // rather than importing the first half and looking complete.
  test("--next is reported as unsupported", () => {
    expect(importAny("curl https://x.test --next https://y.test").warnings).toContain(
      "--next isn't supported; only the first request in the command was imported.",
    );
  });
});
