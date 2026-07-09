-- 0028 — logg over søknader sendt til bank (manuelt eller via API).
-- Vises på kundekortet og som «siste sendte» under Bank API.
create table if not exists public.banksending (
    id           uuid primary key default gen_random_uuid(),
    kundekort_id uuid references public.kundekort(id) on delete cascade,
    kunde_navn   text,
    bank         text not null,
    status       text,          -- «Sendt», «Feilet», «Registrert manuelt»
    ekstern_ref  text,          -- ExternalReference fra bank (f.eks. Instabank)
    signing_url  text,          -- lenke kunden kan bruke for å fullføre
    detalj       text,
    sendt_av     text,
    sendt_at     timestamptz not null default now()
);

create index if not exists idx_banksending_kundekort on public.banksending(kundekort_id);
create index if not exists idx_banksending_tid on public.banksending(sendt_at desc);

-- RLS på: appen kobler server-side med service_role (omgår RLS). Ingen policyer = anon nektes.
alter table public.banksending enable row level security;
