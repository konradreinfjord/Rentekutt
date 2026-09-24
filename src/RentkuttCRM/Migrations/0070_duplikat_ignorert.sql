-- 0070 — «Ignorer duplikat»: lar en admin markere en duplikat-gruppe som gjennomgått/ikke-duplikat,
-- så den skjules fra duplikatlista. Signaturen er et fingeravtrykk av kortenes id-er i gruppen, så
-- en gruppe dukker opp igjen automatisk hvis et NYTT kort kommer til (da endres signaturen).
create table if not exists public.duplikat_ignorert (
    signatur     text primary key,
    kilde        text,
    kundetype    text,
    nummer       text,
    ignorert_at  timestamptz not null default now(),
    ignorert_av  text
);
alter table public.duplikat_ignorert enable row level security;
