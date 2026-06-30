-- 0007 — 2FA (SMS via LinkMobility) per bruker
alter table public.app_users add column if not exists twofa_enabled boolean not null default false;
