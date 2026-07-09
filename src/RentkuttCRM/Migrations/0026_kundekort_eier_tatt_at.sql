-- 0026 — tidspunkt for når eierskap ble tatt på en sak.
-- Brukes til dashbord: «eierskap tatt per dag» og alder/snitt-tid på pågående, eide saker.
alter table public.kundekort add column if not exists eier_tatt_at timestamptz;

-- Backfill: eksisterende saker som allerede har eier, men mangler tidspunkt.
-- Beste tilgjengelige proxy er updated_at (fallback created_at).
update public.kundekort
   set eier_tatt_at = coalesce(updated_at, created_at)
 where eier is not null
   and eier <> ''
   and eier_tatt_at is null;
