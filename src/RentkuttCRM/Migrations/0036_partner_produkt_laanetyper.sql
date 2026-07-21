-- 0036 — lånetyper per produkt (driver auto-valg av produkt ut fra kundens lånetype).
-- Kommaseparert; tom = ikke knyttet til en bestemt lånetype.
alter table public.partner_produkt add column if not exists laanetyper text;

-- Standard for Instabank Forbrukslån (kode 151): gjelder forbrukslån + refinansiering.
update public.partner_produkt
set laanetyper = 'Forbrukslån,Refinansiering'
where kode = 151 and (laanetyper is null or laanetyper = '');
