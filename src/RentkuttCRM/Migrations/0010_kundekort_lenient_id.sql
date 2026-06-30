-- 0010 — tillat fleksibel kunde_id (fødselsnr, mobil eller fallback).
-- API skal kunne opprette saker også fra payloads med lite data.
alter table public.kundekort drop constraint if exists kundekort_id_lengde;
