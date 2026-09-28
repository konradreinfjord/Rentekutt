-- 0072 — Sett «Ukjent» lånetype → «Boliglån».
-- Engangs datakorreksjon: alle søknader uten valgt lånetype (null eller tom) settes til «Boliglån».
-- laanetype ligger i klartekst, så matching i SQL er trygt.
update public.kundekort
set laanetype = 'Boliglån'
where laanetype is null or btrim(laanetype) = '';
