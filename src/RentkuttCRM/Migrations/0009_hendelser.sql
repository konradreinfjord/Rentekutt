-- 0009 — hendelseslogg (systemhendelser, ingen PII)
create table if not exists public.hendelser (
    id          uuid primary key default gen_random_uuid(),
    tidspunkt   timestamptz not null default now(),
    type        text not null,
    beskrivelse text,
    kilde       text
);

create index if not exists hendelser_tidspunkt_idx on public.hendelser (tidspunkt desc);

alter table public.hendelser enable row level security;
