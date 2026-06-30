-- Døp om status "Ny" til "Åpen" på eksisterende kundekort + endre kolonne-default.
update public.kundekort set status = 'Åpen' where status = 'Ny';
alter table public.kundekort alter column status set default 'Åpen';
