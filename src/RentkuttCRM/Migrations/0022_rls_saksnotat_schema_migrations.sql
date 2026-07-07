-- 0022 — sikkerhet: skru på Row Level Security på tabeller som manglet det.
--
-- saksnotat ble opprettet i 0021 uten RLS (glipp — alle andre tabeller har det).
-- schema_migrations (intern migrasjonslogg) fikk aldri RLS.
--
-- Uten RLS kan hvem som helst med den offentlige anon-nøkkelen lese/endre/slette
-- radene via PostgREST. Appen kobler kun server-side med service_role (omgår RLS),
-- så dette bryter ingenting. Ingen policyer = anon/authenticated nektes alt.

alter table public.saksnotat         enable row level security;
alter table public.schema_migrations enable row level security;
