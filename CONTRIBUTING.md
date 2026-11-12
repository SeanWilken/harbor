# Contributing to harbor

Thanks for looking. harbor is a small, opinionated tool: a deterministic, self-hosted view of your
machines that stays light. Contributions that keep it that way are very welcome.

Site: <https://seanwilken.github.io/harbor/> · Overview for reviewers: [docs/OVERVIEW.md](docs/OVERVIEW.md)

## Before you start

- **Open an issue first** for anything bigger than a bug fix or a small collector, so we can agree on
  the shape before you write it. Roadmap items (peer hubs, context sync, essence profiles, issues/tasks
  sync, desktop view) are being designed; ask before building on them.
- Read [docs/LEAN_ARCHITECTURE_RECOMMENDATION.md](docs/LEAN_ARCHITECTURE_RECOMMENDATION.md) before
  adding collection, jobs or storage. The short version: collect less, write only what changed, keep
  history bounded, and prefer on-demand freshness over polling.
- harbor must stay **standalone and deterministic**: no required cloud service, no AI in the loop,
  no optional integration that the core depends on.

## Setup

| | |
| --- | --- |
| .NET 10 SDK | hub, agent, tray, tests (F#) |
| bun | UI (React 19 + Vite, TypeScript) |
| podman | PostgreSQL 18 for the hub (`deploy/install-pg.sh`) |
| git, Tailscale | the agent shells out to git; the hub identifies callers via Tailscale |

```sh
dotnet build harbor.slnx
dotnet test                                   # tests/Harbor.Tests (xUnit)
cp harbor.example.json harbor.json            # gitignored; never commit yours
(cd ui && bun install && bun run build)
dotnet run --project src/Harbor               # mode `both`: hub + local agent on :7420
(cd ui && bun run dev)                        # UI with hot reload on :7421, proxies /api to :7420
```

`harbor-agent snapshot` and `harbor-agent manifest` print what an agent would send, which is the
quickest way to test a collector without a hub.

## Layout

| path | what |
| --- | --- |
| `src/Harbor.Core` | everything an agent needs: git parsing, discovery, inventory, services, resources, manifests, the hub link |
| `src/Harbor.Agent` | the lean agent executable (no web server, no database) |
| `src/Harbor` | the hub: guard and API (`Hub.fs`), storage, events, recaps, pairing, redaction, Tailscale client |
| `src/Harbor.Tray` | Avalonia tray app |
| `src/Harbor.Db` | SqlHydra-generated database types (do not edit by hand) |
| `db/up` | grate migrations |
| `ui/src` | React UI |
| `deploy` | install scripts and service units |
| `tests/Harbor.Tests` | xUnit tests |

## How code here is written

- **Match the file you're in.** Naming, idioms and comment density should read like the surrounding code.
- **Small, composable functions.** Keep decisions pure (see `GitParse`, `Enrol`, `Events`, `Redact`) and
  push I/O to the edges, so the decisions are easy to test.
- **Comments explain why, not what.** Most functions need none.
- **Types are strict by default.** A looser type is fine where it buys real composability; say why.
- **Tests where they earn their place**: parsers, decisions and anything security-relevant. Look at the
  existing tests for the density we aim for; exhaustive edge cases aren't required.
- **UI**: keep to the theme tokens in `ui/src/styles.css`; the layout reference is
  `docs/mockups/machine-hero.html`. Check phone width as well as desktop. `bun run check` type-checks.

## Database changes

Add a new `db/up/NNNN_*.sql` file; grate refuses to run if an applied script was edited. Then run
`db/regen.sh` to regenerate `src/Harbor.Db/HarborDb.fs`.

## Security and privacy

- Anything new an agent collects must be considered for viewer redaction (`src/Harbor/Redact.fs`).
- Remote actions are a per-agent allow-list. Don't add a job kind that runs arbitrary commands.
- Never commit a real `harbor.json`/`hub.json`, tailnet addresses, logins or machine names. Use
  `example.com` and made-up hostnames in docs, tests and screenshots.
- Report vulnerabilities privately (the repository's Security tab → "Report a vulnerability"), or open
  an issue asking for a private contact without including details.

## Pull requests

- One change per PR, with a short description of what and why, and how you checked it
  (`dotnet test`, `bun run check`, a screenshot for UI changes).
- Keep unrelated refactors in their own PR.
- By contributing you agree your work is licensed under the [Apache License 2.0](LICENSE).

## The site

The public site is plain static files in `site/`, deployed to GitHub Pages by
`.github/workflows/pages.yml` on pushes to `main` that touch `site/`. No build step: edit the HTML
and preview it with `python3 -m http.server -d site`. Screenshots in `site/img/` come from the real
UI running against made-up demo data.
