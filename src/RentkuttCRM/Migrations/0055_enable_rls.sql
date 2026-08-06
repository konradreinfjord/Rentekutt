-- 0055 — Slå PÅ Row-Level Security (RLS) på alle tabeller i public-skjemaet.
--
-- Bakgrunn: Supabase-advarsel «rls_disabled_in_public» (KRITISK). Uten RLS kan
-- hvem som helst med prosjekt-URL + den offentlige anon-nøkkelen lese, endre og
-- slette data via PostgREST.
--
-- Denne portalen er en Blazor SERVER-app som KUN snakker med databasen via
-- service_role-nøkkelen (server-side, aldri eksponert i nettleseren). service_role
-- har BYPASSRLS og omgår RLS fullstendig. Det samme gjelder tabell-eieren som
-- migrasjonene kjører som. Å slå PÅ RLS UTEN policies låser derfor ute
-- anon/authenticated-rollene (offentlig tilgang) uten å påvirke appen.
--
-- Vi legger BEVISST ingen policies: ingen offentlig tilgang er ønsket.
-- Kjøres på nytt uten skade (enable row level security er idempotent).
do $$
declare r record;
begin
  for r in
    select c.relname
    from pg_class c
    join pg_namespace n on n.oid = c.relnamespace
    where n.nspname = 'public'
      and c.relkind = 'r'   -- kun vanlige tabeller
  loop
    execute format('alter table public.%I enable row level security', r.relname);
  end loop;
end $$;
