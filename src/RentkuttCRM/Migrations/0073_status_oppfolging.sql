-- 0073 — Ny status «Oppfølging».
-- Alle søknader som har en oppfølgingsdato (neste_oppfolging satt) settes til status «Oppfølging»,
-- unntatt ferdige/positive slutt-tilstander som skal beholdes.
update public.kundekort
set status = 'Oppfølging'
where neste_oppfolging is not null
  and status not in ('Oppfølging', 'SBL Signert', 'Utbetalt', 'Avslått', 'Avsluttet', 'Kansellert', 'Sendt - Innvilget');
