-- 0044 — eksplisitte RLS-policyer som ekstra forsvarslag (GDPR-funn, tilleggsmerknad).
--
-- Appen aksesserer databasen med service_role-nøkkelen (omgår RLS), og GDPR-jobbene via
-- den direkte Postgres-superbrukertilkoblingen. Rollene «anon» og «authenticated» (Supabase
-- sitt offentlige/JWT-lag) brukes IKKE av denne appen. Vi legger derfor eksplisitte
-- restriktive «nekt alt»-policyer for disse rollene, slik at et eventuelt lekket anon-nøkkel
-- ikke gir noen som helst tilgang — og at intensjonen er dokumentert og revisjonsbar i skjemaet.
--
-- Idempotent: kjører over alle tabeller i public (unntatt migreringstabellen), også fremtidige.
do $$
declare t text;
begin
    for t in
        select tablename from pg_tables
        where schemaname = 'public' and tablename <> 'schema_migrations'
    loop
        execute format('alter table public.%I enable row level security', t);

        if not exists (select 1 from pg_policies
                       where schemaname = 'public' and tablename = t and policyname = 'deny_anon') then
            execute format(
                'create policy deny_anon on public.%I as restrictive for all to anon using (false) with check (false)', t);
        end if;

        if not exists (select 1 from pg_policies
                       where schemaname = 'public' and tablename = t and policyname = 'deny_authenticated') then
            execute format(
                'create policy deny_authenticated on public.%I as restrictive for all to authenticated using (false) with check (false)', t);
        end if;
    end loop;
end $$;
