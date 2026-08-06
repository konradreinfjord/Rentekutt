-- 0056 — Kommunikasjon: sporing av automatiske SMS-utsendinger.
-- Brukes både til å hindre duplikater (send 24t-påminnelse kun én gang per sak) og
-- som logg i Kommunikasjon-fanen. Ingen FK til kundekort (robust ved sletting/anonymisering);
-- kundekort_id brukes kun til oppslag.
create table if not exists public.sms_utsending (
    id           uuid primary key default gen_random_uuid(),
    kundekort_id uuid        not null,
    type         text        not null,           -- f.eks. 'paamindelse_24t'
    mobil        text,
    ok           boolean     not null default false,
    detalj       text,
    sendt_at     timestamptz not null default now()
);
create index if not exists idx_sms_utsending_kunde_type on public.sms_utsending (kundekort_id, type);
create index if not exists idx_sms_utsending_sendt on public.sms_utsending (sendt_at desc);

-- RLS på (jf. 0055): kun service_role (appen) skal ha tilgang, ingen offentlige policies.
alter table public.sms_utsending enable row level security;
