-- 0075 — Slå sammen «Avsluttet» → «Avslått», og legg til undergrunn for avslag.
-- Ny kolonne avslag_grunn lagrer undergrunn (Soknedal-webhook «grunn» eller Instabank-respons).
alter table public.kundekort add column if not exists avslag_grunn text;

-- Merge: alle «Avsluttet»-saker blir «Avslått» (én felles status).
update public.kundekort set status = 'Avslått' where status = 'Avsluttet';
