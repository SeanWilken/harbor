-- Role switches: arg is a role name the machine's own config declares (the hub checks it against
-- the latest snapshot; the agent checks it again).
alter table job drop constraint job_kind_check;
alter table job add constraint job_kind_check check (kind in ('fetch', 'pull', 'diff', 'role'));
