-- 0057 — Produktkategori på kundekort (til Markedsinnsikt-inndeling + eksakt pris per produkt).
-- Fritekst/tekstverdi (én av de definerte kategoriene i Services/Produktkategori.cs). Nullbar:
-- når den ikke er satt, utleder appen kategori automatisk fra lånetype/alder/kundetype.
alter table public.kundekort add column if not exists produktkategori text;
