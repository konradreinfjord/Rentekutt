-- 0015 — delegert til bank (hvilken partner saken er sendt til)
alter table public.kundekort add column if not exists delegert_bank text;
