# 08 — API client

The built-in API testing client's transport layer: everything a webview cannot do on its own —
raw sockets, challenge/response and canonical-signing auth, and the transport knobs `fetch`
doesn't expose. The frontend owns request construction (variable interpolation, pre-request
scripts, "plain" auth schemes); this document owns what happens once a fully-resolved request
crosses into the sidecar.

## Scope

- `src/CodeFlow.App/ApiClient/HttpSend.cs`, `SigV4.cs`, `DigestAuth.cs`, `ResponseDecoding.cs`
- `src/CodeFlow.App/ApiClient/WebSocketStream.cs`, `SocketIoFraming.cs`, `MqttConnection.cs`,
  `MqttEndpoint.cs`, `StreamRegistry.cs`, `StreamTlsPolicy.cs`
- `src/CodeFlow.App/ApiClient/ApiCommands.cs`, `ApiHttpCommands.cs`, `ApiStreamCommands.cs`
- gRPC is **not implemented — deferred**

The command parameter/return contract lives in `01-ipc-surface.md`; this document describes what
calling each one actually does. The tree/environment/history/cookie stores the CRUD commands
forward into are owned by `03-storage.md` — those commands are described here only as thin
forwarders, not re-specified.

## Commands

Grouped as they appear in `src/CodeFlow.App/ApiClient/ApiCommands.cs`, in file order. Source lines point at the `pub fn`.

**Tree: collections** (`src/CodeFlow.App/ApiClient/ApiCommands.cs`)
- `api_load_tree` :22 — loads one workspace's collections/folders/requests as a single nested tree.
- `api_create_collection` :28 — inserts an empty collection.
- `api_update_collection` :34 — replaces a collection row.
- `api_delete_collection` :40 — deletes a collection (cascades to its folders/requests at the DB layer).
- `api_duplicate_collection` :46 — deep-copies a collection.

**Tree: folders**
- `api_create_folder` :54 — inserts a folder, optionally nested under a parent folder.
- `api_update_folder` :65 — replaces a folder row.
- `api_delete_folder` :71 — deletes a folder.

**Tree: requests**
- `api_create_request` :79 — inserts a request; `spec` is the frontend's serialized `ApiRequestSpec` JSON, opaque here.
- `api_update_request` :93 — replaces a request row.
- `api_delete_request` :99 — deletes a request.
- `api_duplicate_request` :105 — copies a request.
- `api_move_node` :111 — reparents/reorders a folder or request (`kind` selects which).
- `api_reorder_collections` :124 — persists a new collection ordering for a workspace.

**Environments**
- `api_list_environments` :132, `api_create_environment` :138, `api_update_environment` :144,
  `api_delete_environment` :150, `api_duplicate_environment` :156 — CRUD over `ApiEnvironment` rows.

**History**
- `api_list_history` :164, `api_add_history` :170, `api_delete_history` :176,
  `api_clear_history` :182 — CRUD over `ApiHistoryEntry` rows.

