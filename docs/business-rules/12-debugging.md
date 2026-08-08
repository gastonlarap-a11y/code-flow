# 12 — Debugging

## Scope

**Not implemented — deferred out of v1.** The debug panel is mounted in the renderer and its
commands answer `unknown command`; nothing here has a counterpart in `src/CodeFlow.App/`.

This document is kept as the specification a future implementation would satisfy.

## Commands

Parameters and return types live in `01-ipc-surface.md`; this is what each call actually does.

- `debug_start` — launches `program` under Node with `--inspect-brk`, applies `breakpoints`, waits for the entry halt to be consumed, and stores the session.
- `debug_start_adapter` — spawns a DAP adapter process, completes the `initialize`/`launch`/`configurationDone` handshake, applies `breakpoints`, and stores the session.
- `debug_stop` — asks both backends to stop unconditionally; whichever has no session no-ops.
- `debug_continue` — resumes the routed backend's stopped thread/target.
- `debug_pause` — interrupts the routed backend's running target.
- `debug_step` — steps the routed backend (`over`/`into`/`out`, anything else treated as `over`).
- `debug_set_breakpoints` — re-applies the full breakpoint map to whichever backend is routed, or no-ops if nothing is running.
- `debug_properties` — expands one variable/scope by its backend-native opaque id.
- `debug_evaluate` — evaluates an expression in a paused frame, backend-routed.
- `debug_is_running` `DEAD` — see DBG-037.

## Shared contract

See DBG-001 through DBG-009.

Both backends emit through the identical the sidecar types — not implemented (deferred) imports `PausedEvent`,
`OutputEvent`, `StackFrame` and `Variable` from not implemented (deferred) rather than defining its own —
so `debug:paused`, `debug:resumed`, `debug:output` and `debug:terminated` carry the same JSON
shape regardless of producer (confirmed field-by-field below, not assumed). Backend *selection*
is frontend-driven and static at session start (`debugStore.ts:110-127`: a `DebugAdapter`
config's `command === null` means Node, anything else means DAP with that command as the
adapter binary); for every command issued *after* a session exists, the sidecar infers the
backend purely from the debugger (deferred)()` (not implemented (deferred)) — true routes to the DAP backend, false
routes unconditionally to the inspector backend, even when neither session is actually running (in which
case the inspector backend returns its own "no debug session" error).

Comparing the two producers field-by-field for `PausedEvent`/`StackFrame`: both resolve `line`
to the same 1-based convention at the point the event is built (DBG-014's frame vs DBG-030), both
default a missing/empty function name to the literal `"(anonymous)"`, and both leave `scope_id`
absent when no usable scope was found. They diverge in what `id` and `file` actually contain:
DAP's frame `id` is a stringified DAP-numeric frame id usable only with more DAP requests; CDP's
is an opaque `callFrameId` string usable only with more CDP requests. Neither is valid input to
the other backend's commands, and neither survives past the session that produced it (DAP ids
are only valid until the next `continue`/step per the DAP spec; CDP object/frame ids are
invalidated by `Debugger.resume`) — the shared `StackFrame`/`Variable` the sidecar types make these
look interchangeable across backends when they are not.

`Variable` is shape-identical (`name`, `value`, `object_id`) and both backends leave `name` blank
(`""`) for an `evaluate()` result while populating it for a `properties()` listing — confirmed,
not assumed, by reading both the debugger (deferred)/the debugger (deferred) and
the debugger (deferred)/the debugger (deferred). `value`'s *content*, however, is backend-specific by
construction: DAP passes through whatever string the adapter itself formatted (debugpy's,
netcoredbg's, etc. own stringification, untouched); CDP values are formatted client-side by
`render_value` (DBG-033). The same conceptual value (e.g. a Python vs. a Node string) can render
differently depending on which backend produced it — this is not a defect, it is inherent to one
protocol formatting server-side and the other client-side, but it means `Variable.value` is not a
byte-for-byte equivalent contract the way the rest of the shape is.

**Markers**: `BUG-DBG-a` (DBG-002), `BUG-DBG-b` (DBG-005), `BUG-DBG-c` (DBG-006),
`DIVERGENCE-DBG-a` (DBG-008).

## DAP backend

See DBG-010 through DBG-024.

Framing is HTTP-header-style, byte-length prefixed (DBG-010/DBG-011). Every request carries a
monotonically increasing `seq` from a single counter shared with fire-and-forget `notify` calls;
replies are matched back to their waiter by `request_seq` through a `pending: HashMap<long,
`TaskCompletionSource`<Value>>` (DBG-012/DBG-013). `start()` runs a fixed ten-step handshake (DBG-014)
that negotiates 1-based lines/columns up front, so nothing downstream of `initialize` ever
converts a DAP line number. Breakpoints sent during `start()` swallow their own errors while the
same request sent later through `set_breakpoints()` propagates them (DBG-015, `AMBIGUOUS-DBG-a`);
and `set_breakpoints()` only touches paths present in its input map, so a file whose last
breakpoint was just removed keeps its stale breakpoints armed on the adapter for the rest of the
session (DBG-020, `BUG-DBG-e`). A stopped thread id is remembered and reused by
`resume`/`pause`/`step`, defaulting to `1` if nothing has stopped yet (DBG-017). Assembling a
paused stack costs up to three DAP round trips, with only the top frame's scope resolved eagerly
(DBG-018).

**Markers**: `AMBIGUOUS-DBG-a` (DBG-015), `BUG-DBG-d` (DBG-016), `BUG-DBG-e` (DBG-020),
`AMBIGUOUS-DBG-b` (DBG-021), `AMBIGUOUS-DBG-c` (DBG-023).

## Node / V8 Inspector backend

See DBG-025 through DBG-036.

Node is attached rather than adapted: `--inspect-brk` forces a halt on the program's first
statement so no breakpoint that fires at import time is missed, and the session consumes that
halt itself before the UI ever hears about it (DBG-028, `DIVERGENCE-DBG-b`). The inspector's
WebSocket URL is discovered by polling `GET /json/list` for up to 5 seconds (DBG-025); once
connected, requests are numbered and correlated the same way as DAP (an `id`-keyed pending map)
but over CDP's `method`/`params` shape rather than DAP's `command`/`arguments`. Line numbers cross
the CDP boundary 0-based and are converted to/from the app's 1-based convention at exactly two
points: breakpoint lines going out (`-1`, saturating) and paused-frame lines coming back (`+1`)
(DBG-030). A file path and a `file://` URL are interconverted with explicit Windows-drive-letter
handling (DBG-029). Because `Debugger.setBreakpointByUrl` is additive (unlike DAP's per-source
replace), every breakpoint refresh clears all previously-tracked ids before resending the full set
(DBG-032) — and unlike the DAP backend, this correctly clears a file whose breakpoints were fully
removed too.

**Markers**: `BUG-DBG-f` (DBG-026), `DIVERGENCE-DBG-b` (DBG-028).

## Rules

