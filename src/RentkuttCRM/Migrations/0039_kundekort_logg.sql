-- 0039 — endringslogg per kundekort (audit trail): hvem gjorde hva, når.
-- Registreres automatisk ved opprettelse, feltendringer (fra → til), statusendring,
-- eierskap og sending til bank. Vises i «Handlinger → Logg» på kundekortet.
create table if not exists public.kundekort_logg (
    id           uuid primary key default gen_random_uuid(),
    kundekort_id uuid not null references public.kundekort(id) on delete cascade,
    aktor        text,                 -- hvem (navn/e-post) eller «System»
    tekst        text not null,        -- menneskelesbar beskrivelse
    opprettet    timestamptz not null default now()
);

create index if not exists idx_kundekort_logg_kort on public.kundekort_logg(kundekort_id, opprettet desc);

alter table public.kundekort_logg enable row level security;
