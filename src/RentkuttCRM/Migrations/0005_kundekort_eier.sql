-- 0005 — eierskap til sak (kundekort). eier = e-post til bruker som eier saken.
alter table public.kundekort
    add column if not exists eier      text,
    add column if not exists eier_navn text;

create index if not exists kundekort_eier_idx on public.kundekort (eier);
