# Test vectors

**133 extracted test cases**, held as data rather than as code. They cover exactly the parts
where "mostly right" is failure: AWS SigV4 against Amazon's published vectors, HTTP Digest
against RFC vectors, Socket.IO/Engine.IO wire bytes, DAP framing, PR-link parsing, the
secret-scanner rules, and every AI engine's output interpretation.

Each case's inputs and expected outputs live here in a shape an xUnit `[Theory]` consumes with
`[MemberData]` — deserialise `input`, call the method, assert against `expected`.
`tests/CodeFlow.Tests/TestVectors/FixtureCatalog.cs` loads them; `FixtureCatalogTests` guards
the catalog's own integrity.

## Layout

One file per unit under test, named after it:

```
test-vectors/
├─ README.md
├─ http.vectors.json          ← src/CodeFlow.App/ApiClient/HttpSend.cs
├─ socketio.vectors.json      ← src/CodeFlow.App/ApiClient/SocketIoFraming.cs
├─ secret_scan.vectors.json   ← src/CodeFlow.App/Security/SecretScan.cs
├─ …
└─ sql/                       ← seed artefacts referenced by scenario fixtures
   └─ migrations-legacy-pre-workspace.sql
```

## One file, one or more fixtures

A file holds **either a single fixture object or an array of them**. The array form exists
because one implementation file often covers several distinct units — `HttpSend.cs` covers
SigV4, Digest and cookie parsing — and forcing one unit per file would either scatter one module
across many files or push unrelated cases under a single `unit` label.

`kind`, `unit` and `setup` always belong to the **fixture**, never to an individual case. When
one file needs both a `vector` and a `scenario`, that is two entries in the array, not one entry
with per-case kinds.

## Schema

```jsonc
{
  "$schema": "codeflow-fixture-v1",
  "sourceFile": "src/CodeFlow.App/ApiClient/HttpSend.cs",
  "sourceLines": "1354-1385",
  "extractedFrom": ["sigv4_signs_a_canonical_get"],   // stable case-group names
  "kind": "vector",                                    // "vector" | "scenario"
  "unit": "sign",                                      // what is being tested
  "cases": [
    {
      "id": "sigv4-get-vanilla",
      "input":    { /* whatever the unit takes */ },
      "expected": { /* whatever the unit returns, or {"error": "..."} */ },
      "notes": "Amazon's published get-vanilla vector, byte-identical."
    }
  ]
}
```

`extractedFrom` names must be unique per `sourceFile` — `FixtureCatalogTests` asserts it, because
two fixtures claiming the same case group means one of them is a stale copy.

## The three kinds

**`kind: "vector"` — data in, data out.** Pure functions: bytes/strings/records in,
records/bools/errors out; no filesystem, no network, no process, no keychain. This is the
majority and the high-value part. Values are **byte-identical** to the specification they came
from — a vector that has been "tidied up" is worthless, because the whole point is that it was
validated against a published specification.

**`kind: "scenario"` — data plus a seed artefact.** The test constructs a deterministic
environment first (an in-memory SQLite database, a temporary git repository, a seeded legacy
schema). The JSON carries a `setup` object naming the artefact:

```jsonc
{
  "kind": "scenario",
  "setup": { "seedSql": "sql/migrations-legacy-pre-workspace.sql" },
  "steps": ["run the full migration procedure against the seeded database"],
  "expected": { "rowCounts": { "projects": 3 }, "allProjectsHaveWorkspaceId": true }
}
```

A scenario fixture without its seed artefact is incomplete, and
`FixtureCatalogTests.Scenario_fixtures_carry_their_seed_artefact` fails on one.

**Not extracted — prose only.** The behaviour needs a real external system that cannot be faked
deterministically in a data file: the OS keychain, a live process spawn, a live socket. These are
described in the owning domain document's behaviour section together with an acceptance
checklist, and that document's test-coverage table records them as `behavioural`. Fabricating a
fixture for these would give false confidence.

Four cases are recorded as `behavioural` for that reason: the keychain round-trip needs a real OS
credential store, two debugger cases need a live `node --inspect-brk` process, and one DAP case
needs `debugpy` installed. The other 129 are extracted.

## Every case is accounted for

Each domain document carries a test-coverage table:

| Case | Implementation | Fixture | Kind |
|---|---|---|---|
| `sigv4_signs_a_canonical_get` | `src/CodeFlow.App/ApiClient/SigV4.cs` | `http.vectors.json#sigv4-get-vanilla` | vector |
| `keyring_round_trips_a_secret` | `src/CodeFlow.App/Security/CredentialStore.cs` | — | behavioural |

The sum of those tables across all documents equals 133.
