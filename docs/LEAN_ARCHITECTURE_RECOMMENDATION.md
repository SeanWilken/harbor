# Lean state, synchronization, and edge-agent recommendation

Status: review proposal. Response and what was adopted: see the end of this document.

This recommendation follows a review of Harbor at `98668f9`. It intentionally
reduces collection breadth, scan frequency, database churn, and overlap with
GitHub and the MyAI suite. Harbor should remain useful as a standalone product.

## Product boundary

Harbor is most valuable as:

> A lightweight operational view of machines, local repository clones, service
> readiness, meaningful drift, and explicitly requested maintenance actions.

It does not need to be a high-frequency telemetry platform, complete package
CMDB, source-control mirror, or permanent log of every refresh.

Harbor should answer:

- Which machines are available?
- What important capabilities and services are currently ready?
- Which local clones have work not represented remotely?
- Which machines or repositories changed meaningfully?
- What requires attention?
- What bounded action did the user request, and what was its result?

It should not continuously persist enough detail to reconstruct every machine
minute by minute.

## Use each source for what it knows best

### GitHub or another Git host

Use the hosting service as the source for remote facts:

- Repository existence and ownership
- Default branch
- Remote refs and accepted commits
- Releases and tags
- Pull requests and checks when enabled
- Remote activity timestamps

Fetch those facts conditionally using ETags, webhooks where practical, or a
daily/on-demand refresh. Do not copy them into a new append-only event on every
agent push.

GitHub cannot provide local-only facts:

- Uncommitted changes
- Untracked files
- Commits not yet pushed
- Local branches and worktrees
- Missing or stale clones
- Local service and resource health

Those remain Harbor agent observations.

### Harbor agent

Collect only local facts that justify an edge agent:

- Machine availability and agent version
- Capability/readiness summary
- Compact local clone state
- Dirty/ahead/behind counts
- Current branch and HEAD
- Explicitly configured service checks
- Coarse disk/memory pressure
- Optional inventory digest

Detailed paths, changed filenames, process lists, Git identity, MAC addresses,
package lists, commit subjects, and command output should be opt-in collections,
not unconditional baseline payloads.

### Harbor hub

Own:

- Enrollment and machine bindings within Harbor
- Current operational projections
- Meaningful state transitions
- Explicit administrative audit
- Bounded action delivery and results
- Recaps derived from current state and meaningful transitions

Do not use the hub as another Git host or metrics time-series database.

## State categories

Treat data differently based on why it exists.

| Category | Examples | Storage behavior |
| --- | --- | --- |
| Desired configuration | Agent name, roots, checks, collection policy | Versioned configuration, preferably Git-backed |
| Current projection | Latest machine, repo and service state | Mutable upsert/materialized view |
| Meaningful transition | clean -> dirty, service ready -> unavailable | Immutable event with deduplication |
| Administrative audit | approve, reject, request fetch, hide repo | Immutable and durable |
| Inventory artifact | Package/repository inventory with content hash | Store only on hash change; bounded history |
| Ephemeral telemetry | CPU, memory, active processes, temporary latency | Memory/ring buffer; do not persist by default |
| Remote-derived data | GitHub releases, refs, checks | Conditional cache with source timestamp/ETag |

An immutable architecture does not mean appending every sample forever. Keep
meaningful observations immutable and maintain a mutable current projection for
fast reads.

## Replace periodic full snapshots with deltas and reconciliation

Recommended agent protocol:

```text
startup
  -> send capabilities and compact full state

local change or explicit request
  -> send changed subjects only

heartbeat
  -> machine ID, agent version, health, current-state hash

daily reconciliation
  -> compact full state to repair missed events or watcher failures
```

Each subject carries a stable key and content hash:

```json
{
  "subject": "repo:github.com/myai-tech/core",
  "kind": "repository-observation",
  "schemaVersion": "1.0",
  "version": 42,
  "contentHash": "sha256:...",
  "observedAt": "...",
  "payload": {}
}
```

If the hub already acknowledges the hash, the agent sends no payload. If a
version is missed, the hub requests that subject or a bounded reconciliation.

## Adaptive synchronization cadence

The current one-minute push, five-minute full scan, hourly fetch, and six-hour
package scan are unnecessarily aggressive for mostly idle machines.

Recommended defaults:

