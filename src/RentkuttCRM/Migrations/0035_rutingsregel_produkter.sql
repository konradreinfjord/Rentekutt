-- 0035 — produktvalg per bank i rutingsreglene.
-- Lagres som JSON: banknavn → liste av produktkoder, f.eks. {"Instabank":[151,251]}.
-- Tom/utelatt = regelen gjelder alle produkter for banken (som før).
alter table public.rutingsregel add column if not exists produkter text;
