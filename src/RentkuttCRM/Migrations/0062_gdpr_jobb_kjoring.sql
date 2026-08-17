-- 0062 — Kjøringslogg for GDPR-bakgrunnsjobber (vedlegg A). Én rad per FORSØK: opprettes ved start,
-- oppdateres ved fullføring. fullfort_at = null betyr «startet, men kom ikke i mål» (drept jobb / feil).
-- Alarmene måler TILSTAND (fravær av vellykket kjøring), ikke en engangshendelse.
create table if not exists public.gdpr_jobb_kjoring (
    id               bigserial   primary key,
    jobb             text        not null,          -- anonymisering | sletting | reparasjon
    startet_at       timestamptz not null default now(),
    fullfort_at      timestamptz,                    -- null = ikke fullført
    antall_behandlet int,
    feilmelding      text
);
-- Rask oppslag av siste fullførte kjøring per jobbtype (driver Alarm 1).
create index if not exists idx_gdpr_jobb_kjoring_jobb_fullfort
    on public.gdpr_jobb_kjoring (jobb, fullfort_at desc);

alter table public.gdpr_jobb_kjoring enable row level security;
