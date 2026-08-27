-- 0064 — Dialer (Zisson/Wave click-to-call).
-- 1) Kobling CRM-bruker → Zisson-agent (agentGuid): styrer hvilken telefon click-to-call ringer.
alter table public.app_users add column if not exists zisson_agent_guid text;

-- 2) Utgående anrop startet via dialeren. Én rad pr. klikk. Bakgrunnsjobben (ZissonCdrWorker)
--    henter utfall/varighet fra Zisson CDR og oppdaterer loggen når samtalen er avsluttet.
create table if not exists public.dialer_anrop (
    id            uuid        primary key default gen_random_uuid(),
    kundekort_id  uuid        not null,
    aktor         text,                              -- hvem som ringte (navn/e-post)
    agent_guid    text,                              -- Zisson-agenten som ringte
    til_nummer    text,                              -- normalisert nummer (E.164)
    zid           text,                              -- Zisson samtale-id (fra click-to-call-svaret)
    startet_at    timestamptz not null default now(),
    status        text        not null default 'uavklart',  -- uavklart | ferdig | feilet
    utfall        text,                              -- svart | ikke_svart | ukjent
    taletid_sek   int,
    ferdig_at     timestamptz
);

-- Bakgrunnsjobben plukker uavklarte anrop – indeks for raskt oppslag.
create index if not exists idx_dialer_anrop_status
    on public.dialer_anrop (status, startet_at desc);
create index if not exists idx_dialer_anrop_kundekort
    on public.dialer_anrop (kundekort_id, startet_at desc);

-- RLS på (service_role bypasser; ingen public-tilgang) – som øvrige tabeller (migr. 0055).
alter table public.dialer_anrop enable row level security;
