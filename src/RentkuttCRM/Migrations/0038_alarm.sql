-- 0038 — alarmliste. Når noe stopper eller feiler (migrasjoner, banksending,
-- sikkerhetsbryter mot Instabank) reises en alarm her. Vises i portalen og må
-- kvitteres ut. Åpne alarmer dedupliseres på «noekkel» (teller opp antall i stedet
-- for å spamme like alarmer).
create table if not exists public.alarm (
    id          uuid primary key default gen_random_uuid(),
    type        text not null,
    alvorlighet text not null default 'advarsel',   -- kritisk | advarsel | info
    tittel      text not null,
    detalj      text,
    kilde       text,
    noekkel     text,
    antall      int not null default 1,
    kvittert    boolean not null default false,
    kvittert_av text,
    kvittert_at timestamptz,
    sist_sett   timestamptz not null default now(),
    created_at  timestamptz not null default now()
);

create index if not exists idx_alarm_kvittert on public.alarm(kvittert);
create index if not exists idx_alarm_noekkel on public.alarm(noekkel);

alter table public.alarm enable row level security;
