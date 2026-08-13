-- 0060 — Per-bank UTFALL på banksending (bankens beslutning), skilt fra sende-status.
-- Driver kundekortets samlede status i fler-bank-logikken (alle banker må avklares før endelig utfall).
alter table public.banksending add column if not exists utfall text not null default 'Venter';