```text
heartbeat:             5 minutes
repo reconciliation:  24 hours
package inventory:    24 hours or manual
automatic git fetch:  disabled
service checks:       5 minutes, or per-check policy
resource detail:      only while a dashboard is actively subscribed
```

Wake or refresh immediately when:

- The user opens a machine and requests fresh state
- A bounded job is queued
- A repository watcher observes relevant changes
- A configured service changes state
- The machine starts or resumes
- The network reconnects

Watchers are hints, not authority. The daily reconciliation repairs missed
events.

Avoid injecting Harbor hooks into every Git repository. Prefer a debounced
filesystem watcher plus on-demand Git inspection. Repository hooks may be an
explicit opt-in for high-value workspaces later.

## Repository state should be compact

The default repository observation should contain:

```text
remote key
HEAD OID
branch
upstream
ahead/behind
dirty counts
last fetch time
last local change time
worktree relationship
```

Do not send changed filenames, identities, commit subjects, or full local branch
catalogs unless a user requests detail and has permission.

The hub can combine this compact local observation with GitHub remote data. A
daily or user-requested refresh is enough for broad fleet status.

## Resources and processes

Harbor is not a metrics database.

Persist only threshold transitions such as:

```text
disk healthy -> warning
memory healthy -> pressure
service ready -> unavailable
machine online -> stale
```

Current CPU, memory, GPU and process data can be sampled on demand while the
machine view is open. Keep a short in-memory ring buffer if a graph is useful.
Do not persist ordinary samples or top-process lists by default.

## Inventory and recaps

Package inventory should be content-addressed:

```text
collect -> normalize -> hash
same hash -> send acknowledgement only
new hash -> store one immutable artifact
```

Retention should be explicit, for example:

```text
latest inventory:      always
changed inventories:   last 12 or 90 days
operational events:    30-90 days
administrative audit:  durable
job output:            short retention unless promoted to an artifact
recaps:                last 52 weekly reports
```

Recaps should derive from the current projection and retained meaningful events.
They should not require minute-by-minute snapshots.

## Storage recommendation

For a personal or small-fleet installation, PostgreSQL should be optional rather
than required.

Suggested tiers:

| Deployment | Storage |
| --- | --- |
| Single machine / small personal fleet | SQLite with WAL and backups |
| Larger fleet / concurrent integrations | PostgreSQL |
| Edge reporter | No database; one durable latest-state/outbox file |

Keep a storage interface so SQLite and PostgreSQL implement the same semantics.
Do not introduce SQLite if PostgreSQL is already accepted for the immediate hub
without first measuring actual complexity; the priority is reducing writes and
history, not changing databases for its own sake.

When PostgreSQL is used:

- Upsert only changed current rows
- Avoid delete/reinsert on every push
- Partition or prune retained events
- Store inventory artifacts once per content hash
- Measure WAL and autovacuum rather than estimating from row count alone

## Rust edge agent

Rust is a good fit for a future edge reporter if the protocol and collection
boundary are stabilized first.

Do not rewrite the hub or business rules merely to use Rust. Keep the F# hub and
implement a second conforming agent after the protocol is versioned.

The Rust agent should:

- Be one small native binary for `linux-arm64`, `linux-x64`, and `win-x64`
- Make outbound connections only
- Keep one durable latest-state/outbox record
- Use bounded subprocess concurrency
- Collect only configured capabilities
- Perform compact delta/hash reporting
- Refuse unknown operations locally
- Run without PostgreSQL, ASP.NET, UI assets, or migration dependencies
- Expose no listener by default
- Support a read-only local status command
- Report its build/protocol version

Pi and N100-class nodes are edge reporters, not default Harbor hubs. They should
report health, selected services, relevant repositories, storage pressure, and
capability readiness. They should not host the full database/UI stack unless a
specific deployment requires it.

The first Rust milestone should be protocol compatibility, not feature parity:

```text
hello/enrollment
heartbeat
compact machine state
configured service checks
repository summary
latest-state retry
```

Package inventory, jobs, process detail, and display capabilities can follow as
independently negotiated capabilities.

## Harbor and MyAI interoperability

Harbor remains useful without MyAI. When MyAI exists:

- Workspace Control owns desired station declarations and accepted repository
  state.
