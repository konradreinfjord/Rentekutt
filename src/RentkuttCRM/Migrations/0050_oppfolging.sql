-- 0050 — oppfølgings-pipeline: oppgaver (forfallsdato + notat) per søknad, eid av en rådgiver.
-- Flere per søknad. Åpne oppfølginger synkroniseres til kundekort.neste_oppfolging for visning.
create table if not exists public.oppfolging (
    id            uuid primary key default gen_random_uuid(),
    kundekort_id  uuid not null references public.kundekort(id) on delete cascade,
    eier          text,
    forfaller     date not null,
    notat         text,
    fullfort      boolean not null default false,
    fullfort_at   timestamptz,
    opprettet_av  text,
    created_at    timestamptz not null default now()
);

create index if not exists idx_oppfolging_kundekort on public.oppfolging(kundekort_id);
create index if not exists idx_oppfolging_eier_aapen on public.oppfolging(eier) where fullfort = false;
