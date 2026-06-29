-- 0002 — Kundekort (lånesøknad). B2C nå; B2B utvides senere.
-- Identifikator: fødselsnummer (11 siffer) for B2C, orgnr (9 siffer) for B2B.
-- Alle data knyttes til denne id-en. RLS på — kun server (service_role) har tilgang.

create table if not exists public.kundekort (
    kunde_id    text primary key,                       -- fødselsnr (11) / orgnr (9)
    kunde_type  text not null default 'B2C' check (kunde_type in ('B2C', 'B2B')),

    -- A. Søker
    fullt_navn       text,
    foedselsnummer   text,                               -- 11 siffer (B2C)
    mobilnummer      text,
    epost            text,
    adresse          text,
    postnummer       text,
    poststed         text,

    -- B. Medsøker (kun ved valg)
    har_medsoker            boolean not null default false,
    medsoker_navn           text,
    medsoker_foedselsnummer text,
    medsoker_mobil          text,
    medsoker_epost          text,
    medsoker_adresse        text,
    medsoker_postnummer     text,
    medsoker_poststed       text,
    medsoker_inntekt        numeric,
    medsoker_arbeidsforhold text,

    -- C. Husholdning
    statsborgerskap        text,
    opprinnelsesland       text,
    aar_bodd_i_norge       int,
    sivilstatus            text,                          -- gift/samboer/singel/skilt/separert/enke(mann)
    antall_barn_under_18   int,
    boforhold              text,                          -- selveier/borettslag/leier/hos foreldre
    botid_mnd              int,                           -- botid på nåværende adresse (mnd)
    antall_biler           int,

    -- D. Arbeid og inntekt
    arbeidssituasjon       text,                          -- fast/selvstendig/offentlig/pensjonist/...
    arbeidsgiver           text,
    ansiennitet_mnd        int,
    utdanning              text,
    aarsinntekt_brutto     numeric,
    andre_inntekter        numeric,
    ektefelle_inntekt      numeric,

    -- E. Utgifter og forpliktelser
    boligkostnad_mnd       numeric,
    barnebidrag_betalt_mnd numeric,

    -- F. Gjeld (manuelt, verifiseres mot Gjeldsregisteret)
    boliggjeld             numeric,
    studielaan             numeric,
    billaan                numeric,
    forbruksgjeld          numeric,                       -- forbrukslån/kredittkort/inkasso totalt
    refinansieres_belop    numeric,
    aktiv_inkasso          boolean not null default false,

    -- G. Lånedetaljer
    onsket_laanebelop      numeric,
    onsket_lopetid_mnd     int,
    laanetype              text,                          -- forbrukslån/refinansiering/boliglån

    -- H. Utbetaling
    kontonummer            text,

    -- Meta
    status      text not null default 'Ny',
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),

    -- Id-lengde må matche kundetype
    constraint kundekort_id_lengde check (
        (kunde_type = 'B2C' and char_length(kunde_id) = 11)
        or (kunde_type = 'B2B' and char_length(kunde_id) = 9)
    )
);

alter table public.kundekort enable row level security;
