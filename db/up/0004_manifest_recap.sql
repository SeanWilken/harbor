-- Installed-software manifests: one row per distinct state of a machine (a new row only when the
-- agent's content hash changes), so the rows are that machine's change history.
create table manifest (
    id            bigint generated always as identity primary key,
    machine_id    bigint not null references machine (id),
    taken_at      timestamptz not null,
    received_at   timestamptz not null default now(),
    hash          text not null,
    os            text not null,           -- pretty name, for lists
    package_count int not null,
    payload       jsonb not null
);
create index manifest_machine on manifest (machine_id, id desc);

-- Weekly recaps as generated, so last month's recap still reads as it did then.
create table recap (
    id           bigint generated always as identity primary key,
    period_start timestamptz not null,
    period_end   timestamptz not null unique,
    created_at   timestamptz not null default now(),
    payload      jsonb not null,
    markdown     text not null
);

alter table event drop constraint event_kind_check;
alter table event add constraint event_kind_check check (kind in (
    'machine_enrolled', 'machine_approved', 'machine_rejected', 'repo_added', 'repo_removed', 'commit_seen',
    'dirty', 'clean', 'ahead_behind', 'branch_changed', 'service_up', 'service_down', 'job_finished',
    'job_requested', 'repo_hidden', 'repo_unhidden', 'manifest_changed'));
