-- 0032 — eide saker som fortsatt står «Åpen» flyttes til «Pågår».
-- Fra nå settes status automatisk til «Pågår» når en rådgiver tar eierskap;
-- denne engangs-oppdateringen tar de som allerede er eid men ikke bumpet.
update public.kundekort
   set status = 'Pågår'
 where eier is not null
   and eier <> ''
   and status = 'Åpen';
