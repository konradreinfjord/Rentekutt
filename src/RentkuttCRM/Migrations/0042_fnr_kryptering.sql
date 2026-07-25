-- 0042 — feltnivåkryptering av fødselsnummer.
-- Fødselsnummer/medsøker-fnr og kunde_id krypteres i ro av appen (AES-256-GCM);
-- for likhets-oppslag på det krypterte fnr legges en søkbar HMAC-kolonne med indeks.
-- (Selve krypteringen/bakfyllingen gjøres i koden når Gdpr__FieldKey er satt.)
alter table public.kundekort
    add column if not exists fnr_hmac text;

create index if not exists idx_kundekort_fnr_hmac on public.kundekort(fnr_hmac);
