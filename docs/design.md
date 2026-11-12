# harbor: design (agreed 2026-11-08; slices 1–2 built)

Working name `harbor` (corner-dock grown up). Own git repo, own folder.

## Scope refinement and integration handoff (2026-11-12)

Harbor owns its general machine/repository inventory, ledger, configuration/admin and
access-catalog direction. DisplayOverInternet remains a lightweight standalone display
product and will supply optional reusable display/runtime packages. Harbor must remain
independently installable and useful without that application, its daemon or a repository
checkout. This is a package-first integration, not a required second service.

See [DISPLAY_INTEGRATION.md](DISPLAY_INTEGRATION.md) before implementing desktop viewing,
pairing/device projections or access tiles. It records what exists, what is still planned,
work that can proceed independently, and producer/consumer acceptance gates. Existing
tailnet read access does not grant desktop viewing or control. The proposed consumer
scopes/feed below remain design direction rather than implemented access contracts.

## Starting point (2026-11-08)
- The hub machine has dotnet 10, bun, podman and git; PostgreSQL runs in podman
- A tailnet with Linux desktops, a Raspberry Pi, Windows PCs, iPads and a phone
- Tailscale's LocalAPI socket is readable by users (the snap install)
- A couple of dozen git repos spread across machines

## 1. Architecture: per-machine agent + hub (recommended)
| | Agent per machine + hub | One host scanning over SSH |
|---|---|---|
| Install | binary on each machine | nothing |
| Windows (laptop) | native (git.exe, `sc query`) | needs OpenSSH server + PowerShell quirks |
| Offline machines | last snapshot kept, "stale 14d" pill | just missing |
| Speed | local disk walk | walk over SSH, slow on big drives |
| Blast radius | hub holds no shell access | hub holds SSH keys into every box |
| "Is it running" | local process/systemd/port checks | per-check SSH round trips |

Agents **push** snapshots to the hub (works when the hub can't reach them; a machine that wakes up reports itself). Phones/iPads are viewers only.
Same binary runs as `agent`, `hub` or both, so one machine needs one process.

## 2. Storage
- Hub: PostgreSQL (podman container). Snapshot table as JSONB (shape evolves freely) + a few normalized tables (machine, repo, service, event) for queries and the productivity timeline (commits/day per machine/repo).
- Agent: no database. Config file + in-memory last scan.
- Challenge: slice 1 doesn't need Postgres at all (one machine, live scan). I'd add it with the hub in slice 2 rather than stand up a container to store one machine's snapshot.

## 3. Languages
- **F#** for agent + hub (ASP.NET Core minimal APIs). Fits the stack; `win-x64` + `linux-x64` self-contained single-file publish covers laptop. Native AOT possible later (System.Text.Json source-gen).
- **git via CLI** (`status --porcelain=v2 --branch`, `for-each-ref`, `log`), not LibGit2Sharp: respects your git config, credential helpers, worktrees, fsmonitor; libgit2 lags git features.
- **UI: TypeScript/React + Vite, built with bun**, served by the hub as static files. Web over TUI because iPads can view it over the tailnet; retro-future look: DisplayOverInternet's sage-on-black palette with monospace labels (see `docs/mockups/machine-hero.html`). A TUI (Spectre.Console) could read the same API later.
- Considered Rust for the agent (tiny static binary, learning value) — not worth splitting the codebase now; the agent is mostly shelling out to git.

## 4. Discovery, auth, permissions
- **Transport + machine identity: Tailscale.** Hub at a MagicDNS name (e.g. `devbox:7420`). The hub checks each agent's caller identity via Tailscale's local WhoIs API, so no home-made PKI. Trade-off: tied to Tailscale; fallback is mTLS later.
- **Enrolment:** agent first contacts hub → shows as "pending" in the UI → you approve once.
- **Consumers (other agents/systems):** bearer tokens, each with
  - scopes: `machines:<glob>`, `repos:read`, `services:read`, `logs:<source>`, `releases:read`
  - granularity: `detail = summary|full`, min interval (rate limit), redactions (remote URLs, paths, emails)
  - read via `GET /feed?since=<cursor>` (append-only event log) and `GET /snapshot`
- **Writes** (fetch/pull/branch ops) are UI-only, never consumer scopes; pull is `--ff-only`.

## 5. Later slices (not now)
- Own-project versions: optional `.harbor.toml` in a repo saying how to read "running" (command/HTTP/file) vs "built" (artifact mtime or `git describe`). Generalizes corner-dock's built-vs-deployed.
- Upstream releases for third-party repos: `git ls-remote --tags` (any host, no API rate limits), GitHub releases API as an extra.
- GitKraken-like: commit graph from `git log --format=%H%x00%P...`, diffs, branches, fetch/pull.

## Slice 1 (what I'd build once agreed)
- F# server: config (scan roots, ignore globs, services list) → repo discovery → per-repo git facts (remotes, user.name/email, branch, upstream ahead/behind, dirty counts, last commit date, last-modified = newest of commit/index/dirty-file mtimes) → service checks (systemd system/user unit, process name, TCP port, HTTP URL, podman container).
- JSON API `/api/repos`, `/api/services`, localhost-bound by default.
- React UI: machine card, repo rows with pills (branch, ↑↓, dirty, identity), service pills (running/stopped), Tokyo Night, chevrons to page machines (one machine for now).
- A few tests on parsing (porcelain v2, for-each-ref) where bugs actually live.

## Slice 2 (built 2026-11-12)
- Modes agent / hub / both / migrate. In `both`, the local agent pushes in-process and is auto-approved.
- Identity: WhoIs StableID + agent-claimed name = one machine; pending until approved (loopback, or
  `hub.admins` over the tailnet). Pushes over loopback are refused. The hub listens on loopback plus its
  Tailscale IPs only.
- Storage: Postgres 18 (podman quadlet), grate migrations (edits to applied scripts are refused),
  SqlHydra.Query over generated types (separate project, nullness off). Ingest is serialized.
- Offline machines: the hub keeps the last snapshot; the UI shows "stale <age>" after ~3 missed pushes.
  Agents resend recent commits for repos whose HEAD the hub hasn't stored, so commits made while a
  machine was away still land on the timeline with their author dates.
- Across machines: grouped by normalized origin URL; shows whether HEADs match and which has the newest
  last commit. It doesn't show ancestry ("A is behind B"): the hub only sees 30 days of commits and no
  parent links, so it can't tell behind from diverged. Adding parent shas to the payload would fix that.
- Verified: a second local agent (other name and roots), a third over IPv6, approve/reject, a dirty to clean
  event, retry with backoff while the hub was down, and the win-x64 single-file agent under wine 10
  (it pushed and enrolled; no git.exe in wine, so repo rows showed a git error).

## Slice 3 (built 2026-11-12)
- UI redesign (retro-future, tabs: This machine / Network / Pending / pinned machines).
- Agent resources, processes, disks, idle time, networks, local branches.
- Roles (admin/viewer), viewer redaction, credential-free remote URLs, audit of admin actions.
- Agent hub link (outbound WebSocket: hello/welcome/wake); Tailscale Serve listener with header identity.
- Manifests with change history, machine compare, weekly recaps (behind-since from git).
- Current overview, footprint numbers and open decisions (including edge agents in .NET vs Rust):
  [OVERVIEW.md](OVERVIEW.md).
