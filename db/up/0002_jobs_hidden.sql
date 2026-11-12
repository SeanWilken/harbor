-- Work the hub asks an agent to do. Delivered in the reply to the agent's next push; the result
-- comes back in a later push. kind must also be in the agent's own `allow` list to run.
create table job (
    id           bigint generated always as identity primary key,
    machine_id   bigint not null references machine (id),
    kind         text not null check (kind in ('fetch')),
    path         text,           -- null = whole machine
    requested_at timestamptz not null default now(),
    requested_by text not null,  -- 'local' or the Tailscale login
    sent_at      timestamptz,    -- last handed to the agent; resent if no result after a while
    finished_at  timestamptz,
    ok           boolean,
    output       text
);
create index job_open on job (machine_id) where finished_at is null;

-- Repos the user chose not to see. The agent still reports them; the hub filters.
create table repo_hidden (
    machine_id bigint not null references machine (id),
    path       text not null,
    hidden_at  timestamptz not null default now(),
    primary key (machine_id, path)
);

alter table event drop constraint event_kind_check;
alter table event add constraint event_kind_check check (kind in (
    'machine_enrolled', 'machine_approved', 'repo_added', 'repo_removed', 'commit_seen',
    'dirty', 'clean', 'ahead_behind', 'branch_changed', 'service_up', 'service_down', 'job_finished'));
