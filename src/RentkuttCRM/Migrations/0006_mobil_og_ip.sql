-- 0006 — mobilnummer på bruker (for 2FA) + IP-whitelist på webhook
alter table public.app_users add column if not exists mobilnummer text;
alter table public.webhooks  add column if not exists ip_allowlist text;
