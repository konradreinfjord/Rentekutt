-- 0034 — hvilket bankprodukt en søknad ble sendt på.
-- produkt_kode brukes av sendekøen til å rute til riktig Instabank-produkt.
alter table public.banksending add column if not exists produkt      text;
alter table public.banksending add column if not exists produkt_kode int;
