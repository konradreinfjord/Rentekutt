-- 0030 — sikker sendekø: status «I kø» + forsøksteller.
-- Sendinger legges i kø (status «I kø») og plukkes throttlet av en bakgrunnsarbeider,
-- slik at vi aldri bombarderer bank-API-et (rate limiting). forsok teller antall forsøk.
alter table public.banksending add column if not exists forsok int not null default 0;

create index if not exists idx_banksending_ko on public.banksending(status, sendt_at);
