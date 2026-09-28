-- 0071 — Omkategoriser «Refinansiering» → «Boliglån».
-- Engangs datakorreksjon: alle søknader med lånetype «Refinansiering» settes til «Boliglån»,
-- MED unntak av Heimen Dahlen, som fortsatt skal stå som «Refinansiering».
-- laanetype og fullt_navn ligger i klartekst (kun fnr/kunde_id er kryptert), så matching i SQL er trygt.
update public.kundekort
set laanetype = 'Boliglån'
where laanetype = 'Refinansiering'
  and coalesce(fullt_navn, '') not ilike '%heimen dahlen%';
