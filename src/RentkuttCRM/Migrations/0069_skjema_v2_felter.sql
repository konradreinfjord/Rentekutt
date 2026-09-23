-- 0069 — Nye felter fra rentekutt-skjema v2 (nøstede bolig/gjeld-grupper).
-- har_usikret_gjeld: ja/nei — har kunden usikret gjeld / kredittkort (bolig.har_usikret_gjeld_eller_kredittkort).
-- belaaningsgrad: LTV i prosent kunden oppga (bolig.beregnet_belaaningsgrad_prosent).
-- naavaerende_laanebelop: nåværende lånebeløp (lanedetaljer.naavaerende_laanebelop).
-- (boligverdi finnes allerede — mappingen leser nå bolig.anslaatt_verdi inn i den.)
alter table public.kundekort add column if not exists har_usikret_gjeld boolean not null default false;
alter table public.kundekort add column if not exists belaaningsgrad numeric;
alter table public.kundekort add column if not exists naavaerende_laanebelop numeric;
