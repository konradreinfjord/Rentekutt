-- 0040 — marker anonymiserte kundekort (GDPR). Settes av den automatiske GDPR-jobben
-- (GdprService/GdprWorker) slik at rader ikke anonymiseres på nytt, og for etterprøving.
alter table public.kundekort add column if not exists anonymisert_at timestamptz;

create index if not exists idx_kundekort_created_at on public.kundekort(created_at);
