-- pull: fetch, then fast-forward a clean branch with no unpushed commits (agent-side checks).
alter table job drop constraint job_kind_check;
alter table job add constraint job_kind_check check (kind in ('fetch', 'pull'));
