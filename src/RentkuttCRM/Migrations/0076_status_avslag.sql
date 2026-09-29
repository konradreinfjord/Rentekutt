-- 0076 — Gi status «Avslått» nytt navn «Avslag».
-- Gjelder kundekort-status (ikke per-bank utfall i bank_sending, som beholder «Avslått»).
-- Kjøres etter 0075 (som bl.a. setter «Avsluttet» → «Avslått»), så begge fanges opp.
update public.kundekort set status = 'Avslag' where status = 'Avslått';
