-- Sørg for at updated_at oppdateres ved hver endring (status, delegering, felter).
-- Brukes til Tidsbruk-widgeten (liggetid siden siste statusendring).
create or replace function public.set_updated_at()
returns trigger as $$
begin
    new.updated_at = now();
    return new;
end;
$$ language plpgsql;

drop trigger if exists kundekort_set_updated_at on public.kundekort;
create trigger kundekort_set_updated_at
    before update on public.kundekort
    for each row execute function public.set_updated_at();
