# Releases from harbor: what a repo provides

Harbor can refresh a deployed project from its UI: pull or build images, run migrations, recreate
containers. It works out what a repo can do from the repo's own files. Nobody writes a release
script for harbor; a repo only follows a few conventions below. This document is the contract
between harbor and the repos it releases. Each repo's own agent owns its side.

## How harbor sees a repo

The agent scans each repo it already tracks for **release units**. A unit is one folder (the repo
root, or a subfolder in a monorepo, up to three levels down) holding a compose file:
`compose.yaml`, `compose.yml`, `docker-compose.yaml` or `docker-compose.yml`. Git-ignored folders
count: a local deployment folder with its own `.env` is the usual case.

For each unit harbor reads, without running anything:

- **services**, with their `image`, whether they have a `build` section, and their `profiles`.
  An image named by a variable (`${CORE_API_IMAGE}`, `${X:-default}`) is resolved from the unit's
  `.env`. Only variables used in `image:` lines are read; no other value leaves the machine.
- **profiles**: the union of the services' profiles. These are the only profile values a release
  can ask for.
- **migrations**: a `migrations/` folder of `.sql` files in the unit or at the repo root, and a
  runner for them (below).
- **what's running**: the containers compose started from that folder (by compose's
  working-directory label), with their image and state.

## Steps

A release is an ordered subset of these steps. Harbor runs compose itself (`podman compose`, or
`docker compose` where podman is missing); only migrations call the repo's own code.

| Step | Runs | Offered when |
|---|---|---|
| `pull` | `compose [--profile P] pull` (failures for local-only images are reported, not fatal) | any service has an image |
| `build` | `compose [--profile P] build` | any service has a `build` section |
| `migrate` | the repo's runner: `apply` | a runner is found |
| `up` | `compose [--profile P] up -d --force-recreate --remove-orphans` | always |

Before a release that includes `migrate`, harbor runs the runner's `status` and shows how many
migrations are pending in the confirmation.

## The migration runner contract

Harbor looks for, in the unit folder and then the repo root:

- Linux and macOS: `migrate-db.sh`, run as `bash migrate-db.sh <action>`
- Windows: `migrate-db.ps1`, run as `pwsh -File migrate-db.ps1 -Action <action>`

The runner must:

1. Accept `status` and `apply`. Other actions are fine; harbor never sends them.
2. With `status --json` (`-Json` in PowerShell), print one JSON object to stdout:
   `{ "applied": ["0001_x", ...], "pending": ["0027_y", ...] }`. Extra fields are ignored.
3. Start whatever it needs itself (for example `compose up -d postgres`) and wait for it.
4. Exit non-zero on failure, and be safe to re-run: `apply` with nothing pending succeeds.
5. Run from its own folder without prompts (stdin is closed) and read secrets from `.env` or the
   environment, never from arguments.

## Who can release

- A machine opts in by adding `"release"` to `agent.allow` in its harbor config. Without it, its
  units are shown but every release is refused on the machine itself.
- Only hub admins queue releases, and every release is in the history with who asked.
- The hub sends a unit path, a profile and step names, never a command. The agent checks all three
  against what it detects itself at that moment.
- A recipe (the steps and profile a unit usually releases with) is saved on the hub, keyed by the
  repo's remote and the unit's folder, so every clone of the repo shares it.

The trust model is the same as running the repo's scripts yourself: a release runs whatever the
checked-out commit's runner and compose file say. Review what you pull before you release it.

## Open items for repos

### myAI core (`myai-suite`)

Found while wiring harbor up, 2026-11-26. For the myAI agent to decide and do in its repo:

1. **No Linux migration runner is committed.** `migrate-db.sh` (from the bootstrap bundle) calls
   `migration-runner.sh`, but the repo only has `migration-runner.ps1`, and PowerShell isn't
   installed on the Linux host. Migrations can't run there today, from harbor or by hand.
2. **The deployment folder's scripts aren't in the repo.** `myai-suite/` is git-ignored except its
   compose file; `deploy.*`, `refresh-images.*` and `migrate-db.*` come from a bootstrap bundle on a
   share. Harbor only needs the migration runner, so committing that (and generating `.env` only)
   is enough.
3. **Named arguments.** `deploy.sh` takes three positional `true`/`false` values, and
   `refresh-images.sh` takes the image list first where `deploy.sh` takes the profile first. If the
   scripts stay for manual use, named flags (`--profile`, `--refresh`, `--skip-migrations`) would
   remove the ordering trap. Harbor doesn't call these two scripts.
4. **`status --json`** as in the contract above, so harbor can show pending migrations before a
   release.
