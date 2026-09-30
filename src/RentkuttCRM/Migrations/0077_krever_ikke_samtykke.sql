-- 0077 — Banker som kun mottar kontaktinfo via webhook (ingen registeroppslag) krever ikke
-- GDPR-samtykke (gjeldsregister/kredittsjekk) før sending. Slås PÅ for Soknedal Sparebank.
alter table public.partnere add column if not exists krever_ikke_samtykke boolean not null default false;
update public.partnere set krever_ikke_samtykke = true where navn = 'Soknedal Sparebank';
