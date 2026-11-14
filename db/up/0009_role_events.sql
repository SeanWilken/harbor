-- Role switches show in the history (0008 allowed the job but not its event).
alter table event drop constraint event_kind_check;
alter table event add constraint event_kind_check check (kind in (
    'machine_enrolled', 'machine_approved', 'machine_rejected', 'repo_added', 'repo_removed', 'commit_seen',
    'dirty', 'clean', 'ahead_behind', 'branch_changed', 'service_up', 'service_down', 'job_finished',
    'job_requested', 'repo_hidden', 'repo_unhidden', 'manifest_changed', 'role_changed'));
