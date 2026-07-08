-- 0024 — fylke på kundekort (fra 1881-berikelse; utledes ellers av kommune).
alter table public.kundekort add column if not exists fylke text;
