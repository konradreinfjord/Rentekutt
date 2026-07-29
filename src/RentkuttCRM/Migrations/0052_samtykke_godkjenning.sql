-- 0052 — admin-godkjenninger av samtykke (fireøyne-prinsipp). Når TO ULIKE admin-brukere har
-- godkjent samtykke på et kundekort, opprettes et gyldig samtykke (kilde «Admin dobbeltgodkjenning»).
-- Alternativ til kundens egen 2FA-signering, med sporbarhet på hvem som godkjente.
create table if not exists public.samtykke_godkjenning (
    id            uuid primary key default gen_random_uuid(),
    kundekort_id  uuid not null,
    godkjent_av   text not null,
    godkjent_navn text,
    godkjent_at   timestamptz not null default now()
);

create index if not exists idx_samtykke_godkjenning_kort on public.samtykke_godkjenning(kundekort_id);
