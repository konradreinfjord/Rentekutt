-- 0045 — rettslig grunnlag (behandlingsgrunnlag) per lead (GDPR art. 6, art. 30).
-- Dokumenterer på hver søknad hvilket behandlingsgrunnlag vi støtter oss på.
-- Innkommende leads settes som «Samtykke» (kunden har samtykket i lead-skjemaet);
-- kan justeres per sak (f.eks. «Avtale» når kundeforholdet er etablert).
alter table public.kundekort
    add column if not exists behandlingsgrunnlag text;

update public.kundekort set behandlingsgrunnlag = 'Samtykke' where behandlingsgrunnlag is null;
