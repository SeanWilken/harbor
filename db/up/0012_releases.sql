-- Releases: a job whose path is a repo and whose arg is a JSON request (unit folder, profile,
-- steps). The agent checks it against what it detects on disk; it never carries a command.
alter table job drop constraint job_kind_check;
alter table job add constraint job_kind_check check (kind in ('fetch', 'pull', 'diff', 'role', 'release'));

-- How a unit is usually released, shared by every clone of the repo (keyed by its remote, or by
-- machine and path for a repo without one).
create table release_recipe (
    repo_key   text not null,
    unit       text not null, -- folder relative to the repo, '' = root
    profile    text,
    steps      text[] not null,
    updated_at timestamptz not null,
    updated_by text not null,
    primary key (repo_key, unit)
);
