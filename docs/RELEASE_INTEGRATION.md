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

- Linux and macOS: `migration-runner.sh`, run as `bash migration-runner.sh --action <action> [--json]`
- Windows: `migration-runner.ps1`, run as `pwsh -File migration-runner.ps1 -Action <action> [-Json]`

The runner must:

1. Accept the actions `status` and `apply`. It can have others; harbor never sends them.
2. With `--json` (`-Json`), write JSON to stdout: one object, or one object per line (an event
   stream). Harbor uses the last object that has `pending`, either a list of names or a count, or
   `"upToDate": true`. Other fields and other lines are ignored. Without one, harbor shows
   "pending unknown".
3. Find its own defaults: the suite folder and migrations folder when no paths are passed, and
   secrets from `.env` or the environment, never from arguments.
4. Start or wait for what it needs (for example the database container), exit non-zero on
   failure, and be safe to re-run: `apply` with nothing pending succeeds.
5. Run without prompts: stdin is closed.

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

Found while wiring harbor up, 2026-11-26 and updated the same day after reading the runner. For
the myAI agent to decide and do in its repo:

1. **The runner isn't on the default branch yet.** `migration-runner.sh` and
   `scripts/migration-helper.mjs` are in a feature commit ("add deployment orchestration") that
   is on feature branches but not on master. A clone on master has only `migration-runner.ps1`,
   which calls a helper it doesn't have. Once that commit lands, harbor offers `migrate` for
   `myai-suite` with no further change on either side.
2. **`status --json` doesn't say what's pending.** The helper runs grate with `--isuptodate` and
   writes events (`migration.started`, `migration.completed` with grate's stdout inside). Harbor
   reads the last event that has `pending` (names or a count) or `upToDate`, so adding either to
   the `migration.completed` event for `status` is enough. Names are better: they go in the
   release confirmation.
3. **Does the runner start the database?** The helper waits up to `--wait-seconds` for the
   database; the bundle's `deploy.sh` started the `postgres` service first. When the suite is
   down, a release's `migrate` step runs before `up`, so the runner (or the helper) should start
   `postgres` itself, as the contract asks.
4. **Prerequisites on the host:** Node or Bun, plus the .NET `grate` tool (`--restore-tools`).
   Saying so in the runner's error when grate is missing would make a failed release
   self-explanatory.
5. **The deployment folder's other scripts** (`deploy.*`, `refresh-images.*`, `migrate-db.*`)
   come from a bootstrap bundle and aren't in the repo. Harbor doesn't need them. If they stay for
   manual use: `deploy.sh` takes three positional `true`/`false` values, and `refresh-images.sh`
   takes the image list first where `deploy.sh` takes the profile first. Named flags
   (`--profile`, `--refresh`, `--skip-migrations`) would remove the ordering trap.
