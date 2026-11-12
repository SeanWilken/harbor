-- One-time pairing codes from "Pair a device". Only a hash is stored. A code approves the first
-- machine that presents it before it expires; machine identity is still Tailscale WhoIs.
create table pairing_code (
    id          bigint generated always as identity primary key,
    code_hash   text not null unique,
    created_by  text not null,           -- Tailscale login, or 'local'
    created_at  timestamptz not null default now(),
    expires_at  timestamptz not null,
    used_at     timestamptz,
    machine_id  bigint references machine (id)
);