**Cookies**
- `api_list_cookies` :190, `api_upsert_cookie` :196, `api_delete_cookie` :202,
  `api_clear_cookies` :208 — CRUD over `ApiCookie` rows. No matching/expiry logic lives here — see
  [Cookie handling](#cookie-handling).

**HTTP / GraphQL**
- `api_send_http` :216 — sends one request; not cancellable.
- `api_send_http_tracked` :222 — same send, registered under `id` so `api_cancel_http` can abort it.
- `api_cancel_http` :236 — fires the cancel token for `id`; a no-op if the send already finished.

**WebSocket / Socket.IO**
- `api_ws_connect` :247 — opens a WebSocket and spawns its read/write pump.
- `api_ws_send` :252 — queues a text or (base64) binary frame on an open WebSocket.
- `api_socketio_connect` :262 — opens the underlying WebSocket and spawns the Socket.IO pump.
- `api_socketio_emit` :271 — queues a Socket.IO `EVENT` packet.

**MQTT**
- `api_mqtt_connect` :283 — opens a broker connection and spawns its two tasks.
- `api_mqtt_publish` :288, `api_mqtt_subscribe` :300, `api_mqtt_unsubscribe` :305 — queue the
  matching MQTT control packet.

**Shared**
- `api_stream_disconnect` :312 — closes any live WS/Socket.IO/MQTT connection by id.

**gRPC**
- `api_grpc_describe` :320 — lists services/methods from a `.proto` file or server reflection.
- `api_grpc_call` :325 — invokes one method (unary/client-stream/server-stream/bidi), cancellable.

**Files**
- `api_read_file_base64` :378 — reads a file as base64 plus an extension-guessed MIME type, for
  attaching it as a request body.
- `api_pick_file` :390 — native "open file" dialog.
- `api_save_file` :405 — native "save file" dialog; writes `contents` only if the user picks a path.
- `api_read_text_file` :421 — reads a file as UTF-8 text, erroring if it isn't valid UTF-8.

## The transport boundary

Quoted verbatim from the two places that state it, because both sides of the IPC boundary commit
to the same sentence:

`src/CodeFlow.App/ApiClient/ApiModels.cs` (module doc):
> This module is a transport, not a model. It never reads the database, never resolves a
> `{{variable}}`, and never decides what a request should contain — the frontend interpolates
> variables, runs the pre-request script, applies every auth scheme it can express as a header and
> hands down a fully-resolved `HttpSendRequest`. What lives here is only what a webview genuinely
> cannot do:
> - Raw sockets — WebSocket, Socket.IO, MQTT and gRPC.
> - Auth that needs the wire — Digest (a challenge/response round trip) and AWS SigV4 (a canonical
>   form built from the request as it will actually be sent), both in `http`.
> - Transport knobs the fetch API doesn't expose — per-request TLS verification, client
>   certificates, proxies, redirect policy, streaming file bodies, real timings.
>
> Every type below is mirrored one-for-one in `src/types/api.ts`; the field names are the serde
> wire names, so renaming one here is a breaking change on both sides.

`renderer/src/lib/ipc/apiCommands.ts:20-33` (header comment, frontend side):
> The split of responsibility is deliberate: **the backend is a transport, not a model.** It never
> reads a collection, resolves a variable, or applies auth that could be expressed as a header — the
> frontend interpolates `{{vars}}`, runs the pre-request script and builds the final headers, then
> hands the backend a fully-resolved request. The exceptions are the two things a webview genuinely
> can't do: schemes that need the server's challenge or a canonical signing form (Digest, AWS
> SigV4), and raw sockets (WS/MQTT/gRPC).

Practically: `src/CodeFlow.App/ApiClient/ApiCommands.cs` never inspects `HttpSendRequest.headers` for `{{...}}`, never
runs a script, never reads `ApiRequestSpec`. Every one of the six auth *types* the frontend models
in `AuthConfig` (`src/types/api.ts:153`) — Basic, Bearer, API key, JWT, OAuth2, plus Digest and
AWS SigV4 — resolves to plain headers on the frontend **except** Digest and SigV4, which travel as
a `BackendAuth` value inside `HttpSendRequest.auth` because they cannot be pre-computed without the
final method/URL/headers/body or a live round trip to the server.

## Wire contract types

The 20 types in `src/CodeFlow.App/ApiClient/ApiModels.cs`, each mirrored field-for-field in `src/types/api.ts:590-870`
(confirmed by direct comparison; serde's default naming means the the sidecar `snake_case` field name
*is* the wire name and the TS name, with no ` overrides anywhere in this file).

| the sidecar type | `src/CodeFlow.App/Storage/Database.cs` | Fields | TS mirror |
|---|---|---|---|
| `NetworkOptions` | :37-56 | `timeout_ms: ulong` (default 30 000), `follow_redirects: bool` (default true), `max_redirects: int` (default 10), `verify_ssl: bool` (default true), `keep_auth_on_redirect: bool` (default **false** — off because forwarding a bearer token to whatever host a 302 names is a credential leak), `proxy_url: string` (`""` = direct), `client_cert_path`/`client_cert_password: string`, `ca_cert_path: string`, `cookies: IReadOnlyList<(string,string)>` (pre-matched by the caller), `max_response_bytes: ulong` (default `50*1024*1024` = 52 428 800, `0` = unlimited) | `NetworkOptions` :626-642, identical |
| `FormPart` | :82-87 | `name`, `value: string?`, `file_path: string?`, `content_type: string?` — a part is a file when `file_path` is set, text otherwise | `FormPart` :606-613 |
| `BackendAuth` | :90-103 | tagged enum, `: `Digest{username,password}`, `Awsv4{access_key,secret_key,session_token,region,service}` | `BackendAuth` :615-624, discriminated union on `kind: "digest" \| "awsv4"` |
| `HttpSendRequest` | :107-120 | `method`, `url`, `headers: IReadOnlyList<(string,string)>`, then exactly one of `body_text`/`body_base64`/`body_file`/`form_data`/`urlencoded` (or none), `auth: BackendAuth?`, `options: NetworkOptions` | `HttpSendRequest` :590-604 |
| `ResponseTimings` | :122-130 | `dns_ms`, `connect_ms`, `tls_ms`, `first_byte_ms`, `download_ms`, `total_ms`, all `long`; `-1` means "unavailable" (reqwest exposes no connection trace, see [HTTP](#http--graphql)) | `ResponseTimings` :666-673 |
| `ParsedCookie` | :132-141 | `name`, `value`, `domain`, `path`, `expires: string?` (RFC 3339), `secure: bool`, `http_only: bool` | `ParsedCookie` :675-683 |
| `SentRequestSummary` | :145-151 | `method`, `url`, `headers: IReadOnlyList<(string,string)>`, `body_preview: string` — what actually went on the wire, including headers reqwest added | `SentRequestSummary` :685-690 |
| `HttpResponse` | :153-169 | `status: ushort`, `status_text`, `http_version`, `headers`, `body_text` (empty when binary), `body_base64: string?`, `size_bytes: ulong`, `duration_ms: long`, `timings`, `redirects: IReadOnlyList<string>` (every hop, final URL last), `set_cookies: IReadOnlyList<ParsedCookie>`, `sent: SentRequestSummary` | `HttpResponse` :644-664 |
| `WsConnectRequest` | :176-183 | `url`, `headers`, `subprotocols: IReadOnlyList<string>`, `ping_interval_ms: ulong` (`0` = no auto-ping), `options` | `WsConnectRequest` :756-763 |
| `SocketIoConnectRequest` | :185-196 | `url`, `path`, `namespace`, `version` (`"v4"` = Socket.IO 3/4, `"v3"` = Socket.IO 2), `headers`, `auth_json: string`, `query`, `options` | `SocketIoConnectRequest` :765-776 |
| `MqttLastWill` | :198-204 | `topic`, `payload`, `qos: byte`, `retain: bool` | inlined anonymously in `MqttConnectRequest.last_will` :786 — same fields, no named TS type |
| `MqttSubscribe` | :206-210 | `topic`, `qos: byte` | inlined anonymously in `MqttConnectRequest.subscriptions` :787 |
| `MqttConnectRequest` | :212-225 | `url`, `client_id`, `username`, `password`, `keep_alive_secs: ulong`, `clean_session: bool`, `version` (`"3.1.1"` or `"5.0"`), `last_will: MqttLastWill?`, `subscriptions: IReadOnlyList<MqttSubscribe>`, `options` | `MqttConnectRequest` :778-789 |
| `StreamMessage` | :228-257 | `connection_id`, `direction` (`sent`\|`received`\|`system`\|`error`), `channel` (Socket.IO event name / MQTT topic / `""`), `payload: string`, `binary: bool`, `at: long` (Unix ms), `qos: byte?`, `retain: bool?` (MQTT only) | `StreamMessage` :792-806 |
| `StreamStatusEvent` | :260-266 | `connection_id`, `status` (`connecting`\|`open`\|`closed`\|`error`), `detail: string` | `StreamStatusEvent` :808-812 |
| `GrpcMethodInfo` | :286-295 | `name`, `full_name`, `client_streaming: bool`, `server_streaming: bool`, `input_example: string` (JSON skeleton), `input_type`, `output_type` | `GrpcMethodInfo` :823-832 |
| `GrpcServiceInfo` | :297-301 | `name`, `methods: IReadOnlyList<GrpcMethodInfo>` | `GrpcServiceInfo` :818-821 |
| `GrpcDescribeRequest` | :303-314 | `source` (`"proto"`\|`"reflection"`), `proto_path`, `import_paths: IReadOnlyList<string>`, `endpoint` (reflection only), `use_tls: bool`, `metadata`, `options` | `GrpcDescribeRequest` :834-844 |
| `GrpcCallRequest` | :316-330 | `source`, `proto_path`, `import_paths`, `endpoint`, `service`, `method`, `message_json: string` (object, or array for client-streaming), `metadata`, `use_tls`, `authority: string` (overrides the `:authority` pseudo-header, not the connection target), `options` | `GrpcCallRequest` :846-859 |
| `GrpcResponse` | :332-341 | `message_json` (array for server-streaming), `status_code: int`, `status_message`, `headers`, `trailers`, `duration_ms: long` | `GrpcResponse` :861-870 |

Not mirrored to TS, and not meant to be — pure the sidecar runtime state, described in
[Connection registry and cancellation](#connection-registry-and-cancellation): `Connection`,
`WsCommand`, `MqttCommand`, `ApiRegistry`.

## HTTP / GraphQL

GraphQL is not a distinct transport — it is `HttpSendRequest` with a JSON `body_text` sent to a
single endpoint by POST, per `src/CodeFlow.App/ApiClient/HttpSend.cs` ("HTTP (and therefore GraphQL — it is a POST with a JSON
body) transport"). There is no GraphQL-specific code in this file at all.

**One `HttpClient` per send.** `build_client` (`src/CodeFlow.App/ApiClient/HttpSend.cs`) constructs a fresh client
for every request: TLS verification (`danger_accept_invalid_certs`), the client identity, the CA
bundle, the proxy and the redirect policy are all builder-level settings in reqwest, so two
requests with different transport settings cannot share a client. This is deliberate (module doc,
`src/CodeFlow.App/ApiClient/HttpSend.cs`), not an oversight.

**Digest client identity restriction** (`client_identity`, `src/CodeFlow.App/ApiClient/HttpSend.cs`): the TLS backend is
rustls, which accepts only an unencrypted PEM identity. A `.p12`/`.pfx` path is rejected outright
with an `openssl pkcs12 …` conversion hint; a non-empty `client_cert_password` is also rejected
(rustls cannot decrypt a private key) with an `openssl pkcs8 …` hint. Both are refusals with an
actionable error, not silent ignoring.

### The redirect policy

Custom (`redirect_policy`, `src/CodeFlow.App/ApiClient/HttpSend.cs`), not `HttpClient`::Policy.limited`, because
`keep_auth_on_redirect` needs to intercept a cross-host hop before reqwest strips credentials from
it.

- `follow_redirects: false` → the redirect policy.none()`. No redirect is ever followed; the caller
  gets the raw 3xx.
- Otherwise, a custom closure runs on every hop reqwest's own engine encounters:
  - **Hop cap**: `attempt.previous().len() > max_redirects` → `attempt.error("more than {max}
    redirects")`. `previous()` is reqwest's own count of hops already taken in this attempt.
  - **`keep_auth_on_redirect: true` and the hop crosses host** (`same_origin` compares
    `host_str()` + `port_or_known_default()`) → `attempt.stop()`. This hands the *current* 3xx
    response back to our own code as if it were final, specifically so reqwest does not get the
    chance to strip `Authorization`/`Cookie`/`Proxy-Authorization` the way it does on every
    cross-host hop by default.
  - Otherwise → the hop URL is pushed onto the shared `hops: Arc<Mutex<IReadOnlyList<string>>>` and
    `attempt.follow()` lets reqwest continue with whatever method/body transform *reqwest itself*
    decided for this hop. That decision (301/302/303 semantics, 307/308 preservation) is made
    inside the `reqwest` library, not in this file — this closure only ever says "yes" or "no" to a
    hop reqwest has already prepared.

**The manual resume path** (`run_exchange`, `src/CodeFlow.App/ApiClient/HttpSend.cs`) only ever runs the loop body a
second time when `manual_redirect_target` (`src/CodeFlow.App/ApiClient/HttpSend.cs`) returns `Some`, which requires *all
three*: `follow_redirects`, `keep_auth_on_redirect`, and the response being a redirection — i.e.
only for the one cross-host hop the policy above deliberately stopped at. For every other
configuration, reqwest's own engine has already followed the whole chain by the time `.execute()`
returns, and `run_exchange` returns on the first iteration.

When the manual path *does* fire, it re-reads `Location`, joins it against the current URL, checks
the shared `hops` counter against `max_redirects` again (`taken >= max_redirects` →
`"{method} {url} went through more than {n} redirects"`), and rebuilds the request **from
`req.headers` again** — which is what keeps `Authorization`/`Cookie` intact across the host
change. It also applies its own method/body transform:

`
303                              → GET, no body
301 | 302 when method not GET/HEAD → GET, no body
307 | 308 (and everything else)  → method and body unchanged
`

**`BUG-API-a`**: the comment immediately above this match (`src/CodeFlow.App/ApiClient/HttpSend.cs`) says *"Browsers …
turn a redirected POST into a bodiless GET for 301/302/303"* — but the code changes **any** method
other than GET/HEAD to GET on 301/302, not just POST. A 301/302-redirected PUT or DELETE is
silently downgraded to GET here, which is broader than the browser/`fetch` behaviour the comment
claims to be replicating. This path is reachable only when `keep_auth_on_redirect: true` **and**
the redirect crosses hosts — the default configuration never exercises it (reqwest's own,
unexamined internal policy handles every hop instead). Suspected-correct behaviour: only POST
(and, per RFC 7231 in practice, only when the client doesn't support 307/308 explicitly) should be
downgraded on 301/302; PUT/PATCH/DELETE should keep their method and body. Ported as-is.

The `redirects` field is built from the same `hops` vector both paths share, so its count stays
consistent across the automatic/manual split; `hops` records the *targets* redirected to (not the
starting URL), and `send_inner` (`src/CodeFlow.App/ApiClient/HttpSend.cs`) appends the final URL if the last recorded hop
isn't already it.

### Digest (RFC 7616)

`digest_response`/`digest_authorization`/`digest_challenge`/`parse_auth_params`, `src/CodeFlow.App/ApiClient/HttpSend.cs`.

The flow (`send_inner`, `src/CodeFlow.App/ApiClient/HttpSend.cs`): send once unauthenticated; if the response is 401 **and**
carries a `WWW-Authenticate: Digest` challenge, compute the response and resend — there is no
100-continue dance, the body goes out twice. The re-send targets `attempt.response.url()` (where
the *first* attempt actually landed after any redirects), not the originally-typed URL, because
the nonce and the signed request-target belong to wherever the challenge was issued.

- **Hashes**: `MD5`, `MD5-sess`, `SHA-256`, `SHA-256-sess` (`DigestHash`, `src/CodeFlow.App/ApiClient/HttpSend.cs`). Any
  other `algorithm` value is a hard error naming the four it supports. `-sess` folds the nonces
  into HA1 (`ha1 = H(H(user:realm:pass):nonce:cnonce)`) so a leaked HA1 expires with the nonce.
- **qop**: only `"auth"` is implemented. If the challenge's `qop` list doesn't contain `auth`
  (case-insensitively), the request fails with `"the server only offers digest qop='{offered}';
  this client implements qop=auth"` — `auth-int` (body-hash-in-the-digest) is never attempted, even
  if offered alongside `auth` and preferred by the server. If the challenge has no `qop` at all,
  the RFC 2069 fallback applies: `response = H(HA1:nonce:HA2)`, dropping `nc`/`cnonce`/`qop` from
  both the hash and the emitted header.
- **`nc` is always `"00000001"`** (`src/CodeFlow.App/ApiClient/HttpSend.cs`) and **`cnonce` is 16 random bytes, hex-encoded,
  fresh every send** (`src/CodeFlow.App/ApiClient/HttpSend.cs`). There is no nonce cache across separate `send`/`send_tracked`
  calls — every Digest request gets its own fresh 401 round trip, so `nc=1` is always valid; the
  client never exercises the "reuse a nonce with an incrementing `nc`" optimisation the RFC allows.
- **`uri`** signed is `path[?query]` from the *target* URL (`src/CodeFlow.App/ApiClient/HttpSend.cs`), matching RFC 7616's
  `request-target`, not the original request's path if a redirect intervened.
- **Header assembly** (`src/CodeFlow.App/ApiClient/HttpSend.cs`): `Digest username="…", realm="…", nonce="…", uri="…",
  response="…"`, then `algorithm=` only if the challenge itself carried one (never invented), then
  `opaque="…"` if present, then `qop=auth, nc=00000001, cnonce="…"` only when `qop` was negotiated.

**`BUG-API-b`**: `digest_challenge` (`src/CodeFlow.App/ApiClient/HttpSend.cs`) iterates every `WWW-Authenticate` header
*instance* (`get_all`) but, for each one, only recognises Digest if the literal string `"Digest"`
occupies the first six characters of that header's value. RFC 7235 allows several challenges to be
combined in one header value, comma-separated (`WWW-Authenticate: Basic realm="x", Digest
realm="y", nonce="…"`). A server that combines them this way — legal, and seen in the wild — has
its Digest challenge silently missed unless Digest happens to be the first scheme listed, and the
request fails with `"returned 401 but no 'WWW-Authenticate: Digest' challenge"` even though one was
present. Suspected-correct behaviour: split each header value on scheme boundaries before matching.
Ported as-is.

### AWS Signature Version 4

`sigv4_sign`/`sigv4_headers`/`canonical_uri`/`canonical_query`, `src/CodeFlow.App/ApiClient/HttpSend.cs`. Byte-identical
against Amazon's published `get-vanilla` vector — see [Test coverage](#test-coverage) and
`test-vectors/http.vectors.json`.

- **Algorithm**: `AWS4-HMAC-SHA256` only.
- **Canonical request**: `METHOD\nCANONICAL_URI\nCANONICAL_QUERY\nCANONICAL_HEADERS\n\nSIGNED_HEADERS\nPAYLOAD_HASH`
  (`src/CodeFlow.App/ApiClient/HttpSend.cs`) — note the header block already ends in `\n` per header, so the blank line
  before `SIGNED_HEADERS` is literal, not an extra join.
- **Canonical URI** (`canonical_uri`, `src/CodeFlow.App/ApiClient/HttpSend.cs`): `url.path()` is already percent-decoded
  once by the `url` library's parser; every service *except* `s3` (case-insensitive) gets it
  re-percent-encoded a second time (`%` → `%25` etc.) using `SIGV4_PATH` (all non-alphanumerics
  except `- _ . ~ /`). S3 is signed exactly as it appears on the wire, unencoded a second time —
  this is AWS's own S3-specific carve-out, not an oversight.
- **Canonical query** (`canonical_query`, `src/CodeFlow.App/ApiClient/HttpSend.cs`): every pair percent-encoded with
  `SIGV4_UNRESERVED` (same set minus the `/` exemption), then **lexicographically sorted by the
  encoded `(key, value)` tuple**, then joined with `&`. Repeated keys are not merged; each keeps its
  own position in sorted order (verified by `canonical_query_sorts_and_encodes`).
- **Canonical headers**: every `(name, value)` pair in `request.headers` is included — the caller
  (`sigv4_headers`) has already filtered out unsignable ones (below) and prepended `host` plus
  appended `x-amz-date`/`x-amz-content-sha256`/`x-amz-security-token`. Repeated header names are
  merged into one comma-joined value in send order (not sorted within the value, only the header
  names are sorted); each value has internal whitespace runs collapsed to a single space
  (`normalize_header_value`) before joining.
- **Unsignable headers** (`is_unsignable`, `src/CodeFlow.App/ApiClient/HttpSend.cs`), excluded from the signed set
  entirely: `accept-encoding`, `authorization`, `connection`, `content-length`, `expect`,
  `keep-alive`, `proxy-authorization`, `te`, `transfer-encoding`, `user-agent` — the same set the
  AWS SDKs exclude, because the transport rewrites or adds these after signing.
- **Payload hash** (`sigv4_headers` callers via `prepare_body`, `src/CodeFlow.App/ApiClient/HttpSend.cs`): SHA-256 hex of
  the exact bytes for `body_text`/`body_base64`; a **second read pass over the file** (`hash_file`,
  `src/CodeFlow.App/ApiClient/HttpSend.cs`) for `body_file`, so the payload can be streamed *and* signed without buffering
  it; the literal string `UNSIGNED-PAYLOAD` for multipart bodies, because reqwest assembles the
  multipart bytes (and boundary) internally and they are not knowable at signing time — AWS
  explicitly accepts `UNSIGNED-PAYLOAD` for exactly this case.
- **Signing key derivation**: `HMAC(HMAC(HMAC(HMAC("AWS4"+secret, date), region), service),
  "aws4_request")`, then `HMAC(that, string_to_sign)` → hex signature.
- **`Authorization` header**: `AWS4-HMAC-SHA256 Credential={access_key}/{date}/{region}/{service}/aws4_request,
  SignedHeaders={signed_headers}, Signature={signature}`. `access_key` never enters the signature
  computation itself — only the header — so it cannot drift into the maths (module comment,
  `src/CodeFlow.App/ApiClient/HttpSend.cs`).
- Both `access_key`/`secret_key` and `region`/`service` are required non-empty, or the call fails
  before any network access.

### Body handling

**Priority order** (`prepare_body`, `src/CodeFlow.App/ApiClient/HttpSend.cs`) — exactly one wins, first match:
`body_text` → `body_base64` → `body_file` → `urlencoded` → `form_data` → none. `HttpSendRequest`'s
doc comment states the frontend sends only one; nothing here enforces that, it simply picks by this
fixed order if more than one is set.

- **`body_file`**: streamed from disk in `FILE_CHUNK_BYTES = 64 * 1024`-byte reads
  (`file_stream`, `src/CodeFlow.App/ApiClient/HttpSend.cs`), wrapped as a `HttpClient` stream — a multi-gigabyte upload
  never enters process memory. Content-Length is set explicitly from the file's metadata length
  (`file_len`) so hyper doesn't fall back to chunked transfer encoding.
- **`form_data` (multipart)**: `build_multipart` (`src/CodeFlow.App/ApiClient/HttpSend.cs`) builds a `HttpClient`::Form`
  part by part — file parts stream via `Part.stream_with_length` (known length, same 64 KiB
  chunking as above); text parts via `Part.text`. Any caller-declared `content-type` header is
  **stripped and never sent** when the body is multipart (`src/CodeFlow.App/ApiClient/HttpSend.cs`), because the boundary
  is generated inside `Form` and only the value it produces can delimit the body correctly.
- **`urlencoded`**: `application/x-www-form-urlencoded`-serialized pairs; `Content-Type` is set to
  that value only if the caller hasn't already set one.
- **Body preview** (`preview_text`/`preview_bytes`, `src/CodeFlow.App/ApiClient/HttpSend.cs`): truncated to
  `BODY_PREVIEW_LIMIT = 2048` bytes/chars, cut backward to the nearest UTF-8 char boundary so it
  never splits a multi-byte character, with `"… ({n} bytes total)"` appended when truncated. A
  non-UTF-8 byte body previews as `"<{n} bytes of binary>"`. This preview is *only* for the console
  before the send; `SentRequestSummary.body_preview` carries it, and the file-streamed case previews
  as `"<{size} bytes streamed from {path}>"` without ever reading the file's content for the preview.

### Response handling

**Size cap** (`read_body`, `src/CodeFlow.App/ApiClient/HttpSend.cs`): `NetworkOptions.max_response_bytes`, default
`50 * 1024 * 1024` = **52 428 800 bytes**, `0` = unlimited. Bytes are read chunk by chunk; the
moment the cap would be exceeded, only the remaining room is copied in and the read stops — **this
is a truncation, not an error**. `size_bytes` in the response reflects the truncated length actually
kept, not any `Content-Length` the server declared.

**Text vs. binary decision** (`decode_body`/`is_textual_type`/`looks_binary`/`charset_of`,
`src/CodeFlow.App/ApiClient/HttpSend.cs`):

1. If `Content-Type` is declared, `is_textual_type` decides — `VERBATIM`, this exact set:
   `
   media.starts_with("text/")
     || media.ends_with("+json")
     || media.ends_with("+xml")
     || matches!(media,
         "application/json" | "application/xml" | "application/javascript"
         | "application/x-javascript" | "application/ecmascript" | "application/graphql"
         | "application/x-www-form-urlencoded" | "application/x-ndjson"
         | "application/ld+json" | "application/sql" | "image/svg+xml")
   `
   (`media` is the part of `Content-Type` before the first `;`, trimmed.) Any `+json`/`+xml` vendor
   type (`application/vnd.github+json`) counts as text by suffix, deliberately not by an exhaustive
   list.
2. If no `Content-Type` at all, `looks_binary` decides: a NUL byte anywhere in the **first 4096
   bytes** means binary. No NUL in that window means text.
3. **Binary** → `body_text` empty, `body_base64` = standard-alphabet base64 of the raw bytes.
4. **Text** → charset-aware decode: if the declared charset (from the `charset=` parameter) is one
   of `iso-8859-1`, `latin1`, `latin-1`, `windows-1252`, `cp1252` (case-insensitive), every byte is
   cast directly to its Unicode code point (`bytes.iter().map(|&b| b as char)`) — a correct Latin-1
   transcode, not a guess, because those code points *are* Unicode 0–255 by definition. Otherwise,
   **lossy UTF-8** decode: invalid sequences become U+FFFD, and the rest of a mostly-valid page (a
   `text/html; charset=utf-8` page with one bad byte — the module comment cites google.com as a real
   example) still renders instead of falling back to base64 wholesale.

**`ADVERTISED_ENCODINGS`** (`src/CodeFlow.App/ApiClient/HttpSend.cs`), `VERBATIM`:
`csharp
private const string AdvertisedEncodings = "gzip, br, deflate";
`
reqwest negotiates these encodings from a layer below the `Request` this module can inspect, so
`wire_headers` (`src/CodeFlow.App/ApiClient/HttpSend.cs`) reconstructs the `Accept-Encoding` header for the console instead
of reading it back — it is appended only if the built request doesn't already carry one. The exact
same string is duplicated in the frontend at `src/lib/api/send.ts:154` (`buildImplicitHeaders`),
under an explicit comment there: `// Must mirror ADVERTISED_ENCODINGS in `src/CodeFlow.App/ApiClient/HttpSend.cs`.
Confirmed byte-identical between the two files at read time.

`wire_headers` also synthesizes the `Host` header from the URL when the built request doesn't carry
one explicitly (`host[:port]`), so the console's "what was actually sent" view is honest about both
headers the transport injects below the level this code can otherwise see.

### Cookie handling

`parse_set_cookie`/`parse_set_cookies`/`parse_http_date`, `src/CodeFlow.App/ApiClient/HttpSend.cs`.

- Parses every `Set-Cookie` header instance (`get_all`) independently.
- Splits on `;`; the first segment is `name=value` (both trimmed); attribute matching is
  case-insensitive on the key.
- **Defaults**: `domain` = the *request* URL's host (`url.host_str()`); `path` = `"/"` unconditionally
  — **`BUG-API-c`**: RFC 6265 §5.1.4 specifies a default-path *algorithm* derived from the request
  URI's own path (drop the last `/`-segment; `"/"` only if there's nothing left), not a flat `"/"`.
  A response to `/v1/users/42` with no `Path` attribute should default the cookie to `/v1/users`,
  not to the root — this implementation always defaults to root regardless of the request path.
  Suspected-correct behaviour: implement RFC 6265's default-path algorithm. Ported as-is (verified
  by `set_cookie_defaults_to_the_request_host_and_root_path`, which only exercises a root-path
  request and so cannot distinguish the two behaviours).
- `Domain=` strips a leading `.` (the pre-RFC 6265 "and every subdomain" spelling; modern semantics
  are the same without it) and overrides the default.
- `Path=` overrides the default verbatim, no normalization.
- **Expiry**: `Max-Age` wins over `Expires` wherever both are present (RFC 6265 order). `Max-Age`
  becomes `now + seconds` in RFC 3339. `Expires` is parsed by `parse_http_date`
  (`src/CodeFlow.App/ApiClient/HttpSend.cs`), trying, in order: RFC 2822, then `"%a, %d %b %Y %H:%M:%S GMT"` (RFC 1123),
  `"%A, %d-%b-%y %H:%M:%S GMT"` (obsolete RFC 850), `"%a %b %e %H:%M:%S %Y"` (`asctime`) — the three
  formats RFC 6265 requires a client to tolerate. An unparseable date gives up rather than guessing:
  the cookie comes back with `expires: None`, i.e. reads as a session cookie.
- **No jar exists in these files.** `NetworkOptions.cookies` is a pre-matched `(name, value)` list
  the *caller* supplies for the URL being sent; `build_request` (`src/CodeFlow.App/ApiClient/HttpSend.cs`) only appends it
  as a `Cookie` header when the caller hasn't already set one explicitly. Matching cookies to a URL
  by domain/path/expiry, and persisting `set_cookies` back into storage, is entirely a frontend/DB
  concern (`api_list_cookies`/`api_upsert_cookie`/… forward straight into `ApiTreeStore`, owned
  elsewhere). This module only parses `Set-Cookie` into `ParsedCookie` and forwards a supplied jar
  outward; it never reads or writes the `ApiCookie` table itself.

## WebSocket

`src/CodeFlow.App/ApiClient/WebSocketStream.cs`. The reader/writer pump (`pump`, `src/CodeFlow.App/ApiClient/WebSocketStream.cs`) is spawned and **outlives** the `connect`
command — `connect` takes an `AppHandle` (not the short-lived `State` a command normally receives)
specifically so the pump can keep reaching the registry and emitting events long after the command
itself returned (`src/CodeFlow.App/ApiClient/WebSocketStream.cs`). Every frame in either direction, and every keepalive tick, becomes a
`StreamMessage`; the UI has no other way to inspect a live connection's history (`src/CodeFlow.App/ApiClient/WebSocketStream.cs`).

- **Registration before dialling** (`connect`, `src/CodeFlow.App/ApiClient/WebSocketStream.cs`): the `mpsc` sender is inserted into
  `ApiRegistry` *before* the handshake starts, so `api_ws_send` calls issued the instant `connect`
  returns — or even while the handshake is still in flight — queue in the channel instead of
  failing with "no open connection".
- **Scheme normalization** (`normalize_scheme`, `src/CodeFlow.App/ApiClient/WebSocketStream.cs`): `https://` → `wss://`, `http://` →
  `ws://`, case-insensitively matched on the scheme only — the rest of the URL (including case) is
  preserved verbatim. Anything already `ws://`/`wss://`, or any other scheme, passes through
  untouched (a non-WS scheme surfaces later as an `IntoClientRequest` error, not here).
- **Header merge** (`upgrade_request`, `src/CodeFlow.App/ApiClient/WebSocketStream.cs`): the **first** row for a given header name
  *replaces* whatever the handshake auto-generated (`Host`, `Origin`, …); a **repeated** name
  *appends* instead of replacing, because a header the caller typed twice was typed twice on
  purpose. Header name matching is on the parsed `HeaderName`, not the caller's original casing.
  `subprotocols` (trimmed, empties dropped) become one comma-joined `Sec-WebSocket-Protocol` header
  if the list is non-empty.
- **TLS**: `verify_ssl: true` uses tokio-tungstenite's default connector (system roots, full
  validation). `verify_ssl: false` installs `AcceptAnyServerCert` (`src/CodeFlow.App/ApiClient/WebSocketStream.cs`), which accepts
  any certificate chain and any server name **but still verifies the TLS12/13 handshake signature**
  via the real crypto provider (crypto.verify_tls12_signature`/`verify_tls13_signature`)
  — the comment at `src/CodeFlow.App/ApiClient/WebSocketStream.cs` states the intent explicitly: *"Signature checking stays intact
  so the handshake still fails on a genuinely broken peer rather than on a name mismatch alone."*
  See `BUG-API-d` below — MQTT and gRPC's equivalent verifiers do not do this.
- **Handshake timeout**: `options.timeout_ms == 0` waits forever; otherwise the whole handshake
  (`connect_async_tls_with_config`) is wrapped in `Task.Delay`::timeout`, erroring as `"Handshake
  timed out after {n}ms"`.
- **Ping scheduling** (`keepalive`/`tick`, `src/CodeFlow.App/ApiClient/WebSocketStream.cs`): `ping_interval_ms == 0` disables
  keepalive entirely (the `select!` arm never completes — the standard library). Otherwise the
  first tick is scheduled *one full period after* `connect`, not immediately — `Task.Delay`::interval`
  fires its first tick right away by default, and pinging in the same millisecond as the handshake
  would be pure noise.
- **Frame → `StreamMessage` mapping** (`pump`, `src/CodeFlow.App/ApiClient/WebSocketStream.cs`): `Text` → `received`, plain string
  payload. `Binary` → `received`, `binary: true`, base64 payload. `Ping` → logged as a `system`
  message (`"ping received — pong sent"`) but **no reply is sent manually** — tungstenite queues and
  flushes the matching `Pong` itself on the next read, and a manual reply would *replace* that
  queued frame rather than add to it. `Pong` → `system`, `"pong received"`. `Close` → the loop
  breaks with `detail` from the close frame's reason, or `"Closed by server"` if none. A transport
  error on read or write → `status = "error"`, logged as an `error` `StreamMessage`, loop breaks.
  `WsCommand.Close` from the command side → sends a WS close frame, `detail = "Closed by client"`.
- **On any exit path**: the writer is closed, the connection is removed from `ApiRegistry`
  (`unregister`, deliberately *not* `ApiRegistry.close` — that would post another `Close` into a
  channel whose reader is already unwinding), and a `StreamStatusEvent` is emitted with whatever
  `status`/`detail` the loop settled on.

`WebSocketStream`, `WebSocketStream`, `WebSocketStream`, `WebSocketStream`, `WebSocketStream`, `WebSocketStream`,
`WebSocketStream` are `pub(super)` specifically so `src/CodeFlow.App/ApiClient/SocketIoFraming.cs` can reuse the same socket-opening and
event-emission machinery — see below.

## Socket.IO / Engine.IO

`src/CodeFlow.App/ApiClient/SocketIoFraming.cs`. Hand-rolled on top of `src/CodeFlow.App/ApiClient/WebSocketStream.cs`'s raw WebSocket, by design: *"There is no Socket.IO
client library here on purpose: the two wire formats involved are a dozen lines each, and pulling in
a client would mean adopting its own reconnect/backoff/ack state machine — none of which an API
console wants, because the whole point is to show the user what the socket actually did rather than
paper over it."* (`src/CodeFlow.App/ApiClient/SocketIoFraming.cs`). This is the source of `DIVERGENCE-API-a`, below.

**Two framing layers in every text frame** (`src/CodeFlow.App/ApiClient/SocketIoFraming.cs`):
`
4        2       /chat,   ["message",{"a":1}]
^        ^       ^        ^
Engine.IO|       |        Socket.IO payload (event name first)
         Socket.IO type   namespace, omitted for the root one
`

**Engine.IO packet types** (`Engine`, `decode_engine`, `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`): `0` OPEN, `1` CLOSE,
`2` PING, `3` PONG, `4` MESSAGE (body is itself a Socket.IO packet), any other leading digit →
`Other(kind, body)`, logged and ignored. An empty frame decodes to `None` and is ignored with a
system message.

**Socket.IO packet types** (constants `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`): `0` CONNECT, `1` DISCONNECT, `2` EVENT,
`3` ACK, `4` CONNECT_ERROR, `5` BINARY_EVENT, `6` BINARY_ACK.

**Packet decode** (`decode_packet`, `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`): `<kind>[<n>-][<namespace>,][<ack id>]<data>`.
- The leading digit is the kind.
- `<n>-` (attachment count) is recognised only when every character before the `-` is an ASCII
  digit — anything else containing a `-` is data, not an attachment count.
- A namespace segment is present only if what follows starts with `/`; it runs up to the next `,`,
  or to end-of-string for a namespace with no trailing payload (`"0/chat"` — CONNECT, namespace
  `/chat`, no data). No leading `/` → namespace defaults to `"/"` (root).
- Any leading run of ASCII digits after the namespace is the ack id.
- Everything left over is the raw JSON `data`.

**Frame encode** (`message_frame`, `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`): `4<kind>[<ns>,]<body>` — the namespace
segment is **omitted entirely for the root namespace** (`""` or `"/"`), never emitted as `/,`,
because that's what every server expects (verified by `root_namespace_has_no_namespace_segment`).
A non-root namespace is normalized to start with `/` if the caller didn't include it, and is always
comma-terminated.

**Event args** (`event_args`/`split_event`, `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`): outgoing, `[name, payload]` (a
JSON *value* the caller already produced — spliced in, not re-encoded, so an object payload stays
an object) or `[name]` if the payload is blank/whitespace-only; the payload, if present, is
validated as JSON before sending (`event_args("message", "{not json}")` errors). Incoming,
`split_event` inverts this: a lone remaining argument keeps its own JSON shape (`["msg","hi"]` →
payload `"hi"`, a JSON string, not `["hi"]`); several arguments become a JSON array.

**Connect body** (`connect_body`/`has_auth`, `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`): the CONNECT packet's payload is
the caller's `auth_json` **only when** the transport is v4 (Socket.IO 3/4) **and** the JSON parses
as a non-empty object — v3 has no handshake-auth payload at all (a system message is logged if the
caller supplied one anyway, `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`), and an empty `{}` is the frontend's "nothing
configured" default rather than something worth putting on the wire.

**Handshake URL** (`handshake_url`, `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`): `{scheme}://{host}{port}{path}/?EIO={3|4}&transport=websocket`,
with the caller's own query pairs preserved first and `EIO`/`transport` appended after, then the
caller's `query` list appended last. `http`/`ws` → `ws`; `https`/`wss` → `wss`; anything else errors.
The path defaults to `/socket.io` when blank, is trimmed of a trailing `/`, and is forced to start
with `/` — and is **always absolute**, replacing whatever path the original URL carried (a mount
point, not a URL suffix), matching `socket.io-client`'s own behaviour. **Only `transport=websocket`
is ever requested — there is no HTTP long-polling fallback or upgrade sequence anywhere in this
file.**

**Connection sequencing** (`connect`/`pump`/`on_frame`, `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`):
1. `connect` registers the sender, opens the raw WebSocket via `WebSocketStream` at the Engine.IO URL
   (EIO=4 for v4, EIO=3 for v3), and emits a `system` message `"websocket upgraded"` — **not** an
   `open` status yet, because the transport is up but the Socket.IO session is not: an `emit` sent
   before the server's own CONNECT acknowledgement would be dropped on the floor server-side.
2. The Engine.IO `OPEN` packet (`Engine.Open`) carries the handshake JSON: `sid` is logged; `pingInterval`
   (default 25 000 ms if absent) arms a client-side ping timer **only for v3** — v4's heartbeat is
   server-initiated (an unanswered v4 ping one ping-timeout later is a dropped connection; the
   comment at `src/CodeFlow.App/ApiClient/SocketIoFraming.cs` says so explicitly), so the v4 timer stays permanently disarmed.
   Immediately after logging OPEN, the client sends its own Socket.IO CONNECT packet
   (`message_frame(CONNECT, namespace, connect_body(...))`).
3. The server's own Socket.IO CONNECT reply (`on_message`, kind `CONNECT`) is what triggers the
   **`open`** `StreamStatusEvent`, with `sid` parsed from its JSON body and logged.
4. Engine.IO `PING` → immediate `PONG` reply plus a system log (server-initiated heartbeat, v4).
   Engine.IO `PONG` → logged only (client-initiated heartbeat reply, v3's own ping timer).
   Engine.IO `CLOSE` → the pump stops with `status: "closed"`.
5. Socket.IO `DISCONNECT` from the server → pump stops with `status: "closed"`. `CONNECT_ERROR` →
   logged as an `error` message (the packet's data, or `"Server refused the connection"` if blank)
   and the pump stops with `status: "error"`.
6. `WsCommand.Close` from the command side sends a Socket.IO `DISCONNECT` for the namespace, *then*
   a raw WebSocket close frame, then stops with `detail: "Closed by client"`.

**Ack correlation — `AMBIGUOUS-API-a`**: `ACK` packets (`src/CodeFlow.App/ApiClient/SocketIoFraming.cs`) have their `ack_id`
and args parsed and logged as a `system` message (`"ack {id} {args}"`), but nothing in this file (or
in `src/CodeFlow.App/ApiClient/ApiCommands.cs`) correlates that ack back to the specific `emit` call that requested it — there is
no per-`emit` ack-id allocation, no pending-ack map, no callback registry anywhere in the
transport. Whether the .NET port needs one, or whether ack correlation is meant to stay
console-only (the ack is shown, not programmatically awaited), is not determined by this layer.

**Binary attachments — `DIVERGENCE-API-b`**: a `5-`/`6-` `BINARY_EVENT`/`BINARY_ACK` packet (with
`_placeholder` markers in its JSON in place of the real bytes) is logged as a `system` message
naming its attachment count, and each binary WebSocket frame that follows it is separately emitted
as its own `system` message with a base64 payload (`src/CodeFlow.App/ApiClient/SocketIoFraming.cs`, `:347-354`) — **the
attachment is never spliced back into the placeholder it belongs to.** This is deliberate, per the
comment at `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`: *"Reassembling it into the placeholder it belongs to is more than
the console needs; showing it beats dropping it."* Preserve this — do not build real Socket.IO
binary reassembly in the port unless asked.

**`DIVERGENCE-API-a`** (module-wide, confirmed by the comment quoted above and shared by
[MQTT](#mqtt)): **there is no automatic reconnection anywhere in this transport layer.** A dropped
WebSocket, Socket.IO or MQTT connection surfaces as a `closed`/`error` `StreamStatusEvent` and stays
closed until the user explicitly calls `connect` again. In an API *testing* tool this is correct
behaviour, not a gap — silent reconnection would hide exactly the kind of instability the tool
exists to reveal. Do not add reconnect/backoff logic in the port.

## MQTT

`src/CodeFlow.App/ApiClient/MqttConnection.cs`. A live MQTT connection is **two tasks**, not one (`src/CodeFlow.App/ApiClient/MqttConnection.cs`):

1. `pump` (`src/CodeFlow.App/ApiClient/MqttConnection.cs`) drains the `MqttCommand` channel the registry exposes to the UI
   (`Publish`/`Subscribe`/`Unsubscribe`/`Close`) and calls into rumqttc's `AsyncClient`.
2. `run_v4`/`run_v5` (`src/CodeFlow.App/ApiClient/MqttConnection.cs`) owns rumqttc's `EventLoop` and turns broker traffic into
   transcript events.

They are split because the event loop is **not cancel-safe** — driving it from the same
`select!` arm as the command channel would lose packets whenever the other arm won a race
(`src/CodeFlow.App/ApiClient/MqttConnection.cs`).

**3.1.1 and 5.0 are two unrelated client stacks** in the MQTT client (3.1.1 vs 5.0) —
separate `QoS`, `Packet`, `MqttOptions`, `EventLoop` types, no shared trait from the library. This
module shares what it can behind `MqttSink` (`src/CodeFlow.App/ApiClient/MqttConnection.cs`, a boxed-future trait so the pump
stays `Send`) and duplicates only what genuinely differs: the two `run_v4`/`run_v5` poll loops.

### URL parsing (`parse_endpoint`, `src/CodeFlow.App/ApiClient/MqttConnection.cs`)

| Scheme | TLS | Default port |
|---|---|---|
| *(none)*, `mqtt`, `tcp` | no | 1883 |
| `mqtts`, `ssl`, `tls` | yes | 8883 |
| `ws`, `wss` | — | **rejected**: `"MQTT over WebSocket ({scheme}://) is not supported by this build — use mqtt:// or mqtts://"` (the rumqttc `websocket` feature isn't compiled in; falling back to plain TCP would dial the wrong port and look like a hang) |
| anything else | — | rejected: `"Unsupported MQTT URL scheme '{other}://'"` |

An IPv6 host in `[::1]:1885` bracket form is unwrapped correctly; a userinfo prefix (`user@host`,
credentials pasted into the URL) is stripped and **discarded** — credentials come only from the
request's own `username`/`password` fields, never from the URL; a trailing path or query is
tolerated and ignored (MQTT has no URL-path semantics). An empty host, or a non-numeric port,
errors explicitly.

### Client identity and session

- **Client id** (`resolve_client_id`, `src/CodeFlow.App/ApiClient/MqttConnection.cs`): the caller's id, trimmed, if non-empty;
  otherwise `codeflow-{8 hex digits}` from `Random.Shared.Next()` — total length always
  `"codeflow-".len() + 8` = 17 characters. Generated because brokers routinely reject an empty
  client id outright.
- **Last will** (`last_will`, `src/CodeFlow.App/ApiClient/MqttConnection.cs`): only sent if `last_will` is `Some` **and** its
  `topic` is non-empty; an empty-topic will is treated as "no will configured".
- **QoS clamping** (`clamp_qos`/`v4_qos`/`v5_qos`, `src/CodeFlow.App/ApiClient/MqttConnection.cs`): any value `> 2` clamps to `0`
  (`AtMostOnce`), silently — the value arrives from a stored request, and a corrupt one should
  degrade rather than fail the publish/subscribe/will. Applied uniformly to publish, subscribe and
  last-will QoS.
- **Keep-alive**: v5's `set_keep_alive` **asserts** on anything under 5 seconds instead of
  returning an error, so this code raises the caller's `keep_alive_secs` to a floor of 5
  (`req.keep_alive_secs.max(5)`) before passing it in. **v4 has no such floor** — `keep_alive_secs`
  (including `0`, which MQTT 3.1.1 defines as "keepalive disabled") passes through unclamped. This
  asymmetry is a direct consequence of the v5 library's own precondition, not an independent design
  choice; document, do not "fix" it into symmetry.
- **Connection timeout**: `(options.timeout_ms / 1000).clamp(1, 3600)` seconds — v5 takes this on
  `MqttOptions`; v4 takes it on a separate `the MQTT client` assigned to
  `eventloop.network_options` after construction (the v4 API keeps connect/flush timeout off the
  options struct).
- **`MAX_PACKET_BYTES = 16 * 1024 * 1024`** (16 MiB), set on both directions for both versions —
  rumqttc's own default is 10 KB each way, which "quietly kills any realistic payload"
  (`src/CodeFlow.App/ApiClient/MqttConnection.cs`); an oversized incoming publish under the default would otherwise surface as a
  state error and drop the connection.
- **`REQUEST_CAPACITY = 64`** — depth of rumqttc's internal outgoing-request queue.

### TLS

`tls_transport` (`src/CodeFlow.App/ApiClient/MqttConnection.cs`): `verify_ssl: true` with no `ca_cert_path` uses rumqttc's
default TLS config (system roots). A configured `ca_cert_path` **replaces** the system roots
entirely rather than adding to them (`src/CodeFlow.App/ApiClient/MqttConnection.cs` — unlike the HTTP transport, which adds a
custom CA on top of the system store) because rumqttc's rustls stack isn't re-exported far enough
here to build a merged root store.

`verify_ssl: false` installs `AcceptAnyServerCert` (`src/CodeFlow.App/ApiClient/MqttConnection.cs`), which — **unlike `src/CodeFlow.App/ApiClient/WebSocketStream.cs`'s**
equivalent — returns `Ok(HandshakeSignatureValid.assertion())` unconditionally from both
`verify_tls12_signature` and `verify_tls13_signature`, skipping real signature verification
entirely rather than delegating to the crypto provider. See `BUG-API-d`.

**Ignored options are reported, not silently dropped** (`note_ignored_options`, `src/CodeFlow.App/ApiClient/MqttConnection.cs`):
if `proxy_url` or `client_cert_path` is set, a `system` `StreamMessage` says so explicitly
("Proxy is not supported for MQTT — connecting directly" / "Client certificates are not supported
for MQTT — connecting without one") before the connection attempt proceeds without them.

### Event loop (`run_v4`/`run_v5`, `src/CodeFlow.App/ApiClient/MqttConnection.cs`)

- **`ConnAck`** → `open` status (`"Connected, session resumed"` if `session_present`, else
  `"Connected"`), then every configured `subscriptions` entry is sent via a **separately spawned
  task** (`subscribe_initial`, `src/CodeFlow.App/ApiClient/MqttConnection.cs`) — deliberately off the poll loop, because
  `subscribe` waits for room in rumqttc's own request queue, and the only thing that drains that
  queue is the very event loop that would be blocked waiting for it.
- **`Publish` (incoming)** → a `received` `StreamMessage` via `received` (`src/CodeFlow.App/ApiClient/MqttConnection.cs`): UTF-8
  text if valid, otherwise base64 with `binary: true`; `qos`/`retain` always populated. v5's topic
  arrives as raw bytes and is decoded UTF-8-lossy (the spec requires UTF-8, but a non-conforming
  broker shouldn't cost the payload).
- **`SubAck`/`UnsubAck`/`PingResp`/`Disconnect` (broker-initiated)** → logged as `system` messages;
  v4's `SubAck` reason codes render as `"granted QoS {n}"`/`"rejected"`, v5's render via `{:?}` on
  the raw reason-code enum (no v4-style prose translation for v5).
- **`Event.Outgoing(Outgoing.Disconnect)`** → the poll loop breaks cleanly; everything the socket
  does after this is teardown noise, not a failure — the client itself requested the disconnect.
- **Any other poll error** → `report_error` (`src/CodeFlow.App/ApiClient/MqttConnection.cs`), which is a **no-op if `closing` is
  already `true`** (set by `pump` on `MqttCommand.Close` or channel exhaustion) — an error racing a
  deliberate shutdown must not overwrite the `closed` status with a spurious `error` one.
- **`finish`** (`src/CodeFlow.App/ApiClient/MqttConnection.cs`): announces `closed`, then removes the registry entry **only if
  it's still the same sender** (`UnboundedSender.same_channel`) — guards against a reconnect under
  the same connection id having already replaced the entry with a fresh one, which this stale
  cleanup must not clobber.

## gRPC

not implemented (deferred). No generated stubs anywhere — the service is unknown until the user picks a `.proto` or
a server answers reflection, so there is nothing to generate code from ahead of time. A `DescriptorPool`
is built at **runtime**, and every message travels as a prost_reflect.DynamicMessage through a
hand-written `Codec`. `protox` compiles `.proto` sources in pure the sidecar, so the user never needs
`protoc` on `PATH` (not implemented (deferred)).

**All four RPC shapes share one code path**: the gRPC stack (deferred)::Grpc.streaming` (`call_inner`,
not implemented (deferred)). A unary call is just a one-message request stream whose response stream yields one
message; client- and bidi-streaming work with **no second code path** — what differs is only how
`message_json` is read (JSON object vs. JSON array) going in, and how the collected response
messages are rendered (single object vs. array) coming out (not implemented (deferred)).

### Descriptor sources

- **`"proto"`** (`pool_from_proto`, not implemented (deferred)): compiles the named file with `protox`,
  given the caller's `import_paths`. The file's own parent directory is appended to the include
  list *last* — after the caller's own paths, so caller ordering stays authoritative for name
  resolution, but a lone `.proto` with no explicit imports is always openable regardless.
- **`"reflection"`** (`pool_from_reflection`, not implemented (deferred)): tries
  `grpc.reflection.v1.ServerReflection` first; if `list_services` against it fails, retries against
  `grpc.reflection.v1alpha.ServerReflection` — the only way to tell which version a server speaks
  is to ask and see whether it comes back `UNIMPLEMENTED`. Both errors are reported together if
  neither works. For every service name returned, `FileContainingSymbol` is requested and every
  distinct file collected. Because several real server implementations don't return the full
  transitive closure the spec promises for that call, **up to `MAX_DEPENDENCY_ROUNDS = 8`**
  additional rounds chase any dependency still missing from the collected set by
  `FileByFilename`, stopping early once nothing new arrives in a round.

**`AMBIGUOUS-API-b`**: if a server's dependency graph genuinely needs more than 8 rounds to close
(or never returns some named file at all), the remaining unresolved imports are **silently
dropped** — the loop simply stops after round 8, or earlier if a round adds nothing new, with no
error surfaced about which files are still missing. The resulting `DescriptorPool.from_file_descriptor_set`
call is left to fail (opaquely) if the set doesn't close over itself. Whether the cap should be
configurable, raised, or replaced with an explicit "N imports still unresolved" error is not
determined by the source.

**Reflection wire types are hand-transcribed** (`mod reflection`, not implemented (deferred)): only the
subset of `grpc/reflection/v1/reflection.proto` this module actually asks for is modelled — prost
skips the fields left out. Field/tag numbers are transcribed by hand from the spec, verified by
`reflection_messages_match_the_wire_format` against literal wire bytes (see
[Test coverage](#test-coverage)).

### Hand-written codec (`DynamicCodec`/`DynamicEncoder`/`DynamicDecoder`, not implemented (deferred))

`Encode = Decode = DynamicMessage`. Encoding needs nothing beyond the message itself (a
`DynamicMessage` carries its own descriptor); decoding needs the **output** `MessageDescriptor`
supplied by the `DynamicCodec` at construction, because the raw protobuf bytes alone don't say what
type they are. Both directions wrap `prost`'s own `encode`/`decode` and translate any error into a
the gRPC stack (deferred)::internal`.

### Example message generation

`example_message`/`example_value`/`well_known_example` (not implemented (deferred)): builds a JSON skeleton
of a message's fields for the "generate example" UI action.

- **Well-known types** (`well_known_example`, not implemented (deferred)) get a hand-picked JSON form instead
  of a field-by-field skeleton, because their real JSON mapping is nothing like their protobuf field
  layout: `Timestamp` → `"1970-01-01T00:00:00Z"`, `Duration` → `"0s"`, `FieldMask`/`StringValue`/`BytesValue`
  → `""`, `Empty`/`Struct` → `{}`, `Value` → `null`, `ListValue` → `[]`, `Any` → `{"@type": ""}`,
  `BoolValue` → `false`, `DoubleValue`/`FloatValue` → `0.0`, `Int32Value`/`Int64Value`/`UInt32Value`/`UInt64Value`
  → `0`. Any other message type falls through to field-by-field expansion.
- **Recursion cap**: `depth > 1` collapses to `{}` — one level of nested-message expansion, no more.
  A self-referential message (`Address { next: Address }`, exercised by `describes_a_proto_file`)
  would never terminate otherwise; a fully-expanded skeleton of a deeply nested type wouldn't be a
  useful starting point anyway.
- `map` fields → `{}`; `repeated` (list) fields → `[]`; enum fields → the enum's own zero-value
  name (`desc.default_value().name()`).

### Calling (`call`/`call_inner`, not implemented (deferred))

- **Timeout**: `0` = no timeout. Otherwise the *entire* call — connect, descriptor loading, RPC,
  full stream drain — is wrapped in `Task.Delay`::timeout`, because `Endpoint.timeout` alone only
  covers response *headers*; a streaming method's body could still hang indefinitely after that.
- **`authority`** overrides only the `:authority` pseudo-header tonic sends (`ep.origin(...)`) — the
  TCP/TLS connection still goes to `endpoint`. This is the mechanism for reaching a host sitting
  behind a name-based gRPC proxy without changing where the socket actually connects.
- **Metadata** (`apply_metadata`, not implemented (deferred)): a key ending in `-bin` is treated as
  base64-encoded binary metadata per gRPC's own convention and decoded before insertion (errors if
  it isn't valid base64); every other key is inserted as ASCII text metadata.
- **`message_json` interpretation** (`parse_messages`, not implemented (deferred)): a JSON array becomes
  multiple request messages; anything else becomes exactly one. **More than one message is
  rejected outright** (`"{n} messages were supplied but this method is not client-streaming"`)
  unless `method.is_client_streaming()` is true on the resolved method descriptor — the *method's
  own* declared shape decides this, not anything the caller states separately about which of the
  four RPC kinds it intends to invoke.
- **Response rendering** (`messages_to_json`, not implemented (deferred)): every response field is rendered
  even at its zero value (`SerializeOptions.skip_default_fields(false)`) so an all-zero response
  doesn't misleadingly render as `{}`. A server-streaming method's collected messages render as a
  JSON array; every other shape renders its single message (or `""` if none arrived).

**`DIVERGENCE-API-c`**: a non-OK gRPC status is returned as a **normally successful**
`GrpcResponse` (`status_code`/`status_message` populated, `message_json` from whatever partial
stream arrived) — not as a command-level `Err`. This applies uniformly whether the failure arrived
mid-stream (`stream.message()` returning `Err(status)`) or as a trailers-only failure before any
response body existed. The comment states the reasoning directly (not implemented (deferred)): *"because the
UI presents it the way it presents an HTTP 500."* A .NET port that reflexively maps a non-OK gRPC
status to a thrown exception would break this — the command must keep returning a normal, inspectable
response object.

## Connection registry and cancellation

`ApiRegistry` (`src/CodeFlow.App/Storage/Database.cs`), held as shell-managed state (`app.manage`), is two independent
maps:

- **`connections: Mutex<HashMap<string, Connection>>`** — every *live* WS/Socket.IO/MQTT
  connection. `Connection` is `Ws(sender)` | `SocketIo(sender)` (both carry the same
  `UnboundedSender<WsCommand>` type — Socket.IO reuses the WebSocket writer, only its *framing*
  differs) | `Mqtt(sender: UnboundedSender<MqttCommand>)`.
- **`cancels: Mutex<HashMap<string, `TaskCompletionSource`<()>>>`** — cancel tokens for **in-flight HTTP
  sends and gRPC calls only**. Streaming connections are never cancelled this way; they're closed
  via `connections` instead (below).

**`ApiRegistry.close(id)`** (`src/CodeFlow.App/Storage/Database.cs`): sends `WsCommand.Close`/`MqttCommand.Close` into
the connection's channel and removes it from the map; also fires and removes any pending cancel
token for the same id. **Safe on an unknown id** — a disconnect can legitimately race a connection
that already died on its own, and this is a documented no-op in that case, not an error
(`src/CodeFlow.App/Storage/Database.cs`). This is what `api_stream_disconnect` calls directly.

**`with_connection`** (`src/CodeFlow.App/Storage/Database.cs`): the matching sender is **cloned out from under the lock**
before the caller's closure runs the actual send — a full channel would otherwise deadlock the
whole registry, because the send itself would block while still holding the lock every other
command needs.

**HTTP cancellation** (`api_send_http_tracked`/`api_cancel_http`, `src/CodeFlow.App/ApiClient/ApiCommands.cs`; http.send,
`src/CodeFlow.App/ApiClient/HttpSend.cs`): a fresh `oneshot` channel is registered under `id` before the send starts. `send`
races the send future against the cancel receiver:
- Cancel fires first (`success`) → the send is abandoned, `Err("Request cancelled")`.
- The `TaskCompletionSource` is *dropped without firing* (`Err(RecvError)` on the receiver) → **treated
  as not cancelled** — the original send future, still pinned from the race, simply resumes and its
  eventual result is returned normally. `clear_cancel` alone (which only removes the map entry,
  never sends) therefore never cancels anything by itself; it only ever runs *after* the send has
  already completed, to stop a stale token from cancelling a future request reusing the same `id`.
- The send finishes first → its result is returned directly, no cancellation possible after that
  point.

**gRPC cancellation** (`api_grpc_call`, `src/CodeFlow.App/ApiClient/ApiCommands.cs`): no cooperative cancellation exists
inside `call` itself — the command handler races the call future against the cancel receiver
in a `Task.WhenAny`, and cancellation works purely because **losing that race drops the call
future**, which aborts whatever tonic stream state it was holding. `id` cleanup follows the same
register/clear pattern as HTTP.

## Rules

### API-001 One `HttpClient` per HTTP send
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`
**Behaviour**: Every `send` call builds a brand-new `HttpClient` from that request's own
`NetworkOptions` — TLS verification, client identity, CA bundle, proxy and redirect policy are all
reqwest builder-level settings that cannot be changed per-request on a shared client.
**Inputs / outputs**: `NetworkOptions` in; `HttpClient` or a `string` error (unreadable CA/cert
file, invalid proxy URL, client-build failure) out.
**Edge cases**: an empty `proxy_url`/`ca_cert_path`/`client_cert_path` (after `trim`) means "not
set", not an error.
**Frontend dependency**: none.
**Markers**: none.

### API-002 Digest client identity requires an unencrypted PEM
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`
**Behaviour**: A `.p12`/`.pfx` client certificate path is rejected with a conversion hint
(`openssl pkcs12 …`); a non-empty `client_cert_password` is rejected with a decrypt hint
(`openssl pkcs8 …`), because rustls (this build's TLS backend) accepts only an unencrypted PEM
identity.
**Inputs / outputs**: path + password in; `HttpClient` or a `string` error naming the exact
remediation command.
**Edge cases**: matching is on the lowercased file extension only, not file content sniffing.
**Frontend dependency**: none.
**Markers**: none.

### API-003 Redirect policy is custom, with a shared hop counter
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`
**Behaviour**: `follow_redirects: false` → no redirect ever followed (`Policy.none()`). Otherwise
a custom closure enforces `max_redirects` via `attempt.previous().len() > max`, and — when
`keep_auth_on_redirect` is on and the hop crosses host — stops reqwest's own following so the
caller's code can resend with credentials intact instead of letting reqwest strip them.
**Inputs / outputs**: `NetworkOptions.follow_redirects`/`max_redirects`/`keep_auth_on_redirect` in;
a the redirect policy out.
**Edge cases**: same-host hops under `keep_auth_on_redirect` are still followed automatically by
reqwest (no credential stripping happens for same-host hops regardless of the flag).
**Frontend dependency**: none.
**Markers**: none.

### API-004 Manual redirect resume, only for cross-host + keep-auth
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`, `:367-388`
**Behaviour**: `run_exchange`'s loop re-drives a hop by hand only when `manual_redirect_target`
returns `Some` — which requires `follow_redirects`, `keep_auth_on_redirect`, and a redirection
status, all true simultaneously. Every other configuration's redirect chain is fully resolved
inside reqwest before `.execute()` returns.
**Inputs / outputs**: current `Url` + response in; `Url?` (the next hop) out, or a `string`
error on a malformed `Location`.
**Edge cases**: a `Location` header with non-ASCII bytes errors explicitly rather than mangling it.
**Frontend dependency**: none.
**Markers**: none.

### API-005 Redirect method/body downgrade in the manual path
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`
**Behaviour**: In the manual resume path only (API-004), `303` always becomes `GET` with no body;
`301`/`302` becomes `GET` with no body **for any method other than `GET`/`HEAD`**; `307`/`308`
(and everything else) preserve method and body unchanged.
**Inputs / outputs**: HTTP status + current method in; possibly-changed `Method` + `with_body: bool`
out.
**Edge cases**: none — this is the entire switch.
**Frontend dependency**: none.
**Markers**: `BUG-API-a` — the adjacent comment claims this replicates "browsers … turn a redirected
POST into a bodiless GET", but the code downgrades every non-GET/HEAD method (PUT, PATCH, DELETE
included), not just POST. Suspected-correct: only POST should downgrade on 301/302. Ported as-is.

### API-006 Redirect hop cap enforcement is duplicated, not doubled
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`, `:337-343`
**Behaviour**: The automatic path checks `attempt.previous().len() > max_redirects` (reqwest's own
hop count); the manual path checks the shared `hops` vector's length against `max_redirects` before
pushing a new entry. Both read from state that grows by exactly one per accepted hop regardless of
which path handled it, so the effective budget is one unified `max_redirects` across both.
**Inputs / outputs**: hop count vs. `max_redirects` in; `…` with
`"more than {n} redirects"` / `"{method} {url} went through more than {n} redirects"` out.
**Edge cases**: the two error strings differ in wording between the two enforcement sites.
**Frontend dependency**: none.
**Markers**: none.

### API-007 Digest challenge/response round trip
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`, `:938-1109`
**Behaviour**: A send always goes out unauthenticated first. Only on `401` with a parseable
`WWW-Authenticate: Digest` challenge is a second request built with a computed `Authorization:
Digest` header, targeted at wherever the *first* attempt actually landed (`attempt.response.url()`),
not the originally-typed URL.
**Inputs / outputs**: username/password + challenge params in; `Authorization` header value out, or
an error naming the missing challenge / unsupported algorithm / unsupported qop.
**Edge cases**: a 401 with no Digest challenge at all → `"{method} {url} returned 401 but no
'WWW-Authenticate: Digest' challenge, so the digest handshake cannot continue"`.
**Frontend dependency**: none.
**Markers**: see `BUG-API-b` (API-008).

### API-008 Digest challenge parsing misses combined multi-scheme header values
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`
**Behaviour**: `digest_challenge` recognises a Digest challenge only when the literal string
`"Digest"` occupies the first six characters of a `WWW-Authenticate` header *value*. Each header
*instance* is checked independently.
**Inputs / outputs**: `HeaderMap` in; `IReadOnlyDictionary<string, string>?` (parsed challenge params) out.
**Edge cases**: a server that combines multiple schemes into one comma-separated header value
(RFC 7235-legal) is only recognised if Digest happens to be first in that value.
**Frontend dependency**: none.
**Markers**: `BUG-API-b`. Suspected-correct: split each header value on scheme boundaries before
matching. Ported as-is.

### API-009 Digest hash/session/qop selection
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`, `:1047-1072`
**Behaviour**: Hash is one of `MD5`/`MD5-sess`/`SHA-256`/`SHA-256-sess` (default `MD5` if the
challenge omits `algorithm`); `-sess` folds `nonce:cnonce` into HA1. Only `qop=auth` is implemented;
`auth-int` is never attempted even if offered. No `qop` at all falls back to the RFC 2069 form
(`H(HA1:nonce:HA2)`, no `nc`/`cnonce`/`qop`).
**Inputs / outputs**: challenge params in; the digest `response` hex string out, or an error naming
the unsupported algorithm/qop.
**Edge cases**: an unrecognised `algorithm` value fails with the four supported names listed.
**Frontend dependency**: none.
**Markers**: none. Verified against RFC 2617 §3.5 (`test-vectors/http.vectors.json#digest-rfc2617`)
and the RFC 2069 fallback (`#digest-rfc2069-fallback`).

### API-010 Digest nonce-count and cnonce are fresh every send
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`
**Behaviour**: `nc` is always the literal `"00000001"`; `cnonce` is 16 random bytes hex-encoded,
regenerated for every Digest send. There is no cross-request nonce cache — every Digest request
performs its own fresh 401 round trip.
**Inputs / outputs**: none (constant/random) in; `nc`/`cnonce` strings out.
**Edge cases**: none — `nc=1` is always valid because the nonce is always freshly issued.
**Frontend dependency**: none.
**Markers**: none.

### API-011 AWS SigV4 canonical request and signature
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`
**Behaviour**: Builds `METHOD\nURI\nQUERY\nHEADERS\n\nSIGNED_HEADERS\nPAYLOAD_HASH`, hashes it with
SHA-256, wraps it in the `AWS4-HMAC-SHA256\n{date}\n{scope}\n{hash}` string-to-sign, and signs with
the standard 4-step derived key (`"AWS4"+secret` → date → region → service → `"aws4_request"`).
**Inputs / outputs**: method/uri/query/headers/payload-hash + credentials + `amz_date` in;
`(signed_headers, signature)` out.
**Edge cases**: headers are case-normalized to lowercase names and whitespace-collapsed values
before sorting/joining.
**Frontend dependency**: none.
**Markers**: none. Byte-identical against AWS's published `get-vanilla` vector
(`test-vectors/http.vectors.json#sigv4-get-vanilla`).

### API-012 SigV4 signed-header selection
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`, `:1257-1280`
**Behaviour**: `host` is always signed first, then every caller header **except**
`accept-encoding`, `authorization`, `connection`, `content-length`, `expect`, `keep-alive`,
`proxy-authorization`, `te`, `transfer-encoding`, `user-agent`; then `x-amz-date` and
`x-amz-content-sha256` always, and `x-amz-security-token` only if a session token was supplied.
**Inputs / outputs**: request headers + session token presence in; the signed header set out.
**Edge cases**: repeated header names are merged into one comma-joined value (send order) before
being signed as a single entry.
**Frontend dependency**: none.
**Markers**: none.

### API-013 SigV4 canonical URI: S3 is single-encoded, everything else double-encoded
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`
**Behaviour**: `url.path()` (already percent-decoded once by the URL parser) is signed as-is for
`service == "s3"` (case-insensitive), and re-percent-encoded a second time for every other service.
**Inputs / outputs**: `Url` + service name in; canonical URI string out.
**Edge cases**: an empty path signs as `"/"`.
**Frontend dependency**: none.
**Markers**: none.

### API-014 SigV4 canonical query: encode then sort by encoded tuple
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`
**Behaviour**: Every query pair is percent-encoded (unreserved set, `/` not exempt), then the pairs
are sorted lexicographically as `(key, value)` tuples, then joined `key=value` with `&`.
**Inputs / outputs**: `Url` in; canonical query string out.
**Edge cases**: repeated keys are not merged, only reordered by the sort.
**Frontend dependency**: none.
**Markers**: none. Verified by `canonical_query_sorts_and_encodes`.

### API-015 SigV4 payload hash source varies by body kind
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`, `:760-777`
**Behaviour**: SHA-256 hex of the exact bytes for text/base64 bodies; a **second streaming read
pass** over the file for `body_file` bodies (`hash_file`), so the SigV4 hash and the streamed upload
never require buffering the whole file at once; the literal string `UNSIGNED-PAYLOAD` for multipart
bodies, because reqwest assembles the multipart bytes and boundary internally and they aren't
knowable at signing time.
**Inputs / outputs**: body representation in; hex SHA-256 string or `"UNSIGNED-PAYLOAD"` out.
**Edge cases**: the file hash is only computed at all when `req.auth` is `Awsv4` — plain sends never
pay for the second file pass.
**Frontend dependency**: none.
**Markers**: none.

### API-016 Body representation priority
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`
**Behaviour**: Exactly one of `body_text`, `body_base64`, `body_file`, `urlencoded`, `form_data`
wins, checked in that fixed order; nothing enforces the caller sending only one.
**Inputs / outputs**: `HttpSendRequest` in; a `PreparedBody` out.
**Edge cases**: all fields absent → an empty body with no `Content-Type`.
**Frontend dependency**: none — `HttpSendRequest`'s own doc comment states the frontend already
guarantees mutual exclusivity.
**Markers**: none.

### API-017 Multipart strips any caller Content-Type
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`, `:691-731`
**Behaviour**: When the body is multipart, any `content-type` header the caller set is discarded
before the request is built, because the boundary is generated inside `HttpClient`::Form`
and only its own generated value can delimit the body.
**Inputs / outputs**: `FormPart[]` in; a `HttpClient`::Form` + preview text out.
**Edge cases**: a file part with no `content_type` gets no MIME override, letting reqwest infer one
(or send none).
**Frontend dependency**: none.
**Markers**: none.

### API-018 File streaming chunk size
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`, `:743-758`
**Behaviour**: File-backed bodies (single-file and multipart file parts) are read and streamed in
fixed `FILE_CHUNK_BYTES = 64 * 1024`-byte reads, never buffered whole.
**Inputs / outputs**: file path in; a chunked `HttpClient` stream out.
**Edge cases**: reading a directory path errors explicitly (`"'{path}' is a directory, not a file"`)
before any streaming begins.
**Frontend dependency**: none.
**Markers**: none.

### API-019 Body preview truncation
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`, `:779-795`
**Behaviour**: Preview text is cut at `BODY_PREVIEW_LIMIT = 2048` bytes, walked backward to the
nearest valid UTF-8 char boundary, with `"… ({n} bytes total)"` appended when truncated. Non-UTF-8
bytes preview as `"<{n} bytes of binary>"`; a streamed file previews as
`"<{size} bytes streamed from {path}>"` without reading its content.
**Inputs / outputs**: raw body bytes/text in; preview `string` out.
**Edge cases**: the cut point never lands mid-character.
**Frontend dependency**: none.
**Markers**: none.

### API-020 Response size cap truncates, does not error
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`
**Behaviour**: `max_response_bytes` (default `52 428 800`, `0` = unlimited) is enforced while
streaming the response body; once the cap would be exceeded, only the remaining room is copied in
and reading stops. `size_bytes` reflects the truncated length actually kept.
**Inputs / outputs**: `Response` + cap in; `byte[]` (possibly truncated) out.
**Edge cases**: a cap of exactly the response's true length reads it whole with no truncation.
**Frontend dependency**: none.
**Markers**: none.

### API-021 Text/binary decision and charset transcoding
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`
**Behaviour**: A declared `Content-Type` decides via `is_textual_type` (prefix `text/`, suffix
`+json`/`+xml`, or an explicit list — `VERBATIM` in the [Response handling](#response-handling)
section above). With no declared type, a NUL byte in the first 4096 bytes means binary. Textual
bodies decode as lossy UTF-8, except a declared Latin-1/Windows-1252 charset, which transcodes
byte-for-byte to Unicode code points 0–255. Binary bodies produce empty `body_text` and base64
`body_base64`.
**Inputs / outputs**: raw bytes + optional `Content-Type` in; `(body_text, body_base64)` out.
**Edge cases**: a declared text type with some invalid UTF-8 bytes still decodes as text, with
U+FFFD standing in for each bad byte.
**Frontend dependency**: none.
**Markers**: `VERBATIM` on the `is_textual_type` media list and on `ADVERTISED_ENCODINGS`
(API-022).

### API-022 `ADVERTISED_ENCODINGS` mirrored in the frontend
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`; `renderer/src/lib/api/send.ts`
**Behaviour**: `const ADVERTISED_ENCODINGS: string = "gzip, br, deflate";` — reconstructed for the
console because reqwest negotiates it below the layer this code can inspect. The frontend's
`buildImplicitHeaders` embeds the identical literal, with a comment naming this constant as the
thing it must mirror.
**Inputs / outputs**: none (constant) in; `Accept-Encoding` header value out, appended to
`wire_headers` only if the built request doesn't already carry one.
**Edge cases**: none.
**Frontend dependency**: `src/lib/api/send.ts:154`, confirmed byte-identical at read time.
**Markers**: `VERBATIM`.

### API-023 Set-Cookie parsing and defaults
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`
**Behaviour**: Parses every `Set-Cookie` header instance independently; `domain` defaults to the
request URL's host, `path` defaults to `"/"` unconditionally; `Domain=` strips a leading dot;
`Max-Age` wins over `Expires` per RFC 6265.
**Inputs / outputs**: raw header value + request `Url` in; `ParsedCookie` or `None` (malformed —
missing `=`, empty name) out.
**Edge cases**: unparseable `Expires` values leave `expires: None` (reads as a session cookie)
rather than erroring.
**Frontend dependency**: none.
**Markers**: `BUG-API-c` — `path` should follow RFC 6265 §5.1.4's default-path algorithm (derived
from the request URI's own path) instead of always defaulting to `"/"`. Suspected-correct:
implement that algorithm. Ported as-is.

### API-024 No cookie jar in this layer
**Implementation**: `src/CodeFlow.App/Storage/Database.cs`; `src/CodeFlow.App/ApiClient/HttpSend.cs`
**Behaviour**: `NetworkOptions.cookies` is a pre-matched `(name, value)` list the caller supplies;
it is appended as a `Cookie` header only when the caller hasn't already set one. Matching cookies to
a URL by domain/path/expiry and persisting parsed `Set-Cookie` results back into storage happens
entirely outside these files (frontend + `ApiTreeStore`, owned by another document).
**Inputs / outputs**: pre-matched cookie list in; a `Cookie` header out.
**Edge cases**: an explicit `Cookie` header from the caller always wins over the options list.
**Frontend dependency**: `api_list_cookies`/`api_upsert_cookie`/`api_delete_cookie`/`api_clear_cookies`
forward straight into `ApiTreeStore` with no matching logic of their own (see
[Commands](#commands)).
**Markers**: none.

### API-025 WebSocket scheme normalization
**Implementation**: `src/CodeFlow.App/ApiClient/WebSocketStream.cs`
**Behaviour**: `https://` → `wss://`, `http://` → `ws://`, matched case-insensitively on the scheme
only; the rest of the URL, including its case, is preserved verbatim. Any other scheme (already
`ws`/`wss`, or unrelated) passes through unchanged.
**Inputs / outputs**: raw URL string in; normalized URL string out.
**Edge cases**: leading/trailing whitespace is trimmed first.
**Frontend dependency**: none.
**Markers**: none. Verified by `normalizes_http_schemes_preserving_case_of_the_rest`.

### API-026 WebSocket header merge: first row replaces, repeats append
**Implementation**: `src/CodeFlow.App/ApiClient/WebSocketStream.cs`
**Behaviour**: The first caller header row for a given name replaces whatever the handshake
auto-generated (`Host`, `Origin`, …); a second row with the same name appends as an additional
value instead. Non-empty `subprotocols` (trimmed) join into one `Sec-WebSocket-Protocol` header.
**Inputs / outputs**: `(name, value)` pairs + subprotocol list in; a built `Request` out, or an
error naming an invalid header name/value.
**Edge cases**: a blank header key (after trim) is silently skipped.
**Frontend dependency**: none.
**Markers**: none. Verified by `first_header_row_replaces_the_generated_one_and_repeats_append`.

### API-027 WebSocket insecure TLS keeps real signature verification
**Implementation**: `src/CodeFlow.App/ApiClient/WebSocketStream.cs`
**Behaviour**: `verify_ssl: false` accepts any certificate chain/server name but still validates the
TLS12/13 handshake signature via the real crypto provider (crypto.verify_tls12_signature`/
`verify_tls13_signature`), so a genuinely broken peer still fails the handshake.
**Inputs / outputs**: `verify_ssl` in; `Connector?` out (`None` keeps the default connector).
**Edge cases**: `verify_ssl: true` never installs this verifier at all.
**Frontend dependency**: none.
**Markers**: see `BUG-API-d` (API-039) — MQTT and gRPC's equivalent verifiers do not keep this
check.

### API-028 WebSocket keepalive scheduling
**Implementation**: `src/CodeFlow.App/ApiClient/WebSocketStream.cs`
**Behaviour**: `ping_interval_ms == 0` disables keepalive entirely. Otherwise the first ping fires
one full period *after* the handshake, not immediately (`interval_at(`Stopwatch`() + period,
period)`), because `Task.Delay`::interval`'s default immediate first tick would be pure noise right
after connecting.
**Inputs / outputs**: `ping_interval_ms` in; an `Interval?` out.
**Edge cases**: none.
**Frontend dependency**: none.
**Markers**: none.

### API-029 WebSocket frame-to-transcript mapping
**Implementation**: `src/CodeFlow.App/ApiClient/WebSocketStream.cs`
**Behaviour**: `Text`/`Binary` → `received` `StreamMessage` (binary base64-encoded, `binary: true`).
`Ping` → logged only, **no manual pong sent** (tungstenite queues and flushes its own on the next
read). `Pong` → logged only. `Close` → loop ends with the close frame's reason as `detail`, or
`"Closed by server"`. A read/write error → `error` message, loop ends.
**Inputs / outputs**: tungstenite.Message in; `StreamMessage` + loop control out.
**Edge cases**: a manual reply to a received `Ping` would *replace* tungstenite's own queued `Pong`,
not add to it — hence no manual reply.
**Frontend dependency**: none.
**Markers**: none.

### API-030 Engine.IO opcode mapping
**Implementation**: `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`
**Behaviour**: `0` OPEN, `1` CLOSE, `2` PING, `3` PONG, `4` MESSAGE (body is a nested Socket.IO
packet); any other leading digit is `Other(kind, body)`, logged and ignored. An empty frame decodes
to nothing.
**Inputs / outputs**: raw text frame in; `Engine` enum out.
**Edge cases**: none beyond the above.
**Frontend dependency**: none.
**Markers**: none. Verified by `engine_opcodes_map_to_their_packets`.

### API-031 Socket.IO packet type mapping and decode
**Implementation**: `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`, `:415-462`
**Behaviour**: `0` CONNECT, `1` DISCONNECT, `2` EVENT, `3` ACK, `4` CONNECT_ERROR, `5` BINARY_EVENT,
`6` BINARY_ACK. Decode order: kind digit, then an optional `<n>-` attachment count (only if every
preceding character is a digit), then an optional `/namespace,` segment (namespace defaults to
`/`), then an optional leading run of ack-id digits, then whatever's left is `data`.
**Inputs / outputs**: packet body string (post Engine.IO `4` prefix) in; `Packet` struct out, or an
error for an unrecognised leading digit.
**Edge cases**: `"0/chat"` (namespace with no trailing payload) decodes with empty `data`.
**Frontend dependency**: none.
**Markers**: none. Verified by `ack_and_binary_packets_keep_their_metadata`,
`namespace_without_payload_is_still_a_namespace`.

### API-032 Socket.IO frame encoding: root namespace has no segment
**Implementation**: `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`
**Behaviour**: `4<kind>[<ns>,]<body>` — the namespace segment is entirely omitted for the root
namespace (`""` or `"/"`), never emitted as `/,`. A non-root namespace is auto-prefixed with `/` if
missing and always comma-terminated.
**Inputs / outputs**: kind + namespace + body in; frame string out.
**Edge cases**: none.
**Frontend dependency**: none.
**Markers**: none. Verified by `root_namespace_has_no_namespace_segment`,
`named_namespace_is_comma_terminated`.

### API-033 Socket.IO event args encode/decode shape
**Implementation**: `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`
**Behaviour**: Outgoing, `[name, payload]` (payload spliced in as raw JSON, not re-encoded) or
`[name]` if the payload is blank; a non-JSON payload is rejected before sending. Incoming, a lone
remaining argument keeps its own JSON shape; multiple arguments become a JSON array.
**Inputs / outputs**: event name + JSON payload text in; frame body / `(name, payload)` out.
**Edge cases**: `event_args("message", "{not json}")` errors without sending anything.
**Frontend dependency**: none.
**Markers**: none. Verified by `event_with_one_argument_keeps_that_argument_shape` and three
sibling tests.

### API-034 Socket.IO CONNECT auth payload: v4-only, non-empty-object-only
**Implementation**: `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`
**Behaviour**: The CONNECT packet's body is the caller's `auth_json` only when the connection is v4
**and** the JSON parses as a non-empty object; v3 never sends an auth payload (logged if the caller
supplied one), and `{}`/blank produces an empty CONNECT body on v4 too.
**Inputs / outputs**: `auth_json` + v4 flag in; CONNECT packet body string out.
**Edge cases**: malformed JSON in `auth_json` is treated the same as "no auth" (`has_auth` only
matches a successfully-parsed non-empty object).
**Frontend dependency**: none.
**Markers**: none. Verified by `connect_carries_auth_only_on_v4_and_only_when_it_says_something`.

### API-035 Socket.IO handshake sequencing: `open` status waits for the server's CONNECT
**Implementation**: `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`, `:239-297`
**Behaviour**: `connect` emits `system: "websocket upgraded"` once the raw WebSocket is up, but the
`open` `StreamStatusEvent` fires only when the server's own Socket.IO CONNECT reply arrives — an
`emit` sent in between would be silently dropped server-side, so the status intentionally doesn't
claim readiness until it actually is ready.
**Inputs / outputs**: Engine.IO OPEN → client CONNECT sent → server CONNECT received → `open`.
**Edge cases**: a `CONNECT_ERROR` in place of the server's CONNECT reply ends the connection with
`status: "error"` instead.
**Frontend dependency**: none.
**Markers**: none.

### API-036 Socket.IO v3 vs. v4 heartbeat direction
**Implementation**: `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`, `:275-286`
**Behaviour**: v3's client-side ping timer is armed from the OPEN packet's `pingInterval` (default
25 000 ms); v4's heartbeat is entirely server-initiated and the client timer stays permanently
disarmed — an unanswered v4 `PING` from the server is answered immediately with `PONG`.
**Inputs / outputs**: OPEN packet `pingInterval` + v4 flag in; `Interval?` out.
**Edge cases**: v3's own `PONG` replies from the server are logged only, never trigger anything.
**Frontend dependency**: none.
**Markers**: none.

### API-037 Socket.IO handshake URL is absolute and websocket-only
**Implementation**: `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`
**Behaviour**: `{ws|wss}://{host}{port}{path}/?EIO={3|4}&transport=websocket`, caller query first,
then `EIO`/`transport`, then the caller's separate `query` list. `path` defaults to `/socket.io`,
always forced absolute, replacing whatever path the original URL had. Only `transport=websocket` is
ever requested — no HTTP long-polling fallback exists anywhere in this file.
**Inputs / outputs**: base URL + path + EIO version + query pairs in; handshake URL string out, or
an error for a non-`http(s)`/`ws(s)` scheme.
**Edge cases**: a bare `"ftp://"` URL errors before any socket is opened.
**Frontend dependency**: none.
**Markers**: none. Verified by `handshake_url_upgrades_the_scheme_and_keeps_existing_query`.

### API-038 No transparent reconnection anywhere in the streaming transports
**Implementation**: `src/CodeFlow.App/ApiClient/SocketIoFraming.cs` (comment); confirmed absent in `src/CodeFlow.App/ApiClient/WebSocketStream.cs`, `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`, `src/CodeFlow.App/ApiClient/MqttConnection.cs`
**Behaviour**: A dropped WebSocket, Socket.IO or MQTT connection ends with a `closed`/`error`
`StreamStatusEvent` and stays closed. Nothing reconnects automatically; the user must call
`connect` again.
**Inputs / outputs**: n/a — absence of a code path.
**Edge cases**: n/a.
**Frontend dependency**: none.
**Markers**: `DIVERGENCE-API-a` — deliberate: an API testing tool must show real connection
instability, not paper over it with a silent reconnect. Do not add reconnect/backoff logic in the
port.

### API-039 Insecure-TLS verifiers diverge in signature-check strength across transports
**Implementation**: `src/CodeFlow.App/ApiClient/WebSocketStream.cs` vs. `src/CodeFlow.App/ApiClient/MqttConnection.cs` vs. not implemented — deferred
**Behaviour**: All three transports' `verify_ssl: false` verifiers accept any certificate chain
unconditionally. WS's verifier still runs the real TLS12/13 signature check through the crypto
provider. MQTT's and gRPC's verifiers instead return an unconditional `Ok(...::assertion())` from
both signature-check methods, skipping real signature verification entirely.
**Inputs / outputs**: `verify_ssl: false` in; a `ServerCertVerifier` impl out, of two different
strengths depending on transport.
**Edge cases**: only reachable at all when `verify_ssl` is explicitly disabled — the default
(`true`) is unaffected across all three transports.
**Frontend dependency**: none.
**Markers**: `BUG-API-d`. Suspected-correct: MQTT and gRPC's insecure verifiers should delegate to
the real crypto provider the way `src/CodeFlow.App/ApiClient/WebSocketStream.cs`'s does, per that file's own stated intent ("Signature
checking stays intact so the handshake still fails on a genuinely broken peer rather than on a name
mismatch alone", `src/CodeFlow.App/ApiClient/WebSocketStream.cs`). Ported as-is.

### API-040 MQTT URL scheme, port and host parsing
**Implementation**: `src/CodeFlow.App/ApiClient/MqttConnection.cs`
**Behaviour**: See the scheme/port table in [MQTT](#mqtt). `ws://`/`wss://` are explicitly rejected
(the rumqttc `websocket` feature isn't compiled in) rather than silently falling back to plain TCP.
IPv6 bracket hosts are unwrapped; a userinfo prefix is stripped and discarded (credentials come only
from the request's own fields).
**Inputs / outputs**: broker URL string in; `Endpoint { host, port, tls }` out, or a `string` error.
**Edge cases**: an empty URL, empty host, or non-numeric port all error explicitly and distinctly.
**Frontend dependency**: none.
**Markers**: none. Verified by `parses_schemes_and_default_ports`, `rejects_what_it_cannot_do`.

### API-041 MQTT client id generation
**Implementation**: `src/CodeFlow.App/ApiClient/MqttConnection.cs`
**Behaviour**: The caller's trimmed `client_id` if non-empty; otherwise `codeflow-{8 hex digits}`
from a random `uint`, total length always 17 characters.
**Inputs / outputs**: requested id in; resolved id out.
**Edge cases**: an all-whitespace requested id counts as empty.
**Frontend dependency**: none.
**Markers**: none. Verified by `generates_a_client_id_only_when_missing` (pattern-checked, not a
literal value, since the random suffix is non-deterministic).

### API-042 MQTT QoS clamping
**Implementation**: `src/CodeFlow.App/ApiClient/MqttConnection.cs`
**Behaviour**: Any QoS value `> 2` clamps to `0` (`AtMostOnce`) silently, applied uniformly to
publish, subscribe and last-will QoS across both protocol versions.
**Inputs / outputs**: `byte` QoS in; clamped `byte`, `the MQTT client`, or MQTT 5::mqttbytes.QoS
out.
**Edge cases**: `0`, `1`, `2` pass through unchanged.
**Frontend dependency**: none.
**Markers**: none. Verified by `clamps_out_of_range_qos`.

### API-043 MQTT last-will is conditional on a non-empty topic
**Implementation**: `src/CodeFlow.App/ApiClient/MqttConnection.cs`
**Behaviour**: A `last_will` value is only forwarded to rumqttc if its `topic` is non-empty; an
empty-topic will (however the rest of it is populated) is treated as "no will configured".
**Inputs / outputs**: `MqttLastWill?` in; `&MqttLastWill?` out.
**Edge cases**: `payload`, `qos`, `retain` are not inspected for this decision, only `topic`.
**Frontend dependency**: none.
**Markers**: none.

### API-044 MQTT v4/v5 keep-alive floor asymmetry
**Implementation**: `src/CodeFlow.App/ApiClient/MqttConnection.cs`
**Behaviour**: v5's `set_keep_alive` asserts on values under 5 seconds, so this code raises
`keep_alive_secs` to `max(5)` before calling it. v4 has no such floor — including `0`, which MQTT
3.1.1 defines as "keepalive disabled" — and passes the value through unclamped.
**Inputs / outputs**: `keep_alive_secs: ulong` in; a `Duration` on the matching version's options out.
**Edge cases**: this is a consequence of rumqttc's own v5 precondition, not an independent design
choice — do not "fix" it into v4/v5 symmetry.
**Frontend dependency**: none.
**Markers**: none.

### API-045 MQTT max packet size override
**Implementation**: `src/CodeFlow.App/ApiClient/MqttConnection.cs`, `:85`, `:125`
**Behaviour**: Both directions, both protocol versions, are capped at `MAX_PACKET_BYTES = 16 * 1024
* 1024` (16 MiB), overriding rumqttc's own 10 KB default, which would otherwise drop the connection
on any realistic payload.
**Inputs / outputs**: n/a — a fixed constant applied at `MqttOptions` construction.
**Edge cases**: none.
**Frontend dependency**: none.
**Markers**: none.

### API-046 MQTT two-task architecture for cancel-safety
**Implementation**: `src/CodeFlow.App/ApiClient/MqttConnection.cs`, `:519-576`, `:628-789`
**Behaviour**: `pump` (drains the UI's `MqttCommand` channel) and `run_v4`/`run_v5` (drives
rumqttc's the event loop) run as two separately spawned tasks, because the event loop is not
cancel-safe — racing it against the command channel in one `select!` would lose packets whenever the
other arm won.
**Inputs / outputs**: n/a — a concurrency-structure rule.
**Edge cases**: none.
**Frontend dependency**: none.
**Markers**: none.

### API-047 MQTT ignored HTTP-only options are reported, not silently dropped
**Implementation**: `src/CodeFlow.App/ApiClient/MqttConnection.cs`
**Behaviour**: If `proxy_url` or `client_cert_path` is set on a connect request, a `system`
`StreamMessage` states explicitly that MQTT doesn't support it and the connection proceeds without
it — connecting directly / without a client certificate.
**Inputs / outputs**: `NetworkOptions` in; zero or more `system` transcript messages out.
**Edge cases**: both can fire on the same connect if both options are set.
**Frontend dependency**: none.
**Markers**: none.

### API-048 MQTT stale-connection guard on cleanup
**Implementation**: `src/CodeFlow.App/ApiClient/MqttConnection.cs`
**Behaviour**: When an event loop ends, `finish` removes the registry entry for its connection id
only if the stored sender is still the *same* channel (`same_channel`) — a reconnect under the same
id that already replaced the entry is left alone.
**Inputs / outputs**: connection id + this task's own sender in; registry mutation (or none) out.
**Edge cases**: a reconnect racing a slow teardown of the previous connection cannot be clobbered by
that teardown.
**Frontend dependency**: none.
**Markers**: none.

### API-049 gRPC: no generated stubs, runtime descriptor pool
**Implementation**: not implemented — deferred, `:337-362`
**Behaviour**: `.proto` sources are compiled at runtime with `protox` (pure the sidecar, no `protoc`
required) into a `DescriptorPool`; every call constructs and reads `DynamicMessage`s against that
pool rather than generated types.
**Inputs / outputs**: `.proto` path + import paths in; `DescriptorPool` or a `string` error out.
**Edge cases**: the `.proto` file's own parent directory is appended to the include list last, after
the caller's own paths.
**Frontend dependency**: none.
**Markers**: none.

### API-050 gRPC reflection version fallback
**Implementation**: not implemented — deferred, `:445-461`
**Behaviour**: `list_services` is tried against `grpc.reflection.v1.ServerReflection` first; if that
fails, `grpc.reflection.v1alpha.ServerReflection` is tried next. Both errors are reported together
only if neither succeeds.
**Inputs / outputs**: reflection channel in; `(service_name, IReadOnlyList<service>)` or a combined error out.
**Edge cases**: a server implementing only v1alpha still resolves correctly, just after one extra
round trip.
**Frontend dependency**: none.
**Markers**: none.

### API-051 gRPC reflection transitive dependency chasing
**Implementation**: not implemented — deferred
**Behaviour**: After collecting every file returned for each service's `FileContainingSymbol`, up
to `MAX_DEPENDENCY_ROUNDS = 8` further rounds request any still-missing dependency by
`FileByFilename`, stopping early once a round adds nothing new.
**Inputs / outputs**: collected files + their `dependency` lists in; a closed (or best-effort)
`FileDescriptorSet` out.
**Edge cases**: a dependency that never resolves is silently dropped after the cap — see
`AMBIGUOUS-API-b`.
**Frontend dependency**: none.
**Markers**: `AMBIGUOUS-API-b`. Whether the cap should be configurable, raised, or replaced with an
explicit "still missing" error is not determined by the source. Do not guess a resolution.

### API-052 gRPC hand-written reflection message transcription
**Implementation**: not implemented — deferred
**Behaviour**: Only the subset of `grpc/reflection/v1/reflection.proto` this module needs is
hand-transcribed as prost.Message/prost.Oneof structs with explicit tag numbers, rather than
generated from the real `.proto` file.
**Inputs / outputs**: n/a — a type-definition rule.
**Edge cases**: a mistyped tag number would silently talk past a real reflection server.
**Frontend dependency**: none.
**Markers**: none. Verified byte-for-byte against hand-computed wire bytes by
`reflection_messages_match_the_wire_format`.

### API-053 gRPC hand-written dynamic codec
**Implementation**: not implemented — deferred
**Behaviour**: `DynamicCodec`'s decoder needs the target `MessageDescriptor` supplied at
construction (raw bytes alone don't say what type they are); the encoder needs nothing beyond the
`DynamicMessage` itself, which already carries its own descriptor.
**Inputs / outputs**: `MessageDescriptor` (decode) / `DynamicMessage` (encode) in; the gRPC stack (deferred)
on failure.
**Edge cases**: none.
**Frontend dependency**: none.
**Markers**: none.

### API-054 gRPC example message generation: well-known types and recursion cap
**Implementation**: not implemented — deferred
**Behaviour**: A hand-picked JSON literal is used for each well-known protobuf type (`Timestamp`,
`Duration`, `Struct`, `Any`, the wrapper types, …) instead of expanding their fields. Any other
message expands one level of nested-message depth; anything past `depth > 1` collapses to `{}`.
**Inputs / outputs**: `MessageDescriptor` + current depth in; a JSON skeleton `Value` out.
**Edge cases**: a self-referential message (`Address { next: Address }`) terminates correctly
because of the depth cap.
**Frontend dependency**: none.
**Markers**: none. Verified by `describes_a_proto_file`.

### API-055 gRPC `authority` overrides only the `:authority` pseudo-header
**Implementation**: not implemented — deferred
**Behaviour**: A non-empty `authority` reconfigures tonic's `Endpoint.origin`, which changes the
`:authority` header sent, while the actual TCP/TLS connection still targets `endpoint`.
**Inputs / outputs**: `authority` string in; a modified `Endpoint` out, or an error for an
unparseable authority.
**Edge cases**: an empty (after trim) `authority` leaves the endpoint's own default untouched.
**Frontend dependency**: none.
**Markers**: none.

### API-056 gRPC metadata: `-bin` suffix means base64-encoded binary
**Implementation**: not implemented — deferred
**Behaviour**: A metadata key ending in `-bin` (case-insensitive after lowercasing) is decoded from
base64 and inserted as binary metadata; every other key is inserted as ASCII text metadata.
**Inputs / outputs**: `(key, value)` pairs in; populated `MetadataMap` out, or an error for invalid
base64/invalid key/value.
**Edge cases**: an empty (after trim) key is silently skipped.
**Frontend dependency**: none.
**Markers**: none.

### API-057 gRPC all four RPC shapes share one streaming call
**Implementation**: not implemented — deferred, `:690-791`
**Behaviour**: Every call — unary, client-streaming, server-streaming, bidi — goes through
the gRPC stack (deferred)::Grpc.streaming`. What differs is only how `message_json` is read (JSON object vs.
array, gated by `method.is_client_streaming()`) and how the response is rendered (single value vs.
array, gated by `method.is_server_streaming()`).
**Inputs / outputs**: `GrpcCallRequest` in; `GrpcResponse` out.
**Edge cases**: more than one JSON message supplied for a non-client-streaming method is rejected
before any network call is made.
**Frontend dependency**: none.
**Markers**: none.

### API-058 gRPC timeout wraps the entire call, not just headers
**Implementation**: not implemented — deferred
**Behaviour**: `Endpoint.timeout` alone only covers response headers; the whole `call_inner` future
(connect, descriptor loading, RPC, full stream drain) is separately wrapped in
`Task.Delay`::timeout` for any non-zero `timeout_ms`.
**Inputs / outputs**: `timeout_ms` in; `GrpcResponse` out, with `"The gRPC call
timed out after {n}ms"` on expiry.
**Edge cases**: `timeout_ms == 0` means no timeout at all, including no header-level one.
**Frontend dependency**: none.
**Markers**: none.

### API-059 gRPC non-OK status is a normal response, not a command error
**Implementation**: not implemented — deferred, `:752-791`
**Behaviour**: A non-OK gRPC status — whether it arrives mid-stream or as a trailers-only failure —
populates `status_code`/`status_message`/`trailers` on an otherwise-normal `GrpcResponse`. The
the shell command never returns `Err` for a gRPC-level failure, only for calls that never reached the
protocol layer at all (bad `.proto`, unresolvable service/method, connection failure).
**Inputs / outputs**: the gRPC stack (deferred) (mid-stream or call-level) in; populated `GrpcResponse` out.
**Edge cases**: a trailers-only failure has no response headers to report — only what the status
itself carries.
**Frontend dependency**: none.
**Markers**: `DIVERGENCE-API-c` — deliberate, so the UI can present a non-OK status "the way it
presents an HTTP 500" (not implemented (deferred)). A port that maps this to a thrown exception breaks the
contract.

### API-060 Connection registry: two independent maps, one clone-under-lock rule
**Implementation**: `src/CodeFlow.App/Storage/Database.cs`
**Behaviour**: `connections` (live WS/Socket.IO/MQTT handles) and `cancels` (in-flight HTTP/gRPC
cancel tokens) are separate maps with separate lifecycles. `with_connection` always clones the
target sender out from under the lock before using it, so a full channel can never deadlock the
registry.
**Inputs / outputs**: connection/cancel id in; cloned sender or cancel token out.
**Edge cases**: `close(id)` on an unknown id is a documented no-op, not an error — a disconnect can
legitimately race a connection that already died.
**Frontend dependency**: none.
**Markers**: none.

### API-061 HTTP/gRPC cancel-token race semantics
**Implementation**: `src/CodeFlow.App/ApiClient/HttpSend.cs`; `src/CodeFlow.App/ApiClient/ApiCommands.cs`, `:325-339`
**Behaviour**: Firing the cancel token aborts the in-flight send/call (`Err("Request cancelled")` /
`Err("Call cancelled")`). Dropping the token *without* firing it (e.g. `clear_cancel` running after
completion) is not treated as cancellation — for HTTP specifically, the original send future simply
resumes and its real result is returned.
**Inputs / outputs**: `TaskCompletionSource`<()>` raced against the send/call future in; the send/call's
own result, or the cancellation error, out.
**Edge cases**: a cancel that arrives after the send already finished is a no-op (the receiver is
gone; `let _ = tx.send(())` swallows the send failure).
**Frontend dependency**: none.
**Markers**: none.

## Test coverage

All 32 ` functions across the five files that carry them (`src/CodeFlow.App/ApiClient/ApiModels.cs`,
`src/CodeFlow.App/ApiClient/ApiCommands.cs` and their FS-adjacent behaviour carry none themselves).

| extracted case | Source | Fixture | Kind |
|---|---|---|---|
| `sigv4_signature_matches_the_get_vanilla_vector` | `src/CodeFlow.App/ApiClient/HttpSend.cs` | `http.vectors.json#sigv4-get-vanilla` | vector |
| `sigv4_headers_sign_host_date_and_payload_hash` | `src/CodeFlow.App/ApiClient/HttpSend.cs` | `http.vectors.json#sigv4-headers` | vector |
| `digest_matches_the_rfc_2617_example` | `src/CodeFlow.App/ApiClient/HttpSend.cs` | `http.vectors.json#digest-rfc2617` | vector |
| `digest_falls_back_to_the_rfc_2069_form` | `src/CodeFlow.App/ApiClient/HttpSend.cs` | `http.vectors.json#digest-rfc2069-fallback` | vector |
| `auth_params_survive_commas_inside_quotes` | `src/CodeFlow.App/ApiClient/HttpSend.cs` | `http.vectors.json#auth-params-quoted-commas` | vector |
| `canonical_query_sorts_and_encodes` | `src/CodeFlow.App/ApiClient/HttpSend.cs` | `http.vectors.json#canonical-query-sort` | vector |
| `set_cookie_defaults_to_the_request_host_and_root_path` | `src/CodeFlow.App/ApiClient/HttpSend.cs` | `http.vectors.json#cookie-defaults` | vector |
| `set_cookie_reads_domain_path_and_expiry` | `src/CodeFlow.App/ApiClient/HttpSend.cs` | `http.vectors.json#cookie-domain-path-expiry` | vector |
| `declared_text_with_an_invalid_byte_is_still_text` | `src/CodeFlow.App/ApiClient/HttpSend.cs` | `http.vectors.json#decode-invalid-byte-still-text` | vector |
| `binary_types_and_undeclared_binary_go_to_base64` | `src/CodeFlow.App/ApiClient/HttpSend.cs` | `http.vectors.json#decode-binary-to-base64` | vector |
| `undeclared_text_is_shown_and_vendor_json_counts_as_text` | `src/CodeFlow.App/ApiClient/HttpSend.cs` | `http.vectors.json#decode-undeclared-text-and-vendor-json` | vector |
| `a_declared_legacy_charset_is_transcoded_not_replaced` | `src/CodeFlow.App/ApiClient/HttpSend.cs` | `http.vectors.json#decode-latin1-transcode` | vector |
| `normalizes_http_schemes_preserving_case_of_the_rest` | `src/CodeFlow.App/ApiClient/WebSocketStream.cs` | `ws.vectors.json#normalize-scheme` | vector |
| `first_header_row_replaces_the_generated_one_and_repeats_append` | `src/CodeFlow.App/ApiClient/WebSocketStream.cs` | `ws.vectors.json#header-merge` | vector |
| `root_namespace_has_no_namespace_segment` | `src/CodeFlow.App/ApiClient/SocketIoFraming.cs` | `socketio.vectors.json#frame-root-namespace` | vector |
| `named_namespace_is_comma_terminated` | `src/CodeFlow.App/ApiClient/SocketIoFraming.cs` | `socketio.vectors.json#frame-named-namespace` | vector |
| `connect_carries_auth_only_on_v4_and_only_when_it_says_something` | `src/CodeFlow.App/ApiClient/SocketIoFraming.cs` | `socketio.vectors.json#connect-auth-v3-v4` | vector |
| `event_with_one_argument_keeps_that_argument_shape` | `src/CodeFlow.App/ApiClient/SocketIoFraming.cs` | `socketio.vectors.json#event-one-arg` | vector |
| `event_with_several_arguments_becomes_an_array` | `src/CodeFlow.App/ApiClient/SocketIoFraming.cs` | `socketio.vectors.json#event-several-args` | vector |
| `event_with_a_bare_string_payload_stays_json` | `src/CodeFlow.App/ApiClient/SocketIoFraming.cs` | `socketio.vectors.json#event-bare-string` | vector |
| `event_with_no_payload_sends_only_the_name` | `src/CodeFlow.App/ApiClient/SocketIoFraming.cs` | `socketio.vectors.json#event-no-payload` | vector |
| `event_payload_must_be_json` | `src/CodeFlow.App/ApiClient/SocketIoFraming.cs` | `socketio.vectors.json#event-payload-must-be-json` | vector |
| `engine_opcodes_map_to_their_packets` | `src/CodeFlow.App/ApiClient/SocketIoFraming.cs` | `socketio.vectors.json#engine-opcodes` | vector |
| `ack_and_binary_packets_keep_their_metadata` | `src/CodeFlow.App/ApiClient/SocketIoFraming.cs` | `socketio.vectors.json#ack-and-binary-metadata` | vector |
| `namespace_without_payload_is_still_a_namespace` | `src/CodeFlow.App/ApiClient/SocketIoFraming.cs` | `socketio.vectors.json#namespace-no-payload` | vector |
| `handshake_url_upgrades_the_scheme_and_keeps_existing_query` | `src/CodeFlow.App/ApiClient/SocketIoFraming.cs` | `socketio.vectors.json#handshake-url` | vector |
| `parses_schemes_and_default_ports` | `src/CodeFlow.App/ApiClient/MqttConnection.cs` | `mqtt.vectors.json#parse-endpoint-schemes-ports` | vector |
| `rejects_what_it_cannot_do` | `src/CodeFlow.App/ApiClient/MqttConnection.cs` | `mqtt.vectors.json#parse-endpoint-rejections` | vector |
| `generates_a_client_id_only_when_missing` | `src/CodeFlow.App/ApiClient/MqttConnection.cs` | `mqtt.vectors.json#client-id-generation` | vector |
| `clamps_out_of_range_qos` | `src/CodeFlow.App/ApiClient/MqttConnection.cs` | `mqtt.vectors.json#qos-clamping` | vector |
| `describes_a_proto_file` | not implemented (deferred) | `grpc.vectors.json#describe-proto-file` | scenario |
| `reflection_messages_match_the_wire_format` | not implemented (deferred) | `grpc.vectors.json#reflection-wire-format` | vector |

32 tests, 32 fixtures — every test is either a pure-function `vector` or a self-contained `scenario`
(the one that needs a real `.proto` file on disk, with its source embedded inline in the fixture
rather than referencing an external seed artefact, since it's 25 lines and self-describing). No
test in this domain needs a live network, a keychain, or a real broker/server, so no test here is
classified `behavioural`.

## Markers raised

**`BUG-API-a`** — Redirect 301/302 method downgrade applies to any non-GET/HEAD method, not just
POST as the adjacent comment claims. `src/CodeFlow.App/ApiClient/HttpSend.cs`. See API-005.

**`BUG-API-b`** — Digest challenge detection misses multiple auth schemes combined into one
`WWW-Authenticate` header value when Digest isn't first. `src/CodeFlow.App/ApiClient/HttpSend.cs`. See API-007, API-008.

**`BUG-API-c`** — `Set-Cookie` default path is hardcoded `"/"` instead of RFC 6265 §5.1.4's
default-path algorithm. `src/CodeFlow.App/ApiClient/HttpSend.cs`. See API-023.

**`BUG-API-d`** — MQTT's and gRPC's insecure-TLS (`verify_ssl: false`) certificate verifiers skip
real TLS12/13 signature verification entirely (unconditional `assertion()`), unlike WebSocket's
equivalent verifier, which keeps genuine signature checking via the real crypto provider.
`src/CodeFlow.App/ApiClient/MqttConnection.cs`, not implemented (deferred) vs. `src/CodeFlow.App/ApiClient/WebSocketStream.cs`. See API-027, API-039.

**`AMBIGUOUS-API-a`** — Socket.IO `ACK` packets are parsed and logged but never correlated back to
the `emit` call that requested them; no pending-ack registry exists anywhere in this transport
layer. `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`.

**`AMBIGUOUS-API-b`** — gRPC reflection's transitive-dependency chase gives up silently after
`MAX_DEPENDENCY_ROUNDS = 8`, with no error naming what's still missing. not implemented (deferred). See
API-051.

**`DIVERGENCE-API-a`** — No transparent/automatic reconnection anywhere across WebSocket,
Socket.IO or MQTT; confirmed deliberate by `src/CodeFlow.App/ApiClient/SocketIoFraming.cs`'s module comment. See API-038. Do not
add reconnect/backoff logic in the port.

**`DIVERGENCE-API-b`** — Socket.IO binary attachments are shown as separate transcript lines, never
reassembled into the placeholder packet they belong to; confirmed deliberate by
`src/CodeFlow.App/ApiClient/SocketIoFraming.cs`. See the [Socket.IO](#socket-io--engine-io) section.

**`DIVERGENCE-API-c`** — A non-OK gRPC status is returned as a normal, successful command response
(`GrpcResponse`), never as a command-level `Err`; confirmed deliberate by not implemented (deferred). See
API-059.

**`VERBATIM`** — `ADVERTISED_ENCODINGS = "gzip, br, deflate"` (`src/CodeFlow.App/ApiClient/HttpSend.cs`, mirrored at
`src/lib/api/send.ts:154`); the `is_textual_type` media-type list (`src/CodeFlow.App/ApiClient/HttpSend.cs`). Both
transcribed byte-for-byte above; see API-021, API-022.