- Harbor owns raw operational observation and local enrollment.
- Core may bind a Harbor machine to a station and consume normalized summaries.
- Core tasks, claims, policy and audit do not become Harbor jobs.
- Harbor jobs remain provider-local delivery records for bounded operations.
- Display media and signaling remain owned by the display protocol/runtime.

The bridge should be optional and projection-based. Harbor should expose a
versioned snapshot plus cursor/delta feed; it should not duplicate Core's task or
policy models.

## Immediate simplification plan

Before adding more features:

1. Define collection profiles: `minimal`, `standard`, and `detailed`.
2. Change the default to `minimal` or `standard`, not full detail.
3. Reduce default cadence and disable automatic fetch.
4. Send repository/service deltas keyed by content hash.
5. Retain only meaningful transitions and administrative audit.
6. Stop replacing all normalized repo/service rows on unchanged pushes.
7. Add bounded retention policies.
8. Split a true agent-only package from hub/database dependencies.
9. Version the protocol before implementing the Rust agent.
10. Add on-demand freshness when a user opens a machine or requests an action.

## Safety work that remains necessary

Reducing data volume does not remove the need for a few boundary fixes before
Harbor performs broader administration:

- Protect browser mutations against CSRF and clickjacking.
- Bound enrollment, payload size, strings, arrays and WebSocket connections.
- Fail when an explicitly requested configuration file is missing.
- Bind an approved agent to a machine-specific credential, not only a Tailscale
  node and self-asserted name.
- Authenticate the intended hub before an agent sends detailed observations.
- Redact credential-shaped data from URLs, errors and action output.
- Keep jobs typed, idempotent, locally allowlisted and narrowly scoped.

These controls should be implemented alongside simplification, not used as a
reason to retain high-frequency snapshots or broaden Harbor into a general
remote execution service.

## Features to defer

Do not expand these until the lean data path is accepted:

- Arbitrary remote jobs
- Continuous process history
- High-frequency resource history
- Full package inventory on every node
- Display signaling over Harbor AgentLink
- Automatic task creation in MyAI
- Broad machine configuration mutation
- General-purpose remote shell execution

The desired outcome is less data with clearer meaning, not a smaller UI over the
same noisy collection model.

## Response (2026-11-12, after `398e69c`)

Decision: keep the original scope (inventory, manifests, recaps, pairing, tray, display later) but make
it light. Adopted now:

| Recommendation | Done |
| --- | --- |
| Lower cadence | push/heartbeat 5 min (was 1), rescan 10 min (was 5), fetch daily (was hourly), manifest daily (was 6 h) |
| On-demand freshness | opening a machine in the UI asks its agent to rescan and push (`POST /api/machines/{id}/refresh`, at most every 30 s) |
| Stop rewriting unchanged rows | repo/service rows are written only when they differ from the previous push; measured: zero row writes on unchanged pushes (was ~26 deletes + 26 inserts per push per machine) |
| Bounded retention | daily: events 90 days (admin audit kept), manifests 90 days plus the latest, job output 30 days, recaps 52, expired pairing codes |
| CSRF / clickjacking | cross-site writes and websocket upgrades refused (Origin / Sec-Fetch-Site); `X-Frame-Options: DENY`, `frame-ancestors 'none'` |
| Bounded payloads | request bodies capped at 4 MB; link messages at 64 KiB |
| Agent-only package | `harbor-agent`, 22 MB, no web/database code (`e0d316f`) |
| Inventory by content hash | manifests sent and stored only on change (`98668f9`) |
| Credential redaction | remote URLs (`652bfc0`); error text and job output still to do |

Not adopted, deliberately:

- **Per-subject versioned deltas**: the state is small (tens of repos); writing only changed rows plus
  the slower heartbeat gets most of the benefit. Revisit with the Rust agent.
- **Recursive filesystem watchers**: would hit inotify limits on large trees. If needed, watch each
  repo's `.git/HEAD`, `.git/index` and `.git/refs` only.
- **Fetch disabled**: "behind" depends on it; daily is the compromise.
- **Collection profiles**: data is redacted per role already; resource samples live only in the
  single latest-snapshot row, not in history. Revisit if a device needs less collected.
- **SQLite**: not now; PostgreSQL stays while harbor may also host code/tasks.
- **Per-agent credential beyond the Tailscale node**: the node key already is one; the residual risk
  (two agents on one node impersonating each other) is low for a personal fleet.
