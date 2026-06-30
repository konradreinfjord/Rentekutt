-- 0008 — generell innstillingstabell (key/value), bl.a. SMS-rategrenser
create table if not exists public.innstillinger (
    nokkel text primary key,
    verdi  text
);

alter table public.innstillinger enable row level security;
