-- 0021 — oppfølging (siste kontakt / neste oppfølging), tidsstemplede saksnotater

alter table public.kundekort
    add column if not exists siste_kontakt    timestamptz,
    add column if not exists neste_oppfolging timestamptz;

-- Egen logg-tabell: hvert notat er én rad med tidsstempel og forfatter.
create table if not exists public.saksnotat (
    id             uuid primary key default gen_random_uuid(),
    kundekort_id   uuid not null references public.kundekort(id) on delete cascade,
    tekst          text not null,
    forfatter      text,
    forfatter_navn text,
    opprettet      timestamptz not null default now()
);

create index if not exists saksnotat_kundekort_idx on public.saksnotat (kundekort_id, opprettet desc);
