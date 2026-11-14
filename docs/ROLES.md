# Roles: what each machine is doing, and whether it takes work

**Status: living draft (v0, 2026-11-13).** This changes as the wider app suite works out how to
use it. Fields marked *stable* are safe to build on now; everything else may move.

## The idea

Each machine is in exactly one **role** at a time, for example `general`, `worker`, `gaming` or
`media`. A role says two things:

1. **What the machine is for right now.** A gaming PC streaming games, or a media server serving
   films.
2. **How much outside work it takes while in that role.** Harbor reports this so the rest of the
   suite can send heavy work (builds, AI inference, batch jobs) somewhere else in the meantime.

Harbor **reports and switches** roles. It doesn't schedule work, and it doesn't stop anything from
ignoring a role. Whatever hands out work reads the availability and honours it.

### Roles versus services

| | Role | Service |
| --- | --- | --- |
| How many | One at a time per machine | Any number |
| Changes | When someone switches it | When it starts or stops |
| Example | `media` on the box with the film drive | Jellyfin, checked by `services` |
| Says | "Busy with this; send other work elsewhere" | "This is up or down" |

A `media` role usually goes with a Jellyfin service check, but they're separate: Jellyfin can run
in any role, and the role is the statement about workload.

## Declaring roles (agent config)

Roles are declared in each machine's own config. The hub and the UI can only pick one of these
names. They can't add roles, change what a role does, or send commands. Declaring roles is the
consent; `agent.allow` doesn't need a `role` entry.

```jsonc
"roles": [
  // the first role is the default
  { "name": "general", "work": "light", "power": "balanced" },
  { "name": "worker",  "work": "full",  "power": "performance" },
  { "name": "gaming",  "work": "none",  "power": "performance",
    "start": [ { "command": "C:\\Program Files\\Sunshine\\sunshine.exe" },
               { "command": "steam://open/bigpicture" } ],
    "stop":  [ { "process": "ollama" } ] }
]
```

| Field | Meaning |
| --- | --- |
| `title` | Optional: the card's heading, e.g. "Game streaming". Defaults to the name. |
| `description` | Optional: one sentence on what the machine is for in this role. |
| `name` | Lowercase `a-z 0-9 -`, up to 32 characters, unique. *Stable.* |
| `work` | `full`: spare capacity, so heavy work and inference are welcome. `light`: small jobs only. `none`: takes no outside work. *Stable.* |
| `power` | Optional: `performance`, `balanced` or `power-saver`. Windows uses `powercfg /setactive` with the built-in schemes; Linux uses `powerprofilesctl set`. |
| `start` | Run when switching *into* the role. Each entry is either `{ "command": …, "args": [...] }` (an app, file or URL, opened like a double-click, and skipped if a process with that name is already running) or `{ "unit": … }` (a systemd user unit, Linux only). |
| `stop` | Run when switching into the role, before anything starts. Each entry is either `{ "process": … }` (asked to close: SIGTERM on Linux, closing the main window on Windows; never killed) or `{ "unit": … }`. |

A role whose `work` is `none` also pauses harbor's own background git fetches and manifest
rebuilds, so harbor adds no load either. Rescans and pushes continue, so the machine still reports.

Each role lists only what happens when you switch into it. Nothing is undone when you leave. If
switching back to `general` should close Steam, put that in `general.stop`.

## Switching

| From | Who may | How |
| --- | --- | --- |
| Harbor UI (machine card) | Hub admins | Asks for confirmation, then queues a `role` job. The agent runs it at once if linked, otherwise on its next push. |
| Tray on the machine | Whoever is logged in at the machine | The Role submenu. The agent picks it up within 2 seconds. |
| API | Hub admins | `POST /api/machines/{id}/jobs` with `{ "kind": "role", "arg": "<name>" }`. |

The hub refuses a role name that isn't in the machine's latest snapshot, and the agent checks it
again. The switch happens even if an action fails, because the role states intent. The job output,
which only admins see, lists each action as `ok`, `started`, `failed (…)` and so on. Switching to
the current role runs its actions again, for example to restart something it starts.
A new switch closes any older switch still waiting for that machine as superseded, so the newest
request always wins.

The current role is saved on the machine, next to the agent's status file. It survives restarts and
reboots, and a restart doesn't re-run any actions. If a config edit removes the saved role, the
machine falls back to the first declared role.

## Reporting

- **Snapshot** (`snapshot.role`, on every push):
  `{ name, work, since, by, declared: [{ name, work }], applied: [lines] }`.
  Each declared role also carries `title`, `description`, `power`, and `start`/`stop` as short
  labels (an app's file name, a URL, `unit x`; never a full path). `by` is `default`, `tray` or `hub`. Viewers don't see `applied` or the start/stop labels, and see a hub admin's name as `hub`.
- **Machine list** (`GET /api/machines`): `role`, `work` and `roleSince` on each machine.
- **History:** a `role_changed` event when the role changes; the admin who queued it is in the
  `job_requested` audit event. The first report of the default role isn't an event.
- **UI:** role cards on the machine card, one per declared role, paged left and right (or swiped).
  Each shows the title, work level and description; **inspect** lists its capabilities, its duties
  when activated and, for the active role, what the last switch did. Admins activate a role from
  its card. There is also a chip on the network grid and the role name on the
  map. The tray shows the role under each agent.

## Availability API (for the rest of the suite)

`GET /api/availability` is readable by hub admins and viewers, on the tailnet only.

```json
{
  "at": "2026-11-13T21:04:00+00:00",
  "machines": [
    { "machine": "devbox",  "role": "worker", "work": "full", "since": "…", "online": true,  "available": "full",    "lastPushAt": "…" },
    { "machine": "gamebox", "role": "gaming", "work": "none", "since": "…", "online": true,  "available": "none",    "lastPushAt": "…" },
    { "machine": "pi",      "online": true,  "available": "unknown", "lastPushAt": "…" }
  ]
}
```

- `available` is the field to route by (*stable*). It equals `work` while the machine is online and
  is `none` when it's offline. A stale snapshot is never treated as capacity. It's `unknown` when the
  machine declares no roles.
- `online` means the machine holds a live hub link or has pushed within the last three push
  intervals.
- `role`, `work` and `since` are left out (not null) when a machine declares no roles.
- Only approved machines are listed.

The live load figures (CPU, GPU, memory and idle time) are already on `GET /api/machines`. A
consumer can combine `available: full` with low GPU use to pick an inference host.

## Not in v0

- **Administrator or root actions**, such as Windows services, system units or GPU clocks. The
  agent runs as the logged-in user, and giving it more is a separate decision.
- **Automatic switching**, for example "a Sunshine stream is live, so treat the machine as gaming".
  This could later be a suggestion first and a switch only on opt-in.
- **A time limit**, for example "gaming until 23:00, then back to general".
- **Callers without a person**: the API accepts tailnet logins (admins and viewers). A service
  account or token for other apps hasn't been designed yet.
- **What `worker` accepts**: harbor only advertises it. Dispatching work is the suite's or
  coordinator's job.

## Open questions for the suite

1. Is `full`/`light`/`none` enough, or does the suite need capacity by kind (GPU inference vs CPU
   builds vs storage)? A later version could add per-role `accepts: ["inference", "build"]`.
2. Should availability be pushed (an event stream or webhook) rather than polled?
3. Who owns the definition of a role, if the suite also records machine duties? The current
   proposal: the machine's config declares them, harbor reports them, and the suite reads them.
4. What identity will the suite use to call harbor?
