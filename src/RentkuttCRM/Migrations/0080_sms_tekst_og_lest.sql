-- 0080 — Lagre meldingsteksten på utgående SMS, og «lest»-markering på innkommende svar.
-- Gjør det mulig å vise hele SMS-korrespondansen (ut + inn) på hver sak, og å varsle om nye
-- kundesvar (utropstegn på saksrad + notifikasjonssenter) til agenten har sett dem.
alter table public.sms_utsending  add column if not exists tekst text;
alter table public.innkommende_sms add column if not exists lest boolean not null default false;

create index if not exists innkommende_sms_ulest_idx on public.innkommende_sms (lest) where lest = false;
