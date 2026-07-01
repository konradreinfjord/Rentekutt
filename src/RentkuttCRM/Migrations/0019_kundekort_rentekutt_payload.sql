-- Nye felt fra www.rentekutt.no-payloaden (skjema rentekutt_bekreftelse).
alter table public.kundekort
    add column if not exists tjeneste text,
    add column if not exists tjeneste_kode text,
    add column if not exists samtykke_gjeldsregister_kredittsjekk boolean not null default false,
    add column if not exists samlet_gjeld numeric,
    add column if not exists laaneformal text,
    add column if not exists laaneformal_kode text,
    add column if not exists naavaerende_rente numeric,
    add column if not exists skjema_versjon integer,
    add column if not exists statsborgerskap_kode text,
    add column if not exists sivilstatus_kode text,
    add column if not exists boforhold_kode text,
    add column if not exists arbeidssituasjon_kode text,
    add column if not exists utdanning_kode text,
    add column if not exists medsoker_arbeidssituasjon_kode text,
    add column if not exists har_andre_inntekter boolean not null default false,
    add column if not exists har_ektefelle_samboer_inntekt boolean not null default false,
    add column if not exists betaler_barnebidrag boolean not null default false;
