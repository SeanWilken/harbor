-- Read-only diff jobs: arg names what to show (a commit sha or 'worktree'). Their output is code,
-- so the API shows it to admins only, and retention clears it after a day.
alter table job add column arg text;
alter table job drop constraint job_kind_check;
alter table job add constraint job_kind_check check (kind in ('fetch', 'pull', 'diff'));
