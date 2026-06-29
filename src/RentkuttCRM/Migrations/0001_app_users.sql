-- 0001 — brukere for innlogging + brukeradministrasjon
create extension if not exists "pgcrypto";

create table if not exists public.app_users (
    id            uuid primary key default gen_random_uuid(),
    email         text not null unique,
    full_name     text not null default '',
    role          text not null default 'Saksbehandler'
                  check (role in ('Saksbehandler', 'Compliance', 'Leder', 'Administrator')),
    active        boolean not null default true,
    password_hash text not null default '',
    created_at    timestamptz not null default now()
);

create index if not exists app_users_email_idx on public.app_users (lower(email));

alter table public.app_users enable row level security;
