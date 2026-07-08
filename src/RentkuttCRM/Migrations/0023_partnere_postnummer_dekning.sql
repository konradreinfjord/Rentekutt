-- 0023 — postnummer-dekning per bankpartner (for forslag til bank i Database)
-- Fritekst: kommaseparerte postnummer og/eller intervaller, f.eks. "0001-1299, 5000, 7000-7099".
alter table public.partnere add column if not exists postnummer_dekning text;
