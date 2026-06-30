-- 0016 — boligverdi (for belåningsgrad/LTV)
alter table public.kundekort add column if not exists boligverdi numeric;
