-- 0059 — Tidspunkt en sak ble satt til «Sendt bank». Driver auto-timeout: etter X dager
-- (innstilling sendt_bank_timeout_dager) settes saken automatisk til «Sendt til bank - Timeout».
-- Settes kun ved statusovergang til «Sendt bank»; eksisterende saker får den ved neste sending.
alter table public.kundekort add column if not exists sendt_bank_at timestamptz;
