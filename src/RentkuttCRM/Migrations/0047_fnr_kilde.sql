-- 0047 — kilde/sikkerhetsnivå for fødselsnummeret per sak (BankID / Vipps / Skjema).
-- Dokumenterer hvordan identiteten ble fastslått: Vipps/BankID-autentisering (høy sikkerhet)
-- vs. selvrapportert i søknadsskjema (lav sikkerhet).
alter table public.kundekort
    add column if not exists fnr_kilde text;