### DBG-001 Backend selection is frontend-static at start, session-state-inferred afterward
**Implementation**: not implemented — deferred; the renderer half is `renderer/src/state/debugStore.ts:110-127`
**Behaviour**: The frontend never asks the backend "which session type is this"; it decides
which of `debug_start` / `debug_start_adapter` to invoke from a static per-language
`DebugAdapter` config (`adapter.command === null` ⇒ Node). Every command issued afterward
(`debug_continue`, `debug_pause`, `debug_step`, `debug_set_breakpoints`, `debug_properties`,
`debug_evaluate`) calls `using_adapter()`, which returns the debugger (deferred)()` — nothing else.
`true` routes the call to the DAP backend; `false` routes it to the inspector backend unconditionally, even
if no session at all is running.
**Inputs / outputs**: n/a — internal routing.
**Edge cases**: With no session running, every routed control command reaches the inspector backend
and fails with that module's own `"no debug session"` string, never DAP's.
**Frontend dependency**: `debugStore.ts` (all `debug*` action wrappers).
**Markers**: none.

### DBG-002 Starting one backend does not stop a session already running on the other
**Implementation**: not implemented — deferred (`stop().await` — DAP's own `stop`, not the debugger (deferred));
not implemented (deferred) (`stop().await` — Node's own `stop`, not the debugger (deferred)); `debugStore.ts:110-127`
(`start()` calls `debugStart`/`debugStartAdapter` directly, with no preceding `debugStop`)
**Behaviour**: the debugger (deferred)()` calls only the debugger (deferred)()` before launching; the debugger (deferred)()`
calls only the debugger (deferred)()`. Neither stops the other module's session. The frontend does not
call `debug_stop` before `start()` either. If a DAP session is active and the user launches a
Node session (or vice versa), both processes end up running concurrently, but
`using_adapter()` (DBG-001) only ever consults the debugger (deferred)()`, so DAP always wins routing:
the newly started Node session becomes permanently unreachable by `debug_continue` /
`debug_pause` / `debug_step` / `debug_set_breakpoints` / `debug_properties` / `debug_evaluate`
for as long as the DAP session lives, while its process keeps running and its
`debug:output`/`debug:paused` events keep firing into the same UI.
**Inputs / outputs**: n/a.
**Edge cases**: `debug_stop` (which does ask both backends, DBG-003) is the only command that
can reach the orphaned session — but only if the user triggers it while both are alive.
**Frontend dependency**: `debugStore.ts:110-127` (`start`).
**Markers**: `BUG-DBG-a` — suspected-correct behaviour is that the debugger (deferred)()` should also call
the debugger (deferred)()` and vice versa, mirroring `debug_stop`'s "ask both" pattern. Ported as-is.

### DBG-003 `debug_stop` is unconditional and backend-agnostic
**Implementation**: not implemented — deferred
**Behaviour**: Calls the debugger (deferred)().await` then the debugger (deferred)().await`, always both, in that
order, ignoring `using_adapter()`. Each stop is a no-op if its own module's slot is empty.
**Inputs / outputs**: `void` — always `success`; neither `stop()` fn is fallible.
**Edge cases**: none.
**Frontend dependency**: `debugStore.ts` (`stop`).
**Markers**: none.

### DBG-004 Shared payload shapes: confirmed field-by-field equivalence and its limits
**Implementation**: not implemented — deferred; not implemented — deferred
**Behaviour**: `PausedEvent { reason, frames }`, `StackFrame { id, name, file, line, scope_id }`,
`OutputEvent { kind, text }` and `Variable { name, value, object_id }` are the same the sidecar
(and therefore JSON) shape for both producers, with no ` — wire field
names are exactly the struct field names (`scope_id`, `object_id`, snake_case, confirmed
against `renderer/src/lib/ipc/commands.ts:766,773`). Both backends resolve `StackFrame.line` to the
app's 1-based convention before the event is emitted, default a missing/empty function name to
`"(anonymous)"`, and leave `Variable.name` blank for `evaluate()` results while populating it
for `properties()` results.
**Inputs / outputs**: see DBG-019 (DAP) and DBG-030/DBG-031 (Node) for the exact per-field
derivation.
**Edge cases**: `StackFrame.id`, `StackFrame.scope_id` and `Variable.object_id` are each
backend-native opaque tokens (DAP numeric-frame-id-as-string / DAP variablesReference-as-string
vs. CDP `callFrameId` / CDP `objectId`) — valid only against the backend and the specific
session that produced them, never interchangeable, and (per each protocol's own semantics) not
guaranteed valid past the next `continue`/step even within the same backend.
**Frontend dependency**: `debugStore.ts:156-176` (`selectFrame`, `expand` — pass `scope_id`/
`object_id` straight back into `debug_properties`); `debugStore.ts:178-189` (`evaluate` —
passes `frame.id` into `debug_evaluate`).
**Markers**: none.

### DBG-005 `debug:terminated` can fire twice for a single DAP session end
**Implementation**: not implemented — deferred (explicit `terminated`/`exited` event handling), not implemented — deferred
(unconditional emit after the reader loop ends)
**Behaviour**: The DAP reader loop emits `debug:terminated` when it sees an explicit
`terminated` or `exited` DAP event, and *separately* emits it again, unconditionally, once
`read_message` returns `None` (the stream closed) and the `while let` loop exits — which
happens for every session, including ones that sent an explicit `terminated`/`exited` event
first. An adapter that sends `terminated` and then closes its stdout (the common case) produces
two `debug:terminated` events for one logical termination. The Node backend has no equivalent
explicit-event path — not implemented (deferred) emits `debug:terminated` exactly once, only after
the WebSocket stream ends, since CDP has no separate termination event ("the socket closing *is*
the program ending").
**Inputs / outputs**: `debug:terminated` payload is `()` either way.
**Edge cases**: An adapter that closes its stream without ever sending `terminated`/`exited`
(e.g. it crashes) still gets exactly one emit, from the after-loop path — the double-emit only
happens on a graceful, spec-conformant shutdown.
**Frontend dependency**: `debugStore.ts:88-90` (`onDebugTerminated` resets `status`/`frames`/
`variables`/`expanded`/`selectedFrame` — idempotent against a duplicate, so the frontend
tolerates this bug, it does not depend on it).
**Markers**: `BUG-DBG-b` — suspected-correct behaviour is a single emit per session end, e.g. by
tracking whether the explicit-event path already fired before the after-loop emit runs. Ported
as-is.

### DBG-006 `debug:output` empty-line filtering diverges between backends
**Implementation**: not implemented — deferred; not implemented — deferred (`pipe_output`), not implemented — deferred
(`Runtime.consoleAPICalled`)
**Behaviour**: DAP's `output` event handler trims trailing `\n` from the adapter's `output`
text and only emits `debug:output` `if !text.is_empty()` — a blank line is silently dropped.
The Node backend's `pipe_output` (used for the debuggee's raw stdout/stderr) reads
newline-delimited lines via `BufReader.lines()` and emits every line unconditionally,
including empty ones; `Runtime.consoleAPICalled` (`console.log()` etc.) likewise applies no
emptiness check.
**Inputs / outputs**: `OutputEvent { kind, text }`; `kind` is `"stderr"`/`"stdout"`/`"log"` for
DAP (from `body.category`, default `"log"`), `"stdout"`/`"stderr"` for `pipe_output`, and the
CDP console `type` string (default `"log"`) for `Runtime.consoleAPICalled`.
**Edge cases**: A debuggee (Python/etc. via DAP) that prints a blank line produces no
`debug:output` event at all; the equivalent blank `console.log()` or blank stdout line under
Node produces a `debug:output` event with `text: ""`, which the console panel renders as a
blank row (`debugStore.ts:82-87` appends every event it receives, capped at 500 lines, with no
emptiness filter of its own).
**Frontend dependency**: `debugStore.ts:82-87` (`onDebugOutput`).
**Markers**: `BUG-DBG-c` — suspected-correct behaviour is consistent filtering (either both
suppress empty lines or neither does). Ported as-is.

### DBG-007 `Variable.value` content is backend-specific by construction
**Implementation**: not implemented — deferred (`evaluate`), not implemented — deferred (`properties`); not implemented — deferred
(`render_value`)
**Behaviour**: DAP never reformats a value — `Variable.value` is exactly the adapter's own
`"value"`/`"result"` string. The Node backend formats every value client-side through
`render_value` (DBG-033). Same shape, backend-dependent content; not a defect, a consequence of
DAP formatting server-side and CDP not.
**Inputs / outputs**: n/a — see DBG-019, DBG-022, DBG-033, DBG-034, DBG-035 for the concrete
derivations.
**Edge cases**: none beyond what's stated.
**Frontend dependency**: `debugStore.ts:184-185` (renders `result.value` directly into the
console).
**Markers**: none.

### DBG-008 Stop procedure differs: DAP asks politely first, Node kills directly
**Implementation**: not implemented — deferred; not implemented — deferred
**Behaviour**: the debugger (deferred)()` sends a fire-and-forget `disconnect` request
(`{"terminateDebuggee": true}`), sleeps 150ms, then kills the child process tree via
`AiRunRegistry`. the debugger (deferred)()` kills the child process tree immediately, with
no prior notification over the WebSocket. The not implemented (deferred) comment states the rationale directly:
`disconnect` lets the adapter kill the debuggee it started and clean up, which killing the
adapter outright would skip; CDP/V8 has no equivalent handshake need.
**Inputs / outputs**: both return no value; both are safe to call
when nothing is running (no-op via the empty slot check).
**Edge cases**: none.
**Frontend dependency**: `debugStore.ts` (`stop`).
**Markers**: `DIVERGENCE-DBG-a` — deliberate, explained in source, preserve as-is; do not
"fix" Node to also send a pre-kill notification or DAP to skip its grace period.

### DBG-009 Launch-failure error strings share a verbatim substring the frontend keys off
**Implementation**: not implemented — deferred (`format!("failed to launch the debug adapter '{command}': {e}")`);
not implemented (deferred) (`format!("failed to launch '{node_binary}': {e}")`); `debugStore.ts:128-136`
**Behaviour**: Both spawn-failure error strings contain the literal substring
`"failed to launch"`. The frontend's `start()` catch handler does
`detail.toLowerCase().includes("failed to launch")` to decide whether to append the
adapter's install hint (`adapter.install`) to the displayed error.
**Inputs / outputs**: Exact error text: DAP — `failed to launch the debug adapter '{command}':
{underlying `IOException` display}`; Node — `failed to launch '{node_binary}': {underlying
`IOException` display}`.
**Edge cases**: Any other `Err` origin in `start()` (handshake timeout, inspector-attach
failure, CDP call failure) does **not** contain this substring, so the install hint is only
ever shown for a literal process-spawn failure (binary not found, not executable, etc.), never
for a binary that launched but failed to speak the protocol correctly.
**Frontend dependency**: `debugStore.ts:128-136` (`start`) — load-bearing, must be preserved
verbatim in the port.
**Markers**: `VERBATIM` (error-string substring only; the surrounding text is not otherwise
constrained).

### DBG-010 DAP wire framing
**Implementation**: not implemented — deferred
**Behaviour**: `frame(payload: string) -> string` returns
`format!("Content-Length: {}\r\n\r\n{payload}", payload.as_bytes().len())` — an HTTP-header-style
prefix, exactly as LSP uses, with the length counted in **bytes**, not UTF-16/UTF-8 characters.
**Inputs / outputs**: see `dap.vectors.json#frame-ascii-payload`,
`dap.vectors.json#frame-utf8-payload-counts-bytes-not-characters`.
**Edge cases**: A payload containing multi-byte UTF-8 (e.g. `ñ`) produces a Content-Length
larger than the character count; get this wrong in the port and every message after the first
non-ASCII one desyncs the stream.
**Frontend dependency**: none (internal wire format).
**Markers**: none.

