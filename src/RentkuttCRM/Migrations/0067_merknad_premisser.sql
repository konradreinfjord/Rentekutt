-- 0067 — Flere premisser (OG-betingelser) på merknadsregler.
-- Hovedbetingelsen ligger fortsatt i felt_nokkel/operator/verdi (bakoverkompatibelt).
-- Ekstra premisser lagres som JSON-array i tilleggspremisser: [{"f":"laanetype","o":"inneholder","v":"bolig"}].
-- En regel gir badge kun når HOVEDbetingelsen OG alle tilleggspremissene matcher.
alter table public.merknadsregel add column if not exists tilleggspremisser text;
