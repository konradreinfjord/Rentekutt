-- Supabase-linter: «RLS Disabled in Public» på innkommende_sms. Samme gjaldt fire eldre tabeller.
-- RLS på: appen kobler server-side med service_role (omgår RLS). Ingen policyer = anon/authenticated nektes alt.
alter table if exists public.innkommende_sms      enable row level security;
alter table if exists public.bug                  enable row level security;
alter table if exists public.bug_bilde            enable row level security;
alter table if exists public.oppfolging           enable row level security;
alter table if exists public.samtykke_godkjenning enable row level security;
