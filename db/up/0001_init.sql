-- One row per (tailscale node, agent name): two agents on one node enrol separately.
create table machine (
    id            bigint generated always as identity primary key,
    node_id       text not null,  -- Tailscale StableID; 'local' for the hub's own in-process agent
    name          text not null,  -- claimed by the agent
    node_name     text,           -- Tailscale DNS name
    login         text,           -- Tailscale user
    os            text,
    status        text not null check (status in ('pending', 'approved', 'rejected')),
    push_seconds  int,
    first_seen_at timestamptz not null default now(),
    last_push_at  timestamptz,
    approved_at   timestamptz,
    unique (node_id, name)
);

create table snapshot_latest (
    machine_id  bigint primary key references machine (id),
    scanned_at  timestamptz not null,
    received_at timestamptz not null,
    payload     jsonb not null
);

-- Replaced on every push from an approved machine.
create table repo (
    machine_id       bigint not null references machine (id),
    path             text not null,
    name             text not null,
    remote_key       text,  -- normalized origin URL, e.g. github.com/seanwilken/harbor
    branch           text,
    head_oid         text,
    upstream         text,
    ahead            int not null,
    behind           int not null,
    staged           int not null,
    unstaged         int not null,
    untracked        int not null,
    conflicted       int not null,
    dirty            boolean generated always as (staged + unstaged + untracked + conflicted > 0) stored,
    last_commit_at   timestamptz,
    last_modified_at timestamptz,
    error            text,
    primary key (machine_id, path)
);
create index repo_remote_key on repo (remote_key) where remote_key is not null;

create table service (
    machine_id bigint not null references machine (id),
    name       text not null,
    check_kind text not null,
    state      text not null check (state in ('running', 'stopped', 'unknown')),
    detail     text not null,
    primary key (machine_id, name)
);

-- Deduplicated across machines; `commit` is a reserved word.
create table git_commit (
    repo_key           text not null,  -- remote_key, or 'local:<machine id>:<path>'
    sha                text not null,
    authored_at        timestamptz not null,
    author             text not null,
    subject            text not null,
    first_seen_machine bigint not null references machine (id),
    first_seen_at      timestamptz not null,
    primary key (repo_key, sha)
);

-- Append-only. id is the cursor for the slice 3 feed; ingest is serialized so ids commit in order.
create table event (
    id          bigint generated always as identity primary key,
    machine_id  bigint not null references machine (id),
    kind        text not null check (kind in (
                    'machine_enrolled', 'machine_approved', 'repo_added', 'repo_removed', 'commit_seen',
                    'dirty', 'clean', 'ahead_behind', 'branch_changed', 'service_up', 'service_down')),
    occurred_at timestamptz not null,  -- best known time: authored_at for commits, else scanned_at
    recorded_at timestamptz not null default now(),
    repo_path   text,
    subject     text,                  -- sha or service name
    data        jsonb not null default '{}'
);
create index event_machine_time on event (machine_id, occurred_at);
create index event_kind_time on event (kind, occurred_at);
