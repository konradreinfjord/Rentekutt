-- 0074 — Gi nytt navn til status «Sendt - I prosess» → «Sendt til bank», og fjern «Sendt - Innvilget».
-- «Sendt - Innvilget» var ikke i bruk; eventuelle rader slås sammen med «Sendt til bank».
update public.kundekort set status = 'Sendt til bank'   where status = 'Sendt - I prosess';
update public.kundekort set status = 'Sendt til bank'   where status = 'Sendt - Innvilget';
