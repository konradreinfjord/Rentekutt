-- 0066 — Auto-send av uferdige søknader («Påbegynt søknad») etter 30 min.
-- Egen per-bank bryter, uavhengig av vanlig auto_send: er den PÅ og et lead matcher
-- logikk-matrisen for banken, sendes leadet automatisk dersom det fortsatt står i status
-- «Påbegynt søknad» 30 minutter etter registrering. Kun nye leads (etter aktivering) rammes.
alter table public.partnere add column if not exists auto_paabegynt boolean not null default false;
