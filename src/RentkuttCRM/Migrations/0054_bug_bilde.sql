-- 0054 — Bildevedlegg til en bug-sak. Egen tabell (én rad = ett bilde per bug) slik at
-- bug-lista holder seg lett; bildet hentes kun på forespørsel. Bildet lagres som en
-- data:-URL (base64) i en text-kolonne — appen har ingen fil-/objektlagring, og bildene
-- er små skjermbilder til internt bruk. on delete cascade rydder vedlegget når bugen slettes.
create table if not exists public.bug_bilde (
    bug_id       uuid primary key references public.bug(id) on delete cascade,
    data         text        not null,   -- data:image/...;base64,....
    navn         text,                    -- opprinnelig filnavn (visning)
    type         text,                    -- MIME-type
    opprettet_at timestamptz not null default now()
);
