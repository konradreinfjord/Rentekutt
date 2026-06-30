-- 0013 — leadskilde på kundekort (Prismatch, Rentekutt.no, Manuell …)
alter table public.kundekort add column if not exists kilde text;
