-- 0079 — Innkommende SMS (toveis). Mottar svar fra kunder via LinkMobility MO-webhook og kobler
-- dem til riktig kundekort på mobilnummer. Gir toveis SMS-dialog når avsender er et numerisk nummer.
create table if not exists public.innkommende_sms (
    id            uuid primary key default gen_random_uuid(),
    kundekort_id  uuid references public.kundekort(id) on delete set null,
    fra_nummer    text not null,
    til_nummer    text,
    tekst         text not null default '',
    raw           text,
    mottatt_at    timestamptz not null default now()
);

create index if not exists innkommende_sms_kundekort_idx on public.innkommende_sms (kundekort_id);
create index if not exists innkommende_sms_mottatt_idx on public.innkommende_sms (mottatt_at desc);
create index if not exists innkommende_sms_fra_idx on public.innkommende_sms (fra_nummer);
