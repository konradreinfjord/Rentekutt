-- 0033 — produkter per bankpartner.
-- Provisjon/engangssum settes per produkt (bank-nivået i partnere beholdes som
-- fallback for banker uten definerte produkter). Instabank-produktene har faste
-- API-koder; manuelle banker har kode = null. Segment styrer hvilke produkter
-- som vises for privat (B2C) vs bedrift (B2B) på kundekortet.
create table if not exists public.partner_produkt (
    id         uuid primary key default gen_random_uuid(),
    partner_id uuid not null references public.partnere(id) on delete cascade,
    navn       text not null,
    kode       int,                              -- API-produktkode (Instabank); null for manuelle
    segment    text not null default 'privat',   -- 'privat' | 'bedrift'
    provisjon  text,
    engangssum text,
    aktiv      boolean not null default true,
    sortering  int not null default 0,
    created_at timestamptz not null default now()
);

create index if not exists idx_partner_produkt_partner on public.partner_produkt(partner_id);

-- RLS på: appen kobler server-side med service_role (omgår RLS). Ingen policyer = anon nektes.
alter table public.partner_produkt enable row level security;

-- Seed Instabank sine faste produkter for eksisterende Instabank-partner(e).
-- Idempotent: kun der samme produktkode ikke allerede finnes for partneren.
insert into public.partner_produkt (partner_id, navn, kode, segment, sortering)
select p.id, x.navn, x.kode, x.segment, x.sortering
from public.partnere p
cross join (values
    ('Forbrukslån',     151,  'privat',  1),
    ('Kredittlinje',    251,  'privat',  2),
    ('Kredittkort',     600,  'privat',  3),
    ('Bedriftslån',     2001, 'bedrift', 4),
    ('Bedriftskreditt', 2000, 'bedrift', 5)
) as x(navn, kode, segment, sortering)
where lower(replace(p.navn, ' ', '')) like '%instabank%'
  and not exists (
      select 1 from public.partner_produkt pp
      where pp.partner_id = p.id and pp.kode = x.kode
  );
