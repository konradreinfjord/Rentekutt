-- 0078 — Fang innvilget beløp og Instabank sin egen id fra Instabank-responsen, så det kan vises
-- på kundekortet (faktisk innvilget beløp kan avvike fra omsøkt, og Instabank-id-en identifiserer saken hos banken).
alter table public.banksending add column if not exists innvilget_belop numeric;
alter table public.banksending add column if not exists instabank_id text;
