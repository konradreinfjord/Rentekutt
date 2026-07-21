-- 0037 — tving PostgREST til å laste skjema-cachen på nytt.
-- Nye tabeller/kolonner lagt til via direkte DB-tilkobling (Npgsql-migrasjonene,
-- f.eks. partner_produkt og kolonnen laanetyper) blir ikke synlige for REST-laget
-- før cachen lastes på nytt. Klienten (Supabase C#) går via PostgREST, så uten
-- dette feiler insert mot partner_produkt stille («legg til produkt gjør ingenting»).
notify pgrst, 'reload schema';
