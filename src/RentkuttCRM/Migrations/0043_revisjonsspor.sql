-- 0043 — manipuleringssikkert revisjonsspor (GDPR-funn 5).
-- 1) Utvider kundekort_logg med kategori (endring/innsyn/sletting/…) og begrunnelse.
-- 2) Fjerner ON DELETE CASCADE — revisjonssporet har egen oppbevaring og skal IKKE
--    forsvinne når kundekortet slettes (accountability). GDPR-sletting rydder loggen
--    eksplisitt gjennom en kontrollert, autorisert vei (se punkt 3).
-- 3) Gjør loggen append-only: en trigger nekter UPDATE/DELETE med mindre den kjørende
--    transaksjonen eksplisitt setter app.allow_log_purge = 'on' (kun GDPR-jobben og
--    sletting per person gjør det). Vanlig drift kan dermed verken endre eller slette
--    historikk.

alter table public.kundekort_logg
    add column if not exists kategori    text not null default 'endring',
    add column if not exists begrunnelse text;

-- Fjern fremmednøkkelen med kaskadesletting (loggen skal overleve sletting av kortet).
-- Finn constraint-navnet dynamisk så vi ikke er avhengige av auto-navnet.
do $$
declare c text;
begin
    for c in
        select con.conname
        from pg_constraint con
        join pg_class rel on rel.oid = con.conrelid
        join pg_namespace ns on ns.oid = rel.relnamespace
        where con.contype = 'f' and ns.nspname = 'public' and rel.relname = 'kundekort_logg'
    loop
        execute format('alter table public.kundekort_logg drop constraint %I', c);
    end loop;
end $$;

-- Append-only-vokter: blokker endring/sletting utenom autorisert opprydding.
create or replace function public.kundekort_logg_immutabel() returns trigger as $$
begin
    if current_setting('app.allow_log_purge', true) = 'on' then
        return case when tg_op = 'DELETE' then old else new end;
    end if;
    raise exception 'Revisjonsspor er skrivebeskyttet (append-only): % er ikke tillatt', tg_op;
end;
$$ language plpgsql;

drop trigger if exists trg_kundekort_logg_immutabel on public.kundekort_logg;
create trigger trg_kundekort_logg_immutabel
    before update or delete on public.kundekort_logg
    for each row execute function public.kundekort_logg_immutabel();
