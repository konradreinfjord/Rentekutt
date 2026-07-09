-- 0025 — eget orgnr-felt på kundekort (B2B). Tidligere lå orgnr kun i kunde_id,
-- som også kan være mobil/lead-id. Nå mappes payload-feltet «orgnr» til denne kolonnen,
-- og «Orgnr» i UI leser herfra. kunde_id fortsetter å speile orgnr for gruppering.
alter table public.kundekort add column if not exists orgnr text;

-- Backfill: eksisterende B2B der kunde_id er et gyldig 9-sifret orgnr.
update public.kundekort
   set orgnr = kunde_id
 where kunde_type = 'B2B'
   and orgnr is null
   and kunde_id ~ '^[0-9]{9}$';
