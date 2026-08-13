-- 0058 — Merknadsregler: gir kundekort en fargebadge (f.eks. grønn «Boliglån UNG» /
-- «Grønt boliglån») basert på betingelser mot kundekort-parametere. Samme betingelsesmodell
-- som rutingsregel (felt/operator/verdi), men gir en badge (tekst + farge) i stedet for banker.
create table if not exists public.merknadsregel (
    id           uuid        primary key default gen_random_uuid(),
    prioritet    int         not null default 1,
    felt_nokkel  text        not null,
    operator     text        not null,
    verdi        text        not null,
    badge_tekst  text        not null default '',
    badge_farge  text        not null default 'gronn',
    aktiv        boolean     not null default true,
    created_at   timestamptz not null default now()
);
create index if not exists idx_merknadsregel_prioritet on public.merknadsregel (prioritet);

-- RLS på (jf. 0055): kun service_role (appen) har tilgang, ingen offentlige policies.
alter table public.merknadsregel enable row level security;