### DBG-011 DAP message reader: header accumulation, size cap, body read, and failure collapse
**Implementation**: not implemented — deferred
**Behaviour**: Reads the header one byte at a time into a `byte[]`, checking after every byte
whether the buffer ends with the literal 4 bytes `\r\n\r\n`; on a match, header reading stops.
If the header exceeds 8192 bytes without finding the terminator, returns `None`. The header
bytes are decoded via lossy UTF-8 decoding (invalid UTF-8 replaced with U+FFFD, not an
error), then `lines().find_map(...)` picks the **first** line whose trimmed content
case-sensitively starts with `"Content-Length:"`; if none is found, returns `None`. The parsed
length is then read via `read_exact` into a body buffer; a short read (stream closed mid-body)
returns `None`. The body is parsed with `from_slice`; a JSON error also returns
`None`.
**Inputs / outputs**: see `dap.vectors.json#read-message-single-event`,
`dap.vectors.json#read-message-two-consecutive-messages-then-eof`.
**Edge cases**: Every failure mode — EOF, oversized header, missing Content-Length header,
truncated body, malformed JSON body — collapses to the same `None` return. The caller
(`while let Some(message) = read_message(...)`) cannot distinguish "the adapter disconnected"
from "the adapter sent one malformed message and is still alive": either way the read loop
ends and `debug:terminated` fires (DBG-005).
**Frontend dependency**: none directly (internal to the read loop).
**Markers**: none.

### DBG-012 DAP request/response correlation
**Implementation**: not implemented — deferred
**Behaviour**: `Session`(command, arguments)` draws the next value from a single
`AtomicI64` counter (`next_seq`, starting at 1, `Ordering.Relaxed`), registers a
`TaskCompletionSource` under that `seq` in `pending: Mutex<HashMap<long, `TaskCompletionSource`<Value>>>`,
sends `{"seq": seq, "type": "request", "command": command, "arguments": arguments}` as a string
over the outbound channel, and awaits the oneshot. The reader task matches an incoming
`{"type": "response", ...}` message's `request_seq` against `pending`, removing and firing the
matching sender if found (silently dropping the response otherwise — e.g. a duplicate or
late-arriving reply for an already-abandoned request). On receipt, if `response.success ==
false`, the call returns `Err(message)` using `response.message`, falling back to the literal
string `"the debug adapter rejected the request"` if `message` is absent; otherwise it returns
`Ok(response.body)`, defaulting to `JsonValueKind.Null` if `body` is absent.
**Inputs / outputs**: `Value`. Failure strings: `"debug adapter is gone"` (send
into a closed channel), `"debug adapter closed before replying"` (oneshot dropped without a
value), or the adapter's own rejection message / the fallback text above.
**Edge cases**: A request whose reply never arrives (adapter hangs) awaits forever — no
per-request timeout exists at this layer.
**Frontend dependency**: propagates to every DAP-routed command's `_`.
**Markers**: none.

### DBG-013 DAP fire-and-forget `notify`
**Implementation**: not implemented — deferred
**Behaviour**: `Session`(command, arguments)` draws from the **same** `next_seq` counter
as `request`, builds the identical request envelope, and sends it — but registers no pending
entry and does not wait for a reply. Any reply the adapter sends for that `seq` is silently
dropped by the reader (no matching entry in `pending`).
**Inputs / outputs**: none (`fn notify` returns `()`).
**Edge cases**: `seq` numbers are shared across `request` and `notify`, so the sequence is not
gapless from the perspective of someone only counting awaited requests.
**Frontend dependency**: none directly; used internally for `launch` (DBG-014) and `disconnect`
(DBG-024).
**Markers**: none.

