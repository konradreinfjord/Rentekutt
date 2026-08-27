-- 0063 — Fagforening på kundekort (vises under Lånedetaljer). Fritekst/valgt fra dynamisk liste.
alter table public.kundekort add column if not exists fagforening text;
