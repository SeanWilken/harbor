-- Alerts: a condition the hub noticed (machine stopped reporting, site down, ...). One open row per
-- (rule, subject); it resolves when the condition clears. acked_at = someone marked it seen. A
-- condition that clears and comes back is a new row, so it's unseen again.
create table alert (
    id          bigint generated always as identity primary key,
    rule        text not null,
    subject     text not null,
    machine_id  bigint references machine (id) on delete cascade,
    severity    text not null check (severity in ('warn', 'critical')),
    title       text not null,
    detail      text,
    opened_at   timestamptz not null,
    seen_at     timestamptz not null, -- last evaluation that still found the condition
    resolved_at timestamptz,
    acked_at    timestamptz,
    acked_by    text
);
create unique index alert_open on alert (rule, subject) where resolved_at is null;
create index alert_unacked on alert (opened_at) where acked_at is null;
