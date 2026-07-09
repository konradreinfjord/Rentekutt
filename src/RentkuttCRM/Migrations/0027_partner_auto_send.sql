-- 0027 — automatisk sending per bankpartner.
-- Når auto_send = true rutes matchende søknader automatisk til banken.
-- Når auto_send = false vises banken i stedet som «foreslått bank» i markedet
-- (postnummer-dekning treffer, men mennesket bestemmer).
alter table public.partnere add column if not exists auto_send boolean not null default false;
