-- 0004 — spor sist mottatte data på webhooks (for kompakt visning)
alter table public.webhooks
    add column if not exists last_received_at   timestamptz,
    add column if not exists last_received_info text;
