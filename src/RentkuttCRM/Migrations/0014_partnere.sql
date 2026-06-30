-- 0014 — bankpartnere (persistente, ikke kun i minnet)
create table if not exists public.partnere (
    id         uuid primary key default gen_random_uuid(),
    navn       text not null,
    provisjon  text,
    engangssum text,
    created_at timestamptz not null default now()
);

alter table public.partnere enable row level security;