### DBG-014 DAP session start(): exact handshake ordering
**Implementation**: not implemented — deferred
**Behaviour**: In order: (1) the debugger (deferred)()` — own backend's previous session only (DBG-002);
(2) spawn `command args…` in `cwd` with piped stdin/stdout/stderr; (3) start a writer task
that frames (DBG-010) and writes every payload pulled from an unbounded channel to the child's
stdin, flushing after each write, exiting silently on the first write failure; (4) construct
the `Session` and start the reader task (see below) *before* sending anything; (5) send
`initialize` and await it (no timeout) with a fixed capability set: `clientID: "codeflow"`,
`clientName: "CodeFlow"`, `adapterID: launch.get("type").as_str().unwrap_or("debug")`,
`locale: "en"`, `linesStartAt1: true`, `columnsStartAt1: true`, `pathFormat: "path"`,
`supportsVariableType: true`, `supportsRunInTerminalRequest: false`; (6) send `launch` via
`notify` (DBG-013) — **not awaited**, because some adapters withhold the launch reply until
`configurationDone`; (7) wait up to 10 seconds for the adapter's `initialized` event
(`session.ready.notified()`), erroring with the literal string `"the debug adapter never
reported it was initialized"` on timeout; (8) for each `(path, lines)` in the breakpoints map
(HashMap iteration order — unspecified), send `setBreakpoints` with
`{"source": {"path": path}, "breakpoints": [{"line": line} for line in lines]}`, **ignoring any
error** (`let _ = ...`); (9) send `configurationDone`, **ignoring any error** too; (10) store
the session in the module slot — only now does the debugger (deferred)()` become `true`.
The reader task, running concurrently from step (4) onward, demultiplexes incoming messages by
`type`: `"response"` messages are matched by `request_seq` into `pending` (DBG-012); `"event"`
messages are matched by `event` name — `"initialized"` notifies `ready`; `"stopped"` records
`body.threadId` (default `1` if absent) into `stopped_thread`, then spawns a **separate** task
to run `collect_stack` (DBG-018) and emit `debug:paused` (spawned separately so assembling the
stack, which itself sends more requests, can't deadlock against the same reader task that would
have to deliver those requests' replies); `"continued"` emits `debug:resumed` with payload `()`;
`"output"` emits `debug:output` (DBG-006); `"terminated"`/`"exited"` emits `debug:terminated`
(DBG-005). Any other `type` (including a DAP reverse request from the adapter, e.g.
`runInTerminal`) is silently ignored — no reply is ever sent back for an adapter-initiated
request, which is spec-safe here only because `supportsRunInTerminalRequest: false` tells a
conformant adapter not to send one.
**Inputs / outputs**: `cwd: string, command: string, args: IReadOnlyList<string>, launch: Value, breakpoints:
&HashMap<string, IReadOnlyList<uint>>` → `void`.
**Edge cases**: See DBG-015 (breakpoint error asymmetry) and DBG-016 (leak on failure).
**Frontend dependency**: `debugStore.ts:118-127` (`debugStartAdapter`).
**Markers**: none directly (see child rules).

### DBG-015 Breakpoint-set errors are swallowed at start() but propagated at set_breakpoints()
**Implementation**: not implemented — deferred (`let _ = session.request("setBreakpoints", ...)` inside `start`);
not implemented (deferred) (`session.request("setBreakpoints", ...).await?` inside `set_breakpoints`)
**Behaviour**: During `start()`, a `setBreakpoints` failure for any path is discarded and the
launch proceeds regardless. Called later (e.g. the user edits breakpoints during a live
session), the identical request's failure is propagated with `?`, aborting the remaining loop
iterations (any paths not yet processed — HashMap order — never get their `setBreakpoints`
call at all) and returning `Err` to the frontend.
**Inputs / outputs**: `set_breakpoints`'s `Err` is whatever `Session` produced
(DBG-012).
**Edge cases**: none beyond the asymmetry itself.
**Frontend dependency**: `debugStore.ts:93-106` (`toggleBreakpoint` calls `debugSetBreakpoints`
and swallows its own error with `.catch(() => {})` — so even the propagated error at runtime is
invisible to the user today).
**Markers**: `AMBIGUOUS-DBG-a` — the source does not state whether swallowing errors during
`start()` (so one bad breakpoint doesn't abort session launch) while propagating them from
`set_breakpoints()` (so a live edit failure is at least returned to the caller) is a deliberate
tolerance design or an oversight. Not guessed; port as observed.

### DBG-016 DAP start() leaks the adapter/debuggee process on a post-spawn failure
**Implementation**: not implemented — deferred (spawn), not implemented — deferred (`initialize` await and the 10s ready
timeout, both fallible via `?`), not implemented (deferred) (slot only populated on success)
**Behaviour**: The child process is spawned at step (2) of DBG-014, before any handshake step
that can fail. If `initialize` returns `Err` (step 5) or the 10-second `initialized`-event wait
times out (step 7), `start()` returns `Err` immediately. `Session` has no `Drop` impl, so
nothing kills the child at that point; the module slot is never written (it's only set at step
10), so the debugger (deferred)()` reports `false` and a later the debugger (deferred)()` — which only ever acts on
whatever is in the slot — cannot find or kill this process. The reader task (which holds its
own `Arc<Session>` clone, keeping the `Session` and its `Mutex<Child?>` alive in memory)
continues running and will itself only exit when the orphaned process's stdout eventually
closes on its own.
**Inputs / outputs**: n/a — a resource leak, not a return-value defect.
**Edge cases**: A subsequent `debug_start_adapter` call runs the debugger (deferred)()` first (DBG-014 step
1), but that also only acts on the slot, which is still empty — so the previous orphan is not
stopped by a retry either, and a user retrying a failed launch can accumulate multiple orphaned
adapter processes.
**Frontend dependency**: `debugStore.ts:118-127` (`debugStartAdapter` — the frontend has no way
to know a stray process exists, since the command it awaited returned `Err`, which reads as "no
session started").
**Markers**: `BUG-DBG-d` — suspected-correct behaviour: kill the child process explicitly
before returning `Err` from any failure point in `start()` prior to the slot being populated.
Ported as-is.

### DBG-017 Stopped-thread tracking and its default
**Implementation**: not implemented — deferred (recorded on `"stopped"`), not implemented — deferred (`stopped_thread`
read), used by `resume` (not implemented (deferred)), `pause` (not implemented (deferred)), `step`
(not implemented (deferred))
**Behaviour**: The thread id from the most recent `"stopped"` event's `body.threadId` (default
`1` if the field is absent) is stored in `Session.stopped_thread`. `resume`, `pause` and `step`
all read this value (`stopped_thread(session)`, defaulting to `1` again if nothing has ever
stopped) and send it as `threadId` in their respective DAP requests.
**Inputs / outputs**: `pause()`/`resume()` send `{"threadId": thread}` with no other arguments;
`step(kind)` maps `"into"` → `stepIn`, `"out"` → `stepOut`, anything else (including `"over"`)
→ `next`, each sent as `{"threadId": thread}`.
**Edge cases**: Calling `resume`/`pause`/`step` before any `"stopped"` event has ever been seen
sends `threadId: 1` — correct for a single-threaded debuggee, an assumption for a
multi-threaded one.
**Frontend dependency**: `debugStore.ts` (`resume`, `pause`, `step`).
**Markers**: none.

