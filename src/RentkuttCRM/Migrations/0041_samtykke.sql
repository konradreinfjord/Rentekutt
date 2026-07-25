-- 0041 — samtykke som egen entitet (GDPR). Dokumenterer formål, tekstversjon,
-- tidspunkt, kilde, IP og evt. utløp — i stedet for kun et boolsk flagg.
-- Kreves før oversendelse til bank / kredittvurdering (håndheves i koden).
create table if not exists public.samtykke (
    id           uuid primary key default gen_random_uuid(),
    kundekort_id uuid not null references public.kundekort(id) on delete cascade,
    formaal      text not null,               -- f.eks. «Gjeldsregister og kredittsjekk»
    gitt         boolean not null default true,
    tekstversjon text,                         -- hvilken samtykketekst kunden godtok
    kilde        text,                         -- Prismatch / Rentekutt.no / agentnavn
    ip           text,
    gitt_at      timestamptz not null default now(),
    utlop        timestamptz,                  -- null = ingen utløp
    created_at   timestamptz not null default now()
);

create index if not exists idx_samtykke_kort on public.samtykke(kundekort_id, gitt_at desc);

alter table public.samtykke enable row level security;
