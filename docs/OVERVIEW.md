# Harbor overview

For reviewers from other projects (the myAI suite, DisplayOverInternet): what harbor is for, what
it tracks, how it is built, how to integrate with it, and what is still open. Current as of
2026-11-12 (commit after `98668f9`). Details live in [README.md](../README.md),
[design.md](design.md) and [DISPLAY_INTEGRATION.md](DISPLAY_INTEGRATION.md).

## Purpose

A personal, tailnet-only **ledger and control plane for my machines**: what exists where, what state
it is in, what changed, and what needs attention. It is a standalone tool that works without AI;
everything it reports is collected and decided deterministically, so other systems (including AI
agents) can rely on it as a source of facts rather than re-deriving them.

In scope: machines, git repositories, services, resources, installed software, change history,
weekly recaps, access (roles, audit) and bounded remote actions. Out of scope for harbor itself:
desktop streaming (optional DisplayOverInternet packages, see below), generic remote shell, and
being an issue tracker of record (forges stay the source of truth; see roadmap).

## What it tracks

| Area | Facts | Freshness |
| --- | --- | --- |
| Machines | identity (Tailscale StableID + agent name), approval status, OS, last push, live link | every push (default 60 s) |
| Repos | path, remotes (credentials stripped), branch, ahead/behind and since when, dirty counts, last commit, last modified, worktrees, local branches, last fetch | rescan (default 60 s) |
| Commits | 30 days per repo, deduplicated by (remote, sha) across machines | on HEAD change |
| Services | systemd units, processes, ports, HTTP, podman containers, as configured | every push |
| Resources | CPU/GPU/RAM %, disks, load, uptime, input idle (working/idle), top processes | every push |
| Networks | interfaces, addresses, gateway + gateway MAC (to tell networks apart) | every push |
| Installed software | manifest: OS/kernel, dpkg (+ known upgrades), snap, flatpak, repo locations; history of changes | every 6 h, stored only on change |
| Events | repo added/removed, dirty/clean, ahead/behind, branch changed, service up/down, commit seen, manifest changed, jobs, admin actions (with actor) | as they happen |
| Recaps | behind / uncommitted / unpushed / neglected repos, package changes, pending upgrades, version drift | weekly, stored; live on demand |

## Architecture

```
 browser ──HTTPS (Tailscale Serve :7443)──▶ hub :7425 (loopback, identity = Serve login)
 browser ──HTTP over tailnet───────────────▶ hub :7420 (identity = Tailscale WhoIs)
 agent ──push every 60 s (HTTP, WhoIs)─────▶ hub :7420 ──▶ PostgreSQL (podman)
 agent ──persistent WebSocket (outbound)───▶ hub :7420   wake / welcome; display signaling later
```

- **One binary**, modes `agent`, `hub`, `both`, `migrate`. F# on .NET 10, ASP.NET minimal APIs.
- **Agents** hold no database and listen on nothing reachable (an optional loopback debug API).
  They scan, sample and push; the hub stores history, diffs, decides recaps and serves the UI.
  So an agent is already the "edge" role: it defers storage, history and presentation to the hub.
- **Hub**: PostgreSQL 18 with versioned migrations (grate) and typed queries (SqlHydra); React UI
  built with bun and served as static files.
- **Durable vs live**: jobs and their results travel by push/reply (acknowledged, idempotent). The
  WebSocket only wakes agents (a queued job lands in about 0.5 s) and will carry live display
  signaling; nothing durable is replayed from it.

### Why the Tailscale Serve route (hub only)

Only the **hub** needs it, and only for **browsers**. Plain `http://devbox:7420` works over the
tailnet, but browsers treat it as an insecure context: no PWA install, no service worker, and
DisplayOverInternet's embedded viewer will need a secure context (WebRTC, cookies). Serve gives a
real certificate on `https://devbox.<tailnet>.ts.net:7443` without harbor managing certificates.
Agents never need it: they push to the hub's tailnet address and are identified by WhoIs. It is a
one-time `sudo tailscale serve --bg --https=7443 http://127.0.0.1:7425` on the hub machine.

## Access model

- Roles: **admin** (loopback, or `hub.admins` logins) and **viewer** (`hub.viewers`, `"*"` = any
  identified tailnet user). Everyone else: 403. Fails closed.
- Viewers get redacted data: no process lists, MAC/LAN addresses, git identities, changed file
  names, service details, job output, logins, package contents or package drift.
- New machines are **pending** until an admin approves them.
- Admin actions are audited (`event.actor`).
- Remote actions are an allow-list per agent (`agent.allow`, default `["fetch"]`); no shell.

## Integration points for other systems

Available now (same roles apply; an automation running on the hub gets admin via loopback, from
another machine it is identified by its Tailscale login):

- `GET /api/machines`, `/api/machines/{id}/snapshot`, `/api/shared` (repos on several machines)
- `GET /api/machines/{id}/manifests`, `/api/manifests/{id}` (`?format=md`), `/api/manifests/{a}/diff/{b}`
- `GET /api/recap?days=7` (`&format=md`), `/api/recaps`, `/api/recaps/{id}`
- `GET /api/me` (who the hub thinks you are, and your role)
- `POST /api/machines/{id}/jobs` `{ kind: "fetch", path? }` (admin)
- Database: `event` is append-only and ordered (ids commit in order), ready for a feed.

Planned (design direction, not implemented): consumer bearer tokens with scopes, rate limits and
redaction levels; `GET /feed?since=<cursor>` over `event`; a JSON schema for the agent protocol so
non-.NET agents can be written against it.

## Footprint (measured 2026-11-12 on an x86-64 desktop, 32 cores)

| | Lean agent (`harbor-agent`) | Full binary as agent (`harbor agent`) |
| --- | --- | --- |
| File | 21 MB x64, 22 MB arm64 (self-contained, framework trimmed) | 113 MB |
| Memory | ~37 MB private + ~25 MB mapped program | ~50 MB private + ~70 MB mapped |
| Threads | 12 | ~50 |
| CPU | ~0.1 s per minute after start-up | ~0.3 s per minute |
| Needs | `git`; nothing listening, no ICU | same |

The hub (`harbor both`) is ~160 MB RSS plus PostgreSQL and belongs on a stronger machine. A
Raspberry Pi 4 runs the lean agent with room to spare; a Pi Zero 2 (512 MB) should too. Not yet
measured on real Pi hardware.

Projects: `Harbor.Core` (everything an agent needs), `Harbor.Agent` (lean executable),
`Harbor` (hub, references Core), `Harbor.Db` (generated database types).

## Open decisions

1. **Edge agents.** Done first step: the lean .NET agent above. A Rust agent (roughly 3–6 MB,
   under 10 MB memory) stays an option for very small devices or many of them; it would need a
   second implementation of the collectors and protocol, kept in step by a shared JSON schema and
   golden fixtures (the wire protocol is plain JSON for this reason). Agent **profiles** (which
   collectors run) would let small devices report only what they need, in either language.
2. Issues/tasks from GitHub/Gitea as a read cache plus harbor-owned metadata keyed by forge
   references (commit, branch, issue/PR number). Agreed in principle, not built.
3. Network map grouping by gateway, with user-named networks; network selector and VPN switch
   (VPN switch needs `tailscale set --operator`, deliberately not granted yet).
4. Display: Desktop view arrives through DisplayOverInternet's runtime packages and harbor's agent
   link once DOI's acceptance gates pass ([DISPLAY_INTEGRATION.md](DISPLAY_INTEGRATION.md)).
5. Collectors not yet written: winget (Windows), Homebrew, npm -g / pipx / dotnet tools.
