-- 0068 — Standardverdi for «send uferdige etter 30 min».
-- Soknedal Sparebank er samle-banken for påbegynte søknader som ikke fullføres innen 30 min,
-- så den slås PÅ som standard. Alle andre banker (inkl. Instabank) står AV via kolonnens
-- default (false, 0066). Kjøres én gang ved deploy — senere av/på styres i Admin → API og Data.
update public.partnere set auto_paabegynt = true where navn = 'Soknedal Sparebank';
