-- 0029 — persister rutingsreglene (logikk-matrisen) i databasen.
-- Reglene driver «Forslag bank» i person-/bedriftsmarkedet: matcher en regel
-- (f.eks. postnummer mellom 7000-8000 → Sokndal Sparebank), foreslås banken.
create table if not exists public.rutingsregel (
    id          uuid primary key default gen_random_uuid(),
    prioritet   int not null default 1,
    felt_nokkel text not null,          -- feltkatalog-nøkkel, f.eks. «postnummer»
    operator    text not null,          -- «=», «mellom», «inneholder» …
    verdi       text not null,          -- f.eks. «7000-8000»
    banker      text not null default '',-- komma-separerte banknavn
    aktiv       boolean not null default true,
    created_at  timestamptz not null default now()
);

create index if not exists idx_rutingsregel_prioritet on public.rutingsregel(prioritet);

-- RLS på: appen kobler server-side med service_role (omgår RLS). Ingen policyer = anon nektes.
alter table public.rutingsregel enable row level security;
