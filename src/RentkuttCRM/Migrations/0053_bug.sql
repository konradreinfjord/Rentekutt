-- 0053 — Bugs-fane: internt meldingsspor mellom kundeservice og admin/utvikling.
-- Én tabell, ingen relasjoner. opprettet_av er visningsnavn (tekst), ikke fremmednøkkel.
create table if not exists public.bug (
    id                uuid primary key default gen_random_uuid(),
    kategori          text,
    beskrivelse       text        not null default '',
    status            text        not null default 'ikke_registrert',
    opprettet_av      text,
    opprettet_at      timestamptz not null default now(),
    oppdatert_at      timestamptz not null default now(),
    teknisk_kommentar text,       -- skrives av admin
    info_fra_ks       text        -- skrives av kundeservice
);

create index if not exists idx_bug_status_opprettet on public.bug (status, opprettet_at desc);
