-- 0061 — Remapp eksisterende kundekort til den nye statusmodellen.
-- Beholdes uendret: 'Nytt lead', 'Påbegynt søknad', 'Avslått', 'Sendt til bank - Timeout'.
update public.kundekort set status = 'Ny søknad'         where status = 'Åpen';
update public.kundekort set status = 'Pågår - Agent'     where status in ('Pågår', 'Manuell behandling');
update public.kundekort set status = 'Sendt - I prosess' where status = 'Sendt bank';
update public.kundekort set status = 'Sendt - Innvilget' where status = 'Tilbud utsendt';
update public.kundekort set status = 'Utbetalt'          where status = 'Fullført og utbetalt';
update public.kundekort set status = 'Teknisk feil'      where status = 'Feilet i sending';
