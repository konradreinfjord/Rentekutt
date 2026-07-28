-- 0051 — bedriftslån-felt (Instabank company loan, produkt 2001). Brukes kun for B2B-kunder.
-- Fylles i «Bedriftslån»-widgeten (vises kun for B2B) og mappes til Applicant-/toppnivåfelt i payloaden.
alter table public.kundekort
    add column if not exists bedrift_ansatt_i_selskapet          boolean not null default false,
    add column if not exists bedrift_eierandel_over_25           boolean not null default false,
    add column if not exists bedrift_annen_selskapsgjeld         boolean not null default false,
    add column if not exists bedrift_omsetning_i_aar             numeric,
    add column if not exists bedrift_omsetning_neste_aar         numeric,
    add column if not exists bedrift_ny_gjeld_12mnd              boolean not null default false,
    add column if not exists bedrift_beskrivelse                 text,
    add column if not exists bedrift_markedsbeskrivelse          text,
    add column if not exists bedrift_laaneformaal                text,
    add column if not exists bedrift_laaneformaal_beskrivelse    text,
    add column if not exists bedrift_laaneformaal_annet          text,
    add column if not exists bedrift_kredittbruk                 text,
    add column if not exists bedrift_midlenes_opprinnelse        text,
    add column if not exists bedrift_midlenes_opprinnelse_annet  text,
    add column if not exists bedrift_stiller_sikkerhet           boolean not null default false,
    add column if not exists bedrift_sikkerhet_beskrivelse       text,
    -- Kontaktperson (signer) for B2B — personen bak firmaet. For B2B er fullt_navn firmanavnet.
    add column if not exists kontaktperson_navn                  text;
