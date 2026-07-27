-- 0048 — Vipps-autentisering skjer på rentekutt.no. Rett kilde fra «Vipps» til «Rentekutt.no»
-- på eksisterende saker (nye Vipps-saker settes riktig i koden). Autentiseringsmetoden er
-- dokumentert separat i fnr_kilde + revisjonssporet.
update public.kundekort set kilde = 'Rentekutt.no' where kilde = 'Vipps';
