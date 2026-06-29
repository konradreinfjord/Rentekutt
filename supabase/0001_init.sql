-- Rentekutt — minimum oppsett for innlogging + brukeradministrasjon
-- Kjør dette i Supabase: Dashboard → SQL Editor → Run.
--
-- Serveren (ASP.NET) bruker SERVICE_ROLE-nøkkelen og snakker med denne tabellen.
-- Passord hashes i appen (ASP.NET PasswordHasher) — aldri lagret i klartekst.

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

-- RLS på. Serveren bruker service_role-nøkkel som uansett bypasser RLS.
-- Ingen anon-tilgang → ingen kan lese brukertabellen fra nettleseren.
alter table public.app_users enable row level security;

-- (Valgfritt) Hvis du senere vil lese fra klient med anon-nøkkel, legg til
-- eksplisitte policies her. Foreløpig: ingen policies = kun service_role.

-- Første admin opprettes automatisk av appen ved første oppstart
-- (admin@rentekutt.no) hvis tabellen er tom. Du kan endre passordet etterpå.
