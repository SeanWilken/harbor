-- Outside-in checks of websites listed in hub.sites (any host, e.g. DigitalOcean). One row per
-- check; retention keeps 30 days. Sites themselves live in the hub config, keyed by name.
create table site_check (
    id           bigint generated always as identity primary key,
    site         text not null,
    checked_at   timestamptz not null,
    ok           boolean not null,
    status       int,
    ms           int,
    error        text,
    cert_expires timestamptz,
    cert_issuer  text,
    version      text
);
create index site_check_site_time on site_check (site, checked_at);
