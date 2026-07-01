-- SMS-maler som kan sendes til kunder fra kundekort.
create table if not exists public.sms_maler (
    id uuid primary key default gen_random_uuid(),
    navn text not null,
    tekst text not null,
    created_at timestamptz not null default now()
);

alter table public.sms_maler enable row level security;
