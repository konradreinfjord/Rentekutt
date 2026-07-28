-- 0049 — eiendoms-/sikkerhetsfelt for boliglån (Instabank produkt 180).
-- Feltene fylles av kunderådgiver i «G · Boliglån»-widgeten og mappes til Instabanks
-- Application.Securities[].Items[]. Matrikkel (kommune/gnr/bnr/fnr/snr) er påkrevd for selveier;
-- borettslag-feltene (andelsnr/orgnr/fellesgjeld/felleskostnad) for andel/sameie.
-- «Debt» (restgjeld på eiendommen) hentes fra det eksisterende feltet boliggjeld.
alter table public.kundekort
    add column if not exists eiendom_kommune              text,
    add column if not exists eiendom_kommunenummer        integer,
    add column if not exists eiendom_gaardsnummer          integer,
    add column if not exists eiendom_bruksnummer           integer,
    add column if not exists eiendom_festenummer           integer,
    add column if not exists eiendom_seksjonsnummer        integer,
    add column if not exists eiendom_andelsnummer          text,
    add column if not exists eiendom_borettslag_orgnr      text,
    add column if not exists eiendom_fellesgjeld           numeric,
    add column if not exists eiendom_felleskostnad         numeric,
    add column if not exists eiendom_estimert_verdi        numeric,
    add column if not exists eiendom_etakst_referanse      text,
    add column if not exists eiendom_forsikret             boolean not null default false,
    add column if not exists eiendom_forsikringsselskap    text;
