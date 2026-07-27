-- 0046 — lagrer innkommende webhook-payloads (feilsøking/sporbarhet). Vi beholder kun de
-- siste ~50 (trimmes ved skriving). Fødselsnummer maskeres før lagring (samme redaksjon som logg).
create table if not exists public.webhook_payload (
    id           uuid primary key default gen_random_uuid(),
    kanal        text,
    payload      text,
    ok           boolean not null default true,
    feil         text,
    kundekort_id uuid,
    mottatt      timestamptz not null default now()
);

create index if not exists idx_webhook_payload_mottatt on public.webhook_payload(mottatt desc);

alter table public.webhook_payload enable row level security;