### DBG-018 Assembling a paused stack: threads → stackTrace → scopes
**Implementation**: not implemented — deferred (`collect_stack`)
**Behaviour**: Requests `stackTrace` with `{"threadId": thread_id, "startFrame": 0, "levels":
20}` (at most 20 frames; deeper frames are silently unavailable), maps every returned
`stackFrame` through `parse_frame` (DBG-019). Only the **first** (top) frame then has its scope
resolved: `scopes` is requested with `{"frameId": <parsed top frame id as long>}` (skipped
entirely if the id doesn't parse as an integer), and among the returned `scopes` array the first
entry whose `name` does **not** case-insensitively equal `"globals"` is preferred, falling back
to the first scope of any name if every scope is named "globals" or the array is empty; that
scope's `variablesReference` becomes the top frame's `scope_id`. Frames below the top never get
a `scope_id` from this path — clicking a lower frame in the UI is what triggers fetching its
scope (via `debug_properties` against a frame id obtained separately, per the frontend's own
per-frame flow, not shown in these files).
**Inputs / outputs**: returns `PausedEvent { reason, frames }`.
**Edge cases**: A `stackTrace` or `scopes` request failure is swallowed (`.ok()` /
`if let Ok(...)`) and treated as "no frames" / "no scope" respectively, not as an error
surfaced anywhere.
**Frontend dependency**: `debugStore.ts:76-80` (`onDebugPaused` calls `selectFrame(0)`
immediately, which fetches `frames[0].scope_id` via `debug_properties`).
**Markers**: none.

### DBG-019 DAP `parse_frame`/`parse_variable` field mapping
**Implementation**: not implemented — deferred
**Behaviour**: `parse_frame`: `id` = `frame["id"].to_string()` (serde_json's `Value` Display —
for the number DAP always sends this is plain digits; a non-conformant string id would come out
with escaped quotes, see `dap.vectors.json#parse-frame-windows-path` notes); `name` =
`frame["name"]` or `"(anonymous)"`; `file` = `frame["source"]["path"]` or `""`, used verbatim,
no separator normalization; `line` = `frame["line"]` or `0`, **not adjusted** (the client
declared `linesStartAt1: true` at `initialize`, DBG-014 step 5); `scope_id` always `None` here
(filled in later by DBG-018). `parse_variable`: `name`/`value` straight from the adapter's own
strings (default `""` each if absent); `object_id` = `Some(variablesReference.to_string())` if
`variablesReference != 0`, else `None` (`0` is DAP's "not expandable" sentinel).
**Inputs / outputs**: see `dap.vectors.json` (`parse-frame-windows-path`,
`parse-variable-expandable`, `parse-variable-not-expandable`).
**Edge cases**: none beyond what's stated.
**Frontend dependency**: `debugStore.ts` (frame rendering, variable rendering).
**Markers**: none.

### DBG-020 `set_breakpoints` never clears a file dropped from the map
**Implementation**: not implemented — deferred; contrast not implemented — deferred (`apply_breakpoints`, DBG-032);
`debugStore.ts:93-106` (`toggleBreakpoint` deletes the map entry, `breakpoints[key]`, once its
line array is empty)
**Behaviour**: the debugger (deferred)(breakpoints)` iterates only the `(path, lines)` pairs
present in its input and sends one `setBreakpoints` request per path with that path's full
current line list — which, per the DAP spec, *replaces* all breakpoints for that source. A
path that is no longer a key in the map (because the user removed its last breakpoint,
confirmed by the frontend deleting empty entries) is never sent a `setBreakpoints` request at
all, so the adapter is never told to clear it — its previously-armed breakpoints stay live on
the adapter side for the rest of the session, even though the app's own `breakpoints` state no
longer lists them.
**Inputs / outputs**: `breakpoints: &HashMap<string, IReadOnlyList<uint>>` → `void`.
**Edge cases**: Removing the *last remaining* breakpoint in a file is exactly the case this
misses; removing one of several breakpoints in a file that still has others left works
correctly, because that file's key is still present and its (now-shorter) line list is resent.
**Frontend dependency**: `debugStore.ts:93-106` (`toggleBreakpoint`).
**Markers**: `BUG-DBG-e` — suspected-correct behaviour: track previously-sent paths (the way
not implemented (deferred)'s `breakpoint_ids` tracks CDP breakpoint ids, DBG-032) and send an empty-array
`setBreakpoints` for any path that dropped out of the new map. Ported as-is.

### DBG-021 Breakpoint paths reach the DAP adapter forward-slash-normalized
**Implementation**: not implemented — deferred (`path` would be used verbatim as `source.path`); `renderer/src/state/debugStore.ts:
18-22` (`normalizePath` replaces every `\` with `/` before storing/sending breakpoints)
**Behaviour**: The frontend normalizes every breakpoint path to forward slashes, on every
platform, before calling `debug_start_adapter`/`debug_set_breakpoints`. not implemented (deferred) sends that
string unchanged as `source.path` to the adapter — no platform-specific reconstruction, unlike
the Node backend's explicit `file_url`/`url_to_path` handling (DBG-029).
**Inputs / outputs**: n/a.
**Edge cases**: A Windows path like `C:/repo/app.py` (forward slashes) is what a DAP adapter on
Windows receives as `source.path`, not `C:\repo\app.py`.
**Frontend dependency**: `debugStore.ts:18-22` (`normalizePath`), `93-106` (`toggleBreakpoint`).
**Markers**: `AMBIGUOUS-DBG-b` — whether every DAP adapter this app can be pointed at (debugpy,
netcoredbg, rdbg, dlv dap, codelldb, java-debug) accepts a forward-slash path on Windows is a
property of those external programs, not of this source tree, and cannot be determined by
reading it. Not guessed.

### DBG-022 Step/evaluate/properties: exact request shapes and id-parsing errors
**Implementation**: not implemented — deferred
**Behaviour**: `step(kind)` — see DBG-017 for the command mapping. `properties(object_id)` —
parses `object_id` as `long`; on parse failure returns `Err("not an expandable value")` without
sending any request; on success sends `variables` with `{"variablesReference": reference}` and
maps the result array through `parse_variable` (DBG-019), defaulting to `[]` if `variables` is
absent from the body. `evaluate(frame_id, expression)` — parses `frame_id` as `long`; on parse
failure returns `Err("no frame selected")`; on success sends `evaluate` with `{"expression":
expression, "frameId": frame, "context": "repl"}` and builds a `Variable` with `name: ""`,
`value` from `body.result` (or `""`), `object_id` from `body.variablesReference` under the same
`!= 0` rule as DBG-019.
**Inputs / outputs**: `properties`: `IReadOnlyList, string>`. `evaluate`:
`Variable`.
**Edge cases**: Both id-parse errors are raised **before** any request is sent to the adapter —
a malformed id never reaches the wire.
**Frontend dependency**: `debugStore.ts:156-176` (`selectFrame`, `expand`), `178-189`
(`evaluate`).
**Markers**: none.

### DBG-023 `debug:resumed` depends on the adapter choosing to send `continued`
**Implementation**: not implemented — deferred (`"continued" => emit debug:resumed`); not implemented — deferred (`resume`
sends `continue` and returns as soon as the response arrives, without itself emitting anything)
**Behaviour**: the debugger (deferred)()` only sends the `continue` request and awaits its response; the
`debug:resumed` event is emitted solely by the reader task reacting to a `continued` **event**
from the adapter, which DAP adapters are not universally required to send in every
circumstance. The Node/V8 backend has no equivalent gap — `Debugger.resumed` is a CDP
notification paired reliably with every `Debugger.resume`/step call, per V8's own protocol
guarantee (DBG-025 area).
**Inputs / outputs**: `debug:resumed` payload is `()`.
**Edge cases**: For a DAP adapter that omits `continued`, the UI never leaves whatever state it
was in when `resume()`'s request completed — no `debug:resumed` fires, and (until the next
`stopped`/`terminated`) the frontend's `status` can remain `"paused"` while the debuggee is
actually running.
**Frontend dependency**: `debugStore.ts:81` (`onDebugResumed` — the only path that flips
`status` to `"running"`).
**Markers**: `AMBIGUOUS-DBG-c` — whether the specific adapters this app targets (debugpy,
netcoredbg, rdbg, dlv dap, codelldb, java-debug) reliably send `continued` is a property of
those external programs, not determinable from this source tree. Not guessed.

### DBG-024 DAP stop(): polite disconnect, grace period, then kill
**Implementation**: not implemented — deferred
**Behaviour**: Takes the session out of the slot (no-op if already empty). Sends `disconnect`
via `notify` (DBG-013) with `{"terminateDebuggee": true}` — not awaited. Sleeps 150ms
(hardcoded). Takes the child out of `Session.child` and calls `AiRunRegistry` on it
if present.
**Inputs / outputs**: `StopAsync()`, no return value.
**Edge cases**: If the adapter doesn't react to `disconnect` within 150ms, it is killed anyway
— the sleep is a fixed grace period, not a wait-for-acknowledgement.
**Frontend dependency**: `debugStore.ts` (`stop`).
**Markers**: see `DIVERGENCE-DBG-a` at DBG-008.

### DBG-025 Node attach: `--inspect-brk`, free-port pick, and WebSocket discovery
**Implementation**: not implemented — deferred (`ATTACH_TIMEOUT_MS = 5_000`), not implemented — deferred
(`discover_ws_url`), not implemented (deferred), not implemented (deferred) (`pick_free_port`)
**Behaviour**: A free TCP port is obtained by binding `127.0.0.1:0`, reading back the OS-chosen
port, then immediately dropping the listener (`pick_free_port`) — port `0` isn't handed to Node
directly because the inspector prints its actual port to stderr rather than reporting it
anywhere queryable, so a free port must be pre-selected here and handed to Node explicitly via
`--inspect-brk=127.0.0.1:{port}`. `discover_ws_url(port)` then polls `GET
http://127.0.0.1:{port}/json/list` every 80ms until a 5000ms deadline; on each attempt, an HTTP
error, a JSON-decode error, an empty target array, or a first target with no
`webSocketDebuggerUrl` string field all record a `last_error` string and continue polling; on
success, it takes the **first** target's `webSocketDebuggerUrl`, with no filtering by target
type or title. If the deadline passes, returns `Err(last_error)` — whichever failure was most
recently recorded. The initial value `"inspector never answered"` is only ever returned if the
loop body executes zero times, i.e. the deadline (`now + 5000ms`, computed immediately before
the loop) has already passed by the time the loop condition is first checked — not reachable in
practice, but the source does not prevent it structurally.
**Inputs / outputs**: `string` — the WebSocket URL or the last polling error.
**Edge cases**: If `discover_ws_url` fails, `start()` explicitly kills the child
(`child.kill().await`) before returning `Err(format!("could not attach to Node's inspector:
{e}"))` — the one failure path in this function that **does** clean up (contrast DBG-026).
**Frontend dependency**: `debugStore.ts:117` (`debugStart`).
**Markers**: none (see DBG-026 for the asymmetric failure path).

### DBG-026 Node start() leaks the process on failures after successful discovery
**Implementation**: not implemented — deferred (discovery failure — child killed), not implemented — deferred
(`connect_async` — no kill on failure), not implemented (deferred) (`Runtime.enable`,
`Debugger.enable`, `apply_breakpoints`, `Runtime.runIfWaitingForDebugger` — none kill on
failure), not implemented (deferred) (slot only populated on full success)
**Behaviour**: Once `discover_ws_url` succeeds, the local `child` variable is not wrapped by
anything that kills it on drop (the async runtime.Command here is not configured with
`kill_on_drop(true)`). If `connect_async` fails, `start()` returns `Err`
immediately with the local `child` simply going out of scope — the node process (already
running under `--inspect-brk`, halted, with its own stdout/stderr piped and being read by the
`pipe_output` tasks spawned at not implemented (deferred)) keeps running, unreachable: the module
slot was never written, so the debugger (deferred)()` is `false` and the debugger (deferred)()` finds
nothing. The same leak applies if any of `Runtime.enable`, `Debugger.enable`,
`apply_breakpoints`, or `Runtime.runIfWaitingForDebugger` (not implemented (deferred)) returns `Err`
after the WebSocket connected and the `Session` (holding the child in
`Mutex<Child?>`) was constructed — the `Session`, like not implemented (deferred)'s, has no `Drop` impl,
and the reader task's own `Arc<Session>` clone keeps it alive in memory without killing the
underlying process.
**Inputs / outputs**: n/a — resource leak.
**Edge cases**: This is the mirror of DBG-016 for the Node backend, and — as in DAP — a retried
`debug_start` calls the debugger (deferred)()` first (DBG-002), which also only acts on the (still
empty) slot, so a failed launch's orphan is not cleaned up by retrying either.
**Frontend dependency**: `debugStore.ts:117` (`debugStart`).
**Markers**: `BUG-DBG-f` — suspected-correct behaviour: kill the child on every failure path in
`start()` prior to the slot being populated, the way the `discover_ws_url` failure path already
does. Ported as-is.

### DBG-027 Node WebSocket session start ordering
**Implementation**: not implemented — deferred
**Behaviour**: After the WebSocket connects, the socket is split into a sink (drained by a
writer task reading from an unbounded channel) and a stream (read by the reader task, started
immediately, before any CDP call is sent — mirroring the DAP ordering in DBG-014). Then, in
order and each awaited: `Runtime.enable`, `Debugger.enable`, `apply_breakpoints` (DBG-032,
propagates any breakpoint error via `?` — no swallow-vs-propagate asymmetry here, unlike DAP's
DBG-015, because there is only one call site), `Runtime.runIfWaitingForDebugger` (releases the
`--inspect-brk` halt). Only after all four succeed is the session stored in the slot.
The reader task demultiplexes by whether the message has a numeric `id` (a response, matched
into `pending`, DBG-012-equivalent) or a `method` (a CDP notification): `Debugger.paused` —
checked against `is_entry_break` (DBG-028) first; if not the entry break, emits `debug:paused`
with `parse_paused` (DBG-030) against a snapshot of the `scripts` table (DBG-031).
`Debugger.scriptParsed` — records `scriptId → url` into the `scripts` table (DBG-031).
`Debugger.resumed` — emits `debug:resumed` with payload `()`. `Runtime.consoleAPICalled` —
emits `debug:output` (DBG-006) with `kind` from the console call's `type` (default `"log"`) and
`text` from joining every argument's `render_value(...).value` with a single space.
**Inputs / outputs**: `cwd: string, node_binary: string, program: string, args: IReadOnlyList<string>,
breakpoints: &HashMap<string, IReadOnlyList<uint>>` → `void`.
**Edge cases**: none beyond DBG-025/DBG-026/DBG-028.
**Frontend dependency**: `debugStore.ts:117` (`debugStart`).
**Markers**: none directly (see child rules).

### DBG-028 The `--inspect-brk` entry halt is consumed, never shown
**Implementation**: not implemented — deferred (`is_entry_break`), not implemented — deferred (`entry_seen`
flag and its use in the reader loop)
**Behaviour**: `--inspect-brk` unconditionally halts Node on the program's first statement —
required so a breakpoint in code that runs at import time isn't raced past, but not something
anyone asked to see. `is_entry_break(is_first, params)` returns `true` only when `is_first` is
`true` **and** `params.reason` is exactly `"Break on start"` (current V8's label) or `"other"`
(older Node's) **and** `params.hitBreakpoints` is absent or empty (`unwrap_or(true)` on a
missing array). `is_first` itself is derived from a session-scoped `AtomicBool` (`entry_seen`),
`swap`ped to `true` on the very first `Debugger.paused` the reader sees, so only that first
pause can ever qualify — a later pause with the same reason/hit-breakpoints shape (e.g. a
genuine `"other"`-reason stop) is never swallowed. When recognized, the reader sends
`Debugger.resume` directly on the outbound channel (fire-and-forget, no id tracked in
`pending`) and does **not** emit `debug:paused` for it.
**Inputs / outputs**: see `debugger.vectors.json#entry-break-recognition` for the five asserted
cases.
**Edge cases**: An exception thrown on the very first line (`reason: "exception"`, first pause)
is explicitly **not** treated as the entry break (case 5 in the vector) — it is shown to the
user like any other pause, because the whole point of the predicate is to hide only the
halt V8/Node forces, not every event that happens to arrive first.
**Frontend dependency**: none directly — this is what makes the Node backend's first visible
`debug:paused` correspond to a breakpoint/step/exception rather than to the unconditional
`--inspect-brk` halt.
**Markers**: `DIVERGENCE-DBG-b` — deliberate, extensively commented in source; preserve, do not
"fix" by surfacing the entry halt or by simplifying the first-pause detection.

### DBG-029 Path ⇄ `file://` URL conversion
**Implementation**: not implemented — deferred (`file_url`, `url_to_path`)
**Behaviour**: `file_url(path)`: backslashes are replaced with forward slashes; if the
normalized path already starts with `/` (POSIX), the result is `file://{normalized}`
(three slashes total, since `file://` + `/home/...`); otherwise (a drive-letter path) the
result is `file:///{normalized}` (three slashes inserted explicitly). `url_to_path(url)`: after
stripping a `file:///` prefix, if what remains has a `:` as its second byte (a drive letter,
e.g. `C:/...`), every `/` is converted back to `\`; otherwise a leading `/` is re-added to what
remains (restoring the POSIX absolute path). A url that doesn't start with `file:///` at all
(e.g. a `node:internal/...` runtime-internal script url) is returned unchanged.
**Inputs / outputs**: see `debugger.vectors.json` (`file-url-windows-drive-path-round-trips`,
`file-url-posix-path-round-trips`, `url-to-path-leaves-runtime-internal-urls-alone`).
**Edge cases**: none beyond what's stated; the two functions are exact inverses for the two
path shapes they're designed for (POSIX absolute, Windows drive-letter absolute) but make no
claim about UNC paths (`\\server\share\...`) or relative paths.
**Frontend dependency**: `debugStore.ts:20-22` (`normalizePath` on the frontend performs the
same backslash→forward-slash normalization independently, for its own breakpoint-keying
purposes — see DBG-021's Node-side non-issue: because `file_url` does its own normalization
too, the Node backend tolerates either separator style from the frontend).
**Markers**: none.

### DBG-030 Line-number conversion at the two CDP boundaries
**Implementation**: not implemented — deferred (paused frame, `+1`); not implemented — deferred (breakpoint set,
`saturating_sub(1)`)
**Behaviour**: Setting a breakpoint: the app's 1-based line is converted to CDP's 0-based
`lineNumber` via `line.saturating_sub(1)` — a `uint` subtraction that clamps at `0` rather than
underflowing/panicking, so an (invalid, since lines are 1-based) input of `0` is silently
treated identically to `1` on the wire. Reading a stopped frame: CDP's 0-based
`location.lineNumber` (default `0` if absent) becomes the app's 1-based `StackFrame.line` via
`+ 1`. These are the only two conversion points in the Node backend; everywhere else in the app
(breakpoint storage, `StackFrame.line` consumers) is 1-based, matching the DAP backend's
wire-native 1-based convention (DBG-019) — the app-facing contract is 1-based end-to-end
regardless of which backend is active.
**Inputs / outputs**: see `debugger.vectors.json#paused-frame-line-is-cdp-zero-based-plus-one`.
**Edge cases**: A breakpoint line of `0` (shouldn't occur given the 1-based contract, but not
validated anywhere before reaching this function) is sent to CDP identically to a breakpoint on
line `1`.
**Frontend dependency**: `debugStore.ts:32-34` (breakpoints stored "as 1-based line numbers").
**Markers**: none.

### DBG-031 `scriptId → url` table and frame-file resolution precedence
**Implementation**: not implemented — deferred (`scripts` field doc), not implemented — deferred (resolution
logic), not implemented (deferred) (table population)
**Behaviour**: On current V8, a paused call frame's own `url` field is deprecated and comes
back empty, so a `scripts: Mutex<HashMap<scriptId, url>>` table is accumulated from every
`Debugger.scriptParsed` notification (`url`, falling back to `embedderName` if `url` is empty,
skipped entirely if neither yields a non-empty string) and consulted when resolving a frame.
`parse_paused` picks the frame's own `url` field **if non-empty**, otherwise looks up
`location.scriptId` in a **snapshot** of the table (cloned under the lock at the moment
`Debugger.paused` is handled, not implemented (deferred)) and falls back to `""` if neither yields a
value; the resolved string is then passed through `url_to_path` (DBG-029).
**Inputs / outputs**: see `debugger.vectors.json#anonymous-frame-still-gets-a-display-name`
(empty `url`, no `scripts` entry → `file: ""`).
**Edge cases**: A frame whose script was parsed *after* the snapshot was taken (a race between
`Debugger.scriptParsed` and the pause it's cloned for) would resolve to `""` — not observed as
exercised by any test, so not asserted here, but a consequence of the snapshot-then-lookup
structure.
**Frontend dependency**: `debugStore.ts` (frame `file` rendering / "open in editor").
**Markers**: none.

### DBG-032 `apply_breakpoints`: clear-then-set-all, correctly handling a fully-cleared file
**Implementation**: not implemented — deferred
**Behaviour**: Every call (both from `start()`, DBG-027, and from `set_breakpoints()`) first
removes **every** previously-tracked breakpoint id (`session.breakpoint_ids`, a flat `IReadOnlyList<string>`
spanning all files, not keyed by path) via `Debugger.removeBreakpoint`, ignoring any individual
removal's error (`let _ = ...`), then sends one `Debugger.setBreakpointByUrl` call **per
individual line** (not per file) across every `(path, lines)` pair in the input map, propagating
any single call's error via `?` (aborting the remaining lines/files not yet processed). The
resulting fresh id list — only the ones that returned a `breakpointId` — replaces
`session.breakpoint_ids` in full, including when the input map is empty (clearing everything and
setting nothing).
**Inputs / outputs**: `breakpoints: &HashMap<string, IReadOnlyList<uint>>` → `void`.
**Edge cases**: Because clearing is unconditional and independent of which paths appear in the
new map, a file whose last breakpoint was removed (dropped entirely from the map, DBG-020) is
still correctly cleared here — this is the behavior DAP's `set_breakpoints` (DBG-020) lacks.
CDP's `setBreakpointByUrl` is additive per call, which is exactly why this clear-first step
exists (DAP's `setBreakpoints` is a replace-per-source primitive and needs no equivalent).
**Frontend dependency**: `debugStore.ts:93-106` (`toggleBreakpoint`).
**Markers**: none.

### DBG-033 `render_value`: CDP `RemoteObject` → one-line display string
**Implementation**: not implemented — deferred
**Behaviour**: Branches on `object.type`: `"string"` → the string wrapped in literal double
quotes (`"\"{value}\""`); `"undefined"` → the literal `"undefined"`; `"function"` → the first
line of `object.description` (falling back to the literal `"function"` if `description` is
absent, and to `"function"` again if `description` is present but empty after taking its first
line — `.lines().next().unwrap_or("function")`); `"object"` → `object.description` verbatim,
falling back to the literal `"Object"`; anything else (numbers, booleans, symbols, etc.) →
`object.value` JSON-stringified via `JsonElement`()` if present, else `object.description`
if present, else the literal `"undefined"`. `object_id` on the returned `Variable` is always
`object.objectId` if present, `None` otherwise, independent of `type`.
**Inputs / outputs**: see `debugger.vectors.json` (`render-value-string`, `render-value-number`,
`render-value-undefined`, `render-value-object-uses-description-and-keeps-object-id`).
**Edge cases**: A `"number"` value renders through the default branch, i.e. as
`JsonElement`()` of the raw JSON number — not specially formatted (no thousands separators,
no fixed precision, whatever `serde_json` produces for that number).
**Frontend dependency**: `debugStore.ts` (variables panel, console evaluation results).
**Markers**: none.

### DBG-034 Node `properties()`: getters are placeholders, never invoked
**Implementation**: not implemented — deferred
**Behaviour**: Sends `Runtime.getProperties` with `{"objectId": object_id, "ownProperties":
true, "generatePreview": false}`. For each entry in the result, if it has no `value` field
(i.e. it's an accessor property — a getter with no cached value), the entry is rendered as
`Variable { name, value: "(getter)", object_id: None }` **without calling the getter** —
explicitly to avoid invoking a getter's side effects just to populate a row the user may never
look at. Entries that do have a `value` are rendered via `render_value` (DBG-033) with `name`
overwritten from the property's own `name`.
**Inputs / outputs**: `object_id: string` → `IReadOnlyList, string>`, defaulting to `[]` if
the response has no `result` array.
**Edge cases**: A getter is indistinguishable in the UI from a plain string-valued property
literally named `"(getter)"` — the rendering has no visual marker beyond the text itself.
**Frontend dependency**: `debugStore.ts:165-176` (`expand`), `156-163` (`selectFrame`, initial
scope load).
**Markers**: none.

### DBG-035 Node `evaluate()`: exception surfacing
**Implementation**: not implemented — deferred
**Behaviour**: Sends `Debugger.evaluateOnCallFrame` with `{"callFrameId": frame_id,
"expression": expression, "returnByValue": false}`. If the response includes
`exceptionDetails`, returns `Err` with text from `exceptionDetails.exception` rendered through
`render_value` (DBG-033) — or, if that's absent, `exceptionDetails.text` — or, if both are
absent, the literal string `"evaluation failed"`. Otherwise returns `Ok(render_value(result))`.
**Inputs / outputs**: `frame_id: string, expression: string` → `Variable`.
**Edge cases**: `frame_id` is passed to CDP with no validation ahead of time (contrast DAP's
DBG-022, which parses and rejects a malformed id before sending anything) — an invalid
`callFrameId` fails only when CDP itself rejects it, surfacing as whatever error CDP returns
(via `Session`'s generic `error.message` handling, or the fallback `"CDP error"`).
**Frontend dependency**: `debugStore.ts:178-189` (`evaluate` — routes the `Err` into a
`{kind: "error"}` console line, distinct from how `resume`/`pause`/`step` errors are surfaced
into the top-level `error` panel state, DBG-001).
**Markers**: none.

### DBG-036 Node stop(): direct kill, no prior notification
**Implementation**: not implemented — deferred
**Behaviour**: Takes the session out of the slot (no-op if empty), takes the child out of
`Session.child`, and calls `AiRunRegistry` on it directly — no CDP message is sent
to the debuggee or the inspector first.
**Inputs / outputs**: `StopAsync()`, no return value.
**Edge cases**: none.
**Frontend dependency**: `debugStore.ts` (`stop`).
**Markers**: see `DIVERGENCE-DBG-a` at DBG-008.

### DBG-037 `debug_is_running` is dead
**Implementation**: not implemented — deferred
**Behaviour**: a plain synchronous check of whether either backend's slot currently holds a
session (the session handle is non-null). It is registered in the shell
command registry but has **zero** call sites in the frontend: no `invoke("debug_is_running")`
anywhere, and no TS wrapper for it exists at all in `renderer/src/lib/ipc/commands.ts` (confirmed by
`grep` — not merely "wrapper exists but unused", the wrapper itself was never written).
**Inputs / outputs**: no parameters; returns `true` iff the debugger (deferred)()` (Node slot
occupied) or the debugger (deferred)()` (DAP slot occupied) — i.e. it would have reported a session as
"running" only while it is reachable through the module slot, so it would **not** have detected
either of the orphaned-process leaks in DBG-016/DBG-026 (an unslotted process leaves both
`is_running()` checks `false` even while the process itself is still alive).
**Edge cases**: n/a — never called.
**Frontend dependency**: none — `DEAD`.
**Markers**: `DEAD`.

## Test coverage

| extracted case | Source | Fixture | Kind |
|---|---|---|---|
| `messages_are_framed_with_their_byte_length` | not implemented (deferred) | `dap.vectors.json#frame-ascii-payload`, `dap.vectors.json#frame-utf8-payload-counts-bytes-not-characters` | vector |
| `reads_back_a_framed_message` | not implemented (deferred) | `dap.vectors.json#read-message-single-event` | vector |
| `reads_consecutive_messages_without_losing_the_second` | not implemented (deferred) | `dap.vectors.json#read-message-two-consecutive-messages-then-eof` | vector |
| `frames_and_variables_map_onto_the_shared_shapes` | not implemented (deferred) | `dap.vectors.json#parse-frame-windows-path`, `dap.vectors.json#parse-variable-expandable`, `dap.vectors.json#parse-variable-not-expandable` | vector |
| `debugs_python_through_debugpy` | not implemented (deferred) | — | behavioural |
| `windows_paths_become_file_urls_and_back` | not implemented (deferred) | `debugger.vectors.json#file-url-windows-drive-path-round-trips` | vector |
| `posix_paths_survive_the_round_trip` | not implemented (deferred) | `debugger.vectors.json#file-url-posix-path-round-trips` | vector |
| `a_runtime_internal_url_is_left_alone` | not implemented (deferred) | `debugger.vectors.json#url-to-path-leaves-runtime-internal-urls-alone` | vector |
| `paused_frames_carry_one_based_lines_and_their_local_scope` | not implemented (deferred) | `debugger.vectors.json#paused-frame-line-is-cdp-zero-based-plus-one` | vector |
| `the_inspect_brk_halt_is_recognized_but_a_step_never_is` | not implemented (deferred) | `debugger.vectors.json#entry-break-recognition` | vector |
| `an_anonymous_frame_still_gets_a_name` | not implemented (deferred) | `debugger.vectors.json#anonymous-frame-still-gets-a-display-name` | vector |
| `values_render_the_way_a_variables_panel_wants_them` | not implemented (deferred) | `debugger.vectors.json#render-value-string`, `#render-value-number`, `#render-value-undefined`, `#render-value-object-uses-description-and-keeps-object-id` | vector |
| `stops_on_a_breakpoint_and_can_read_the_locals` | not implemented (deferred) | — | behavioural |
| `stepping_over_advances_one_line` | not implemented (deferred) | — | behavioural |

14 tests total (not implemented (deferred) carries none).

### Behavioural acceptance checklists

**`debugs_python_through_debugpy`** (not implemented (deferred), ` mod live_tests`, requires
`python -m debugpy` importable — skipped with a `skipping: debugpy not installed` stderr line
otherwise):
- Spawning `python -m debugpy.adapter` and completing `initialize` (with `linesStartAt1`/
  `columnsStartAt1` true) succeeds.
- A breakpoint set on line 3 of a 3-statement `add(a, b)` script (the `return total` line,
  where `total` is already bound) via `setBreakpoints` + `configurationDone` is honoured: the
  adapter reports a `stopped` event with `reason == "breakpoint"`.
- `collect_stack` against that stop yields a top frame named `add`, at line `3`, whose `file`
  equals the script's absolute path.
- The top frame's resolved `scope_id`, requested via `variables`, includes a variable named
  `total` with `value == "42"`.
- Evaluating `"a * b"` in that frame (`evaluate`, `context: "repl"`) returns `result == "80"`.
- The session tears down via a `disconnect` notify followed by a forced child kill, without
  hanging.

**`stops_on_a_breakpoint_and_can_read_the_locals`** (not implemented (deferred), ` mod
live_tests`, requires `node` on `PATH` — skipped with a `skipping: node not on PATH` stderr
line otherwise):
- `node --inspect-brk=127.0.0.1:{port}` launches and its `/json/list` endpoint becomes
  reachable within the 5-second discovery window.
- `Runtime.enable`/`Debugger.enable` succeed; a breakpoint at line 4 of a 5-line `compute`
  function (the `const doubled = value * 2;` line, where the parameter `value` is bound but the
  local `doubled` is not yet) is set via `Debugger.setBreakpointByUrl` with `lineNumber: 3`
  (0-based).
- The mandatory `--inspect-brk` entry halt is consumed (per `is_entry_break`, DBG-028) and
  never surfaces as a pause the test observes.
- The real breakpoint pause reports a top frame named `compute`, at (1-based) line `4`, whose
  `file` equals the script's absolute path.
- `Runtime.getProperties` on that frame's local scope `objectId` includes a property named
  `value` (it does not assert `doubled`'s absence, only `value`'s presence).
- `Debugger.evaluateOnCallFrame` with expression `"value + 1"` renders, through `render_value`,
  to `"22"`.

**`stepping_over_advances_one_line`** (not implemented (deferred), same module/skip condition as above):
- After the entry-break halt is consumed, the real first pause (breakpoint on line 2 of a
  4-line `let a; let b; let c = a + b; console.log(c);` script) reports line `2`.
- `Debugger.stepOver` produces a new pause whose `reason == "step"` and whose top frame is at
  line `3` — one source line further, not into any callee (there is none on that line to step
  into).

## Markers raised

| Marker | Where | One-line description |
|---|---|---|
| `BUG-DBG-a` | DBG-002 | Starting one backend doesn't stop a session already running on the other; DAP always wins routing, orphaning the other. |
| `BUG-DBG-b` | DBG-005 | DAP can emit `debug:terminated` twice for one session end (explicit event + after-loop emit); Node emits it once. |
| `BUG-DBG-c` | DBG-006 | DAP drops empty `debug:output` lines; Node's raw stdout/stderr piping and console output do not. |
| `BUG-DBG-d` | DBG-016 | DAP `start()` leaks the adapter/debuggee process if `initialize` fails or the 10s ready-wait times out. |
| `BUG-DBG-e` | DBG-020 | DAP `set_breakpoints` never clears a file whose breakpoints were fully removed from the input map. |
| `BUG-DBG-f` | DBG-026 | Node `start()` leaks the node process if the WebSocket connect or any post-connect CDP call fails, unlike the (correctly handled) discovery-failure path. |
| `AMBIGUOUS-DBG-a` | DBG-015 | Whether DAP's error-swallow-at-start vs. error-propagate-at-set_breakpoints asymmetry is deliberate is not stated in source. |
| `AMBIGUOUS-DBG-b` | DBG-021 | Whether every targeted DAP adapter tolerates forward-slash-normalized paths on Windows cannot be determined from this source tree. |
| `AMBIGUOUS-DBG-c` | DBG-023 | Whether every targeted DAP adapter reliably sends the `continued` event (so `debug:resumed` fires) is adapter-dependent, not determinable here. |
| `DIVERGENCE-DBG-a` | DBG-008 | DAP `stop()` sends a polite `disconnect` + 150ms grace before killing; Node `stop()` kills directly. Deliberate, explained in source. |
| `DIVERGENCE-DBG-b` | DBG-028 | The `--inspect-brk` entry halt is deliberately consumed and never shown as a `debug:paused` event. |
| `VERBATIM` | DBG-009 | The `"failed to launch"` substring shared by both backends' spawn-failure error strings, load-bearing for the frontend's install-hint UI. |
| `DEAD` | DBG-037 | `debug_is_running` — registered, zero call sites, no TS wrapper exists at all. |
