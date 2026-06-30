-- 0011 — flere saker per kunde + nye felt (nåværende bank, kommune)
-- Ny PK: id (uuid). kunde_id (fnr/orgnr/mobil) blir vanlig, ikke-unik kolonne,
-- slik at samme kunde kan ha flere kundekort/saker.

alter table public.kundekort add column if not exists navarende_bank text;
alter table public.kundekort add column if not exists kommune       text;

alter table public.kundekort add column if not exists id uuid not null default gen_random_uuid();

alter table public.kundekort drop constraint if exists kundekort_pkey;
alter table public.kundekort add constraint kundekort_pkey primary key (id);

create index if not exists kundekort_kunde_id_lookup on public.kundekort (kunde_id);
