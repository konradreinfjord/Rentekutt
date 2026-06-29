-- 0003 — Webhooks (inbound/outbound). Inbound mottar leads → mappes til kundekort.
create table if not exists public.webhooks (
    id         uuid primary key default gen_random_uuid(),
    name       text not null,
    direction  text not null default 'inbound' check (direction in ('inbound', 'outbound')),
    token      text not null,
    active     boolean not null default true,
    created_at timestamptz not null default now()
);

alter table public.webhooks enable row level security;
