# GDPR — samlet dokumentasjon (Rentekutt-plattformen)

**System:** ASP.NET Core Blazor Server / Supabase Postgres (EU)
**Dato:** 27.07.2026
**Status:** Implementert og verifisert i produksjon (unntak: bevisst utsatt nøkkelrotasjon)

Dokumentet er delt i fem deler:
- **Del I** — Prosedyre-oppsummering (til hovedprosedyren)
- **Del II** — Teknisk implementering per område
- **Del III** — Funksjonsregister (for audit, funksjon-for-funksjon)
- **Del IV** — Verifisering mot produksjon + utbedringer
- **Del V** — App-innstillinger, migrasjoner og restanser

---

# DEL I — Prosedyre-oppsummering

## 1. Tekniske tiltak som er på plass

- **Kryptering av fødselsnummer i ro** (art. 32, 25): fødselsnummer, medsøkers fødselsnummer og
  kunde-id krypteres med AES-256-GCM. Søk skjer via en indeksert HMAC, slik at innsyn/sletting
  fungerer uten å lagre fødselsnummer i klartekst. All eksisterende data er kryptert (bakfylt).
  Nøkkel forvaltes som hemmelig app-innstilling i Azure, aldri i kildekode. Dersom
  krypteringsnøkkelen mangler, lagres fødselsnummer i klartekst fremfor at innkommende saker
  avvises. Valget er truffet bevisst for å unngå tap av data, og kompenseres med kritisk alarm
  ved oppstart og ved hver klartekstskriving.

- **Lagringsbegrensning** (art. 5(1)(e), 17): planlagt daglig jobb som anonymiserer og sletter
  saker etter konfigurerbar oppbevaringstid. Oppbevaringstiden settes av administrator i portalen.

- **Samtykke** (art. 6, 7): samtykke lagres som egen, tidfestet enhet (formål, tekstversjon,
  kilde, IP, tidspunkt, utløp). Overføring av søknad til bank er sperret i kode uten gyldig,
  ikke-utløpt samtykke.

- **Registrertes rettigheter** (art. 15, 17, 20): innsyn gir maskinlesbar eksport av alle
  personopplysninger på tvers av samtlige tabeller; sletting fjerner alle data om personen.

- **Manipuleringssikkert revisjonsspor** (art. 5(2), 30): endrings- og innsynshistorikk er
  append-only. Innsyn logges; fødselsnummer maskeres i sporet.

- **Tilgangskontroll og sikkerhet** (art. 32): Row Level Security på alle tabeller med eksplisitte
  nekt-policyer for offentlige roller (verifisert at anonym tilgang ikke kan lese data).
  Applikasjonen selv aksesserer databasen via en privilegert serverrolle. Tofaktor-autentisering
  (SMS) kreves for alle ansatte; TLS/HSTS, streng CSP og sikkerhetsheadere; rate limiting på
  offentlige endepunkter; automatisk sårbarhetsskanning av avhengigheter i CI.

- **Logg-redaksjon**: all applikasjonslogg kjøres gjennom et sikkerhetsnett som maskerer
  fødselsnummer (kun treff som består modulus-11 + gyldig datoprefiks maskeres, slik at f.eks.
  kontonummer ikke overmaskeres).

- **Vipps-bekreftelse / påbegynt søknad** (art. 6(1)(b)): når en kunde autentiserer seg med Vipps,
  opprettes et utkast med navn, mobilnummer, e-post og adresse fra Vipps (status «Påbegynt søknad»).
  Utkastet kobles til den fullstendige søknaden når skjemaet kommer inn (match på mobilnummer →
  e-post → navn) og løftes til «Åpen». Behandlingsgrunnlag: tiltak på den registrertes anmodning før
  avtaleinngåelse. Utkast omfattes av samme oppbevaringsbegrensning som øvrige saker.

- **Feilsøkingslagring av innkommende payloads** (art. 5(1)(c) dataminimering): de siste 50
  innkommende webhook-forespørslene lagres for feilsøking. Fødselsnummer maskeres før lagring; øvrig
  innhold (navn/kontakt) beholdes. Bufferet er begrenset til 50 rader (eldre trimmes automatisk) og
  slettes uansett etter 10 dager (daglig jobb), er kun tilgjengelig for administrator server-side
  (RLS), og fungerer som et kortlevd driftsbuffer — ikke et register.

## 2. Dokumenterte designvalg

- **Anonymisering vs. pseudonymisering (bevisst valg):** Ved utløpt oppbevaringstid fjernes alle
  direkte identifikatorer (navn, fødselsnummer, kontaktinfo, kontonummer, søkbar HMAC), mens
  enkelte ikke-identifiserende, men presise felt (lånebeløp, opprettelsestidspunkt, kommune)
  beholdes bevisst. Dette gjøres av hensyn til oppfølging, internkontroll og etterprøvbarhet av
  tidligere behandling. Teknisk innebærer det at eldre saker pseudonymiseres (direkte
  identifikatorer fjernes) snarere enn full-anonymiseres. Behandlingsgrunnlaget for fortsatt
  lagring av disse feltene er berettiget interesse (intern kontroll/dokumentasjon), avveid mot den
  registrertes rettigheter. Pseudonymiserte saker oppbevares i 5 år, i tråd med øvrig
  dokumentasjonsplikt, og slettes deretter. Den registrerte kan protestere mot behandlingen etter
  art. 21.

- **Samtykkeformål:** Samtykket gjelder Rentekutts egne oppslag mot eksterne kilder. Bankpartner
  er selvstendig behandlingsansvarlig og innhenter eget grunnlag for sin kredittvurdering.
  Overføring av søknad til bank skjer på grunnlag av art. 6(1)(b), tiltak på den registrertes
  anmodning før avtaleinngåelse. Sperren i kode er en ytterligere kontroll som sikrer at samtykke
  til kredittvurdering foreligger før oversendelse. Formålsnavnet «Gjeldsregister og kredittsjekk»
  beholdes uendret; gjeldsregister-oppslag aktiveres i fase 2. Gjeldsregister-oppslag og
  kredittsjekk deler samme formål (kredittvurdering) og dekkes derfor av ett felles samtykke.

- **Oppbevaringstid:** Konfigureres av administrator i portalen (Admin → GDPR); ikke hardkodet.

---

# DEL II — Teknisk implementering per område

## 1. Feltnivåkryptering av fødselsnummer (art. 32, 25, 5(1)(f))
- **Algoritme:** AES-256-GCM med 96-bit tilfeldig nonce per verdi og 128-bit autentiseringstag.
- **Lagringsformat:** `enc:1:base64(nonce | tag | ciphertext)`.
- **Krypterte felt:** `foedselsnummer`, `medsoker_foedselsnummer`, `kunde_id`.
- **Søkbarhet uten klartekst:** deterministisk HMAC-SHA256 av fødselsnummeret i indeksert kolonne
  `fnr_hmac` (egen avledet nøkkel). Likhets-oppslag skjer mot HMAC, ikke fødselsnummer.
- **Transparent:** krypter-ved-skriving på klon; dekrypter-ved-lesing på alle leseveier.
- **Nøkkel:** app-innstilling `Gdpr__FieldKey` i Azure; aldri i kildekode.
- **Fail-open:** uten nøkkel lagres klartekst (for å ikke miste data) + kritisk alarm.
- **Bakfylling:** engangsjobb i Admin krypterer eksisterende rader (idempotent).
- **Kode:** `Services/CryptoService.cs`, `Services/KundekortService.cs`, migr. `0042`.

## 2. Lagringsbegrensning: anonymisering og sletting (art. 5(1)(e), 17)
- Daglig `BackgroundService` som leser `gdpr_anonymize_months` / `gdpr_delete_months`.
- Anonymisering: nuller identitet/kontakt/medsøker/notater + `fnr_hmac`, setter `anonymisert_at`,
  rydder relaterte tabeller (saksnotat, endringslogg, kundenavn i banksending, alarm).
- Sletting: fjerner kundekort (kaskade) etter slette-grensen.
- Tørrkjør (forhåndsvisning) + manuell kjøring med bekreftelse.
- **Kode:** `Services/GdprService.cs` (`KjorAsync`), `Services/GdprWorker.cs`, migr. `0040`.

## 3. Samtykke som dokumenterbar entitet + sperre (art. 6, 7, 5(1)(a))
- Tabellen `samtykke`: formål, gitt, tekstversjon, kilde, IP, tidsstempel, utløp.
- Sperre i to lag (sendekø + manuell sending) — nekter bank-overføring uten gyldig samtykke;
  utløp håndheves (entiteten er fasit; legacy-boolean kun fallback for eldre saker).
- **Kode:** `Services/SamtykkeService.cs`, `Services/BankSendWorker.cs`, `NyKunde.razor`,
  `WebhookController.cs`, migr. `0041`.

## 4. Registrertes rettigheter (art. 15, 17, 20)
- Søk på fnr (HMAC + klartekst-fallback), mobil, e-post, kunde-id.
- Innsyn: JSON av kundekort + saksnotat + endringslogg + banksending + samtykke + alarm; fnr
  dekryptert. Innsynet logges.
- Sletting: transaksjon som fjerner alle personens rader (inkl. alarm); loggføres uten PII.
- **Kode:** `Services/GdprService.cs` (`SokPersonAsync`/`EksporterPersonAsync`/`SlettPersonAsync`),
  `Admin.razor`.

## 5. Manipuleringssikkert revisjonsspor (art. 5(2), 30, 32)
- Append-only databasetrigger nekter UPDATE/DELETE unntatt autorisert opprydding
  (`app.allow_log_purge`, kun GDPR-jobbene).
- Kaskadesletting fjernet — sporet overlever sletting av kortet.
- Innhold: aktør, tidspunkt, kategori, begrunnelse. Innsyn logges. Fnr maskeres i diffen.
- **Kode:** migr. `0043`, `Services/LoggService.cs`, `KundekortService.cs`, `GdprService.cs`,
  `NyKunde.razor`.

## 6. Behandlingsgrunnlag per lead (art. 6, 30)
- Eksplisitt rettslig grunnlag per søknad (Samtykke/Avtale/Rettslig forpliktelse/Berettiget
  interesse). Innkommende leads = «Samtykke»; endres i kundekortet, endringer havner i sporet.
- **Kode:** migr. `0045`, `Kundekort.cs`, `KundekortService.cs`, `NyKunde.razor`.

## 7. Versjonert regelsett + avgjørelseslogg (art. 22, 30)
- Logikk-matrisen har innholdsbasert versjon; ved bank-sending logges matriseversjon + forslag +
  faktisk bankvalg. Endelig valg gjøres av rådgiver (menneske i løkken).
- **Kode:** `RutingsregelService.cs` (`RutingEval.RegelsettVersjon`), `NyKunde.razor`.

## 8. Sikkerhetskopier (art. 17)
- Plattformadministrert av Supabase. Sletting/anonymisering slår gjennom i backup når disse
  rulleres ut etter Supabases oppbevaringstid. Ved gjenoppretting fra backup må
  sletting/anonymisering kjøres på nytt for den gjenopprettede perioden — dette er en manuell
  handling som må utføres av administrator.

## 9. Tverrgående sikkerhet (art. 32)
- **Tilgang:** RLS på alle tabeller + eksplisitte «nekt alt»-policyer for `anon`/`authenticated`
  (migr. `0044`). Applikasjonen aksesserer via privilegert serverrolle.
- **Autentisering:** PBKDF2-passord; brute-force-sperre; 2FA (SMS) kreves for alle ansatte, med
  selvbetjent oppsett; fail-closed uten SMS-kanal.
- **Transport:** TLS/HSTS, streng CSP, X-Frame-Options: DENY, X-Content-Type-Options, Referrer-Policy.
- **Misbruk:** rate limiting på offentlige endepunkter/webhooks.
- **Hemmeligheter:** Azure App Settings, aldri i kode.
- **CI:** Dependabot + sårbarhetsskanning av NuGet-pakker.
- **Logg-redaksjon:** fnr maskeres i all console-logg (MOD11 + datoprefiks).
- **Kjøringslogg for GDPR-jobbene** (migr. `0062`, tabell `gdpr_jobb_kjoring`): én rad per FORSØK
  (anonymisering/sletting/reparasjon) — opprettes ved start, oppdateres ved fullføring. `fullfort_at
  = null` = «startet, men kom ikke i mål». Skrives av `GdprService.KjorAsync` / `GdprKjoringService`.
  Dette er også tallet man vil ha i hånden ved tilsyn.
- **Alarm 1 — oppbevaringsjobben har ikke fullført** (`GdprOvervaakWorker`, hver time): evaluerer
  `max(fullfort_at)` for `sletting`. Er den eldre enn **48 t** ELLER `null` (aldri fullført), reises
  kritisk, tilstands-basert alarm med KONSEKVENSEN i teksten: «Sletterutinen har ikke fullført siden
  {tidspunkt} ({N} døgn). {M} kundekort ligger over oppbevaringstiden.» Måler fravær av suksess, ikke
  tilstedeværelse av feil, og evalueres via Supabase — uavhengig av jobbens egen Postgres-tilkobling.
- **Alarm 2 — fødselsnummer i klartekst** (`KundekortService.AntallKlartekstFnrAsync`, hver time):
  teller rader der fnr ikke er `enc:1:`-kryptert; kritisk alarm ved > 0. Forventet i normaldrift: 0.
- **Alarm 3 — rader over oppbevaringstid ikke anonymisert** (`GdprOvervaakWorker` +
  `KundekortService.TellIkkeAnonymisertOverTidAsync`, hver time): teller kundekort over
  anonymiseringsgrensen som fortsatt har `anonymisert_at = null`; kritisk alarm hvis > 0 i **mer enn
  48 t** (karens så jobben får ta unna i normalt løp). Alarm 1 måler at jobben KJØRER; Alarm 3 måler at
  den VIRKER — fanger feil parameter/terskel/filter der jobben fullfører uten å behandle radene den skulle.
- **Reparasjonsjobb (F8.1)** (`GdprOvervaakWorker` daglig, kun når `Gdpr__FieldKey` er lastet):
  krypterer klartekst-fnr og regenererer HMAC (`KrypterEksisterendeAsync`), så et kort feilkonfig-
  vindu ikke etterlater et permanent restlager i klartekst. Loggføres som `reparasjon`-kjøring.
- **HMAC-fallback i søk (verifisert):** innsyn/sletting per person matcher både `fnr_hmac` og
  `foedselsnummer` (klartekst) — så klartekst-skrevne rader er søkbare på fnr selv før reparasjon
  (`GdprService.SokFilter`).
- **Kun reelle problemer teller:** info-alarmer (siste-vellykkede-hjerteslag) er logglinjer og telles
  ikke i varsel-lampen/antallet åpne alarmer (`AlarmService.AntallAapneAsync`).
- Alle overvåkings-/reparasjonsmekanismer kjøres **kun i produksjon** (dev deler prod-DB).

> **KRAV ved fremtidig telemetri:** ingen Application Insights/OpenTelemetry i dag. Innføres
> telemetri senere, MÅ redaksjon av personopplysninger være på plass i samme endring.

---

# DEL III — Funksjonsregister (for audit)

Stier relative til `src/RentkuttCRM/`.

### A.1 Kryptering — `Services/CryptoService.cs`
| Medlem | Gjør | Verifiser |
|---|---|---|
| `IsEnabled` | True når `Gdpr__FieldKey` er lastet i kjørende prosess | Admin → GDPR-diagnose |
| `Beskytt` | AES-256-GCM krypter (idempotent; klartekst uten nøkkel) | Selvtest; rå SQL `enc:1:…` |
| `Avdekk` | Dekrypter; slipper legacy klartekst gjennom | Selvtest (rundtur) |
| `HmacFnr` | Deterministisk HMAC-SHA256 av fnr | Samme fnr → samme hmac |

### A.2 Kundekort — `Services/KundekortService.cs`
| Medlem | Gjør | Verifiser |
|---|---|---|
| `SaveAsync` | Krypterer fnr ved skriving; fail-open + kritisk alarm uten nøkkel | Lagre uten nøkkel → alarm |
| `ForDb` | Kryptert klon + setter `fnr_hmac` | Rå SQL: `enc:1:…` + hmac satt |
| `Avdekk`/`AvdekkAlle` | Dekrypterer på alle leseveier | UI viser klartekst fnr |
| `KrypterEksisterendeAsync` | Engangs bakfylling (idempotent) | Admin-knapp; tellinger = 0 |
| `Endringer` | Diff til revisjonsspor; maskerer fnr (siste 4) | Endre fnr → `****…####` |

### A.3 GDPR — `Services/GdprService.cs`
| Medlem | Gjør | Verifiser |
|---|---|---|
| `KjorAsync` | Anonymiser (+`fnr_hmac`) + slett; rydder relaterte tabeller inkl. `alarm`; tørrkjør | Tørrkjør vs `count(*)` |
| `SokPersonAsync` | Personsøk på `fnr_hmac` (+ fallback), mobil, e-post, kunde-id | Admin → Innsyn |
| `EksporterPersonAsync` | Innsyn-JSON (alle tabeller inkl. alarm), fnr dekryptert; logger innsyn | Last ned; sjekk `kategori='innsyn'` |
| `SlettPersonAsync` | Full sletting per person; loggfører uten PII | Ingen PII-rader igjen |
| `KrypteringsDiagnoseAsync` | Live-tellinger (uten `enc:1:` / mangler hmac) | Admin databasetelling |

### A.4 Samtykke — `Services/SamtykkeService.cs`
| Medlem | Gjør | Verifiser |
|---|---|---|
| `RegistrerAsync` | Lagrer samtykke m/ tekstversjon `samtykke-v1`, kilde, IP, tid, utløp | Rad i `samtykke` |
| `HarGyldigEllerLegacyAsync` | Entitet er fasit (håndhever utløp); legacy-flagg kun fallback | Utløpt + flagg=true → blokkert |

### A.5 Revisjonsspor — `Services/LoggService.cs` + migr. `0043`
| Medlem | Gjør | Verifiser |
|---|---|---|
| `LoggAsync`/`LoggInnsynAsync`/`LoggFlereAsync` | Append-only logglinjer (kategori/begrunnelse) | Åpne kort → `innsyn`-rad |
| trigger `kundekort_logg_immutabel` | Nekter UPDATE/DELETE utenom autorisert opprydding | SQL → exception |

### A.6 Bakgrunnsjobber og sperrer
| Medlem | Fil | Gjør |
|---|---|---|
| `GdprWorker` | `Services/GdprWorker.cs` | Daglig `KjorAsync` |
| Samtykke-sperre (kø) | `BankSendWorker.cs` | Nekter banksending uten samtykke → «Feilet i sending» |
| Oppstartsalarmer | `BankSendWorker.cs` | Kritiske alarmer: migrasjonsfeil + «kryptering AV» |
| Samtykke-sperre (manuell) | `NyKunde.razor` `SendTilBank` | Blokkering + avgjørelseslogg |

### A.7 Logg-redaksjon
| Medlem | Fil | Gjør |
|---|---|---|
| `FnrRedactor.Redact` | `Services/FnrRedactor.cs` | Maskerer fnr (MOD11 + datoprefiks inkl. D-nummer) |
| `RedactingConsoleFormatter` | `Services/RedactingConsoleFormatter.cs` | Kjører all console-logg gjennom redaktøren |

### A.8 Regelsett + avgjørelse — `Services/RutingsregelService.cs`
| Medlem | Gjør | Verifiser |
|---|---|---|
| `RutingEval.RegelsettVersjon` | Innholdsbasert versjon (hash) av matrisen | Admin viser `v…` |
| Avgjørelseslogg | `NyKunde.razor` | Matriseversjon + forslag + valgt bank (`avgjørelse`) |

### A.9 Vipps / påbegynt søknad — `Controllers/WebhookController.cs`
| Medlem | Gjør | Verifiser |
|---|---|---|
| `POST /api/webhook/vipps` | Oppretter utkast «Påbegynt søknad» (egen Vipps-token) | Admin → Kanaler |
| `MapVipps` | Mapper CellPhone/FullName/Email/CustomerType/Address/ZipCode | — |
| `FinnPaabegyntAsync` | Match mobil → e-post → navn ved søknad; løfter til «Åpen» | — |
| `WebhookPayloadService` | Lagrer siste 50 payloads (fnr maskert), trimmes | Admin → Kanaler |

### A.10 Tilgang / 2FA / diagnose
| Medlem | Fil | Gjør |
|---|---|---|
| Obligatorisk 2FA + selvbetjent oppsett | `Login.razor` | Krever SMS-kode; oppsett for brukere uten mobil |
| `TwoFactorService.KeyRequireAll` / Admin-bryter | `TwoFactorService.cs`, `Admin.razor` | Global av/på for 2FA |
| `KrypteringStatus()` / `KjorKryptDiagnose()` | `Admin.razor` | Diagnose fra kjørende prosess + live DB |
| RLS-policyer | migr. `0044` | «Nekt alt» for `anon`/`authenticated` |

---

# DEL IV — Verifisering mot produksjon + utbedringer

## Verifisert direkte mot produksjonsdatabasen (revisjon Del 0–10)
- **Deploy:** migrasjoner 0040–0046 kjørt i prod (`schema_migrations`); siste kode-commit kjører.
- **Kryptering:** 0 fnr i klartekst, 0 kunde_id i klartekst, HMAC satt på alle rader; live-selvtest
  (krypter→dekrypter i prosessen) OK → `Gdpr__FieldKey` lastet.
- **HMAC:** 64-tegns SHA-256, deterministisk (samme identifikator → samme hmac).
- **Revisjonsspor:** UPDATE og DELETE mot `kundekort_logg` blokkeres av triggeren (testet, rullet
  tilbake). Innsyn logges (bekreftet voksende antall). Ingen umaskert fnr i loggen.
- **Sletting per person:** replika i rollback-transaksjon tømte alle tabeller for personen.
- **Anonymisering:** felt-for-felt bekreftet; `fnr_hmac` nulles → ikke gjenfinnbar.
- **RLS:** `anon`-rolle kan ikke lese `kundekort` (0 rader; testet med `set role anon`).
- **Logg-redaksjon:** gyldig fnr + D-nummer maskeres; kontonummer (gyldig dato, feil MOD11) maskeres ikke.

## Utbedringer gjort under revisjonen
- **A** Utløpt samtykke håndheves (entitet er fasit; legacy-flagg kun fallback).
- **B** Anonymisering nuller også `fnr_hmac`.
- **C** Innsynseksport inkluderer `alarm`.
- **D** Sletting/anonymisering fjerner tilhørende `alarm`-rader.
- **E** Manglende nøkkel → fail-open + kritisk alarm (oppstart + hver klartekstskriving).
- **F** Reell samtykke-tekstversjon (`samtykke-v1`).
- **2.5** Retensjonsjobben aldersletter kun kvitterte alarmer (bevarer ukvitterte hendelsesspor).
- **G** *(akseptert)* HMAC avledes fra samme basenøkkel via domenetagg `hmac-v1`; separat
  HMAC-nøkkel ville ugyldiggjøre eksisterende HMAC-er.

## Åpne funn (ikke kritiske)
- Retensjonstid settes i Admin (kjører default 12/24 mnd til den settes).
- «Anonymisering» er reelt pseudonymisering (bevisst — se Del I punkt 2).
- MOD11-validering av fødselsnummer skjer nå ved webhook-mottak (generisk avvisning, ingen
  eksistens-lekkasje) i tillegg til ved bank-sending. *(utbedret)*
- Ingen reserve-2FA (SMS-utfall = utestenging).
- `.gitignore` dekker ikke `appsettings*.json` (i dag tomme).
- Append-only kan omgås av direkte privilegert DB-tilgang (iboende ved GUC-vern).

---

# DEL V — App-innstillinger, migrasjoner og restanser

## Nødvendige app-innstillinger (Azure)
| Innstilling | Formål |
|---|---|
| `Gdpr__FieldKey` | Krypteringsnøkkel for fødselsnummer (32-byte base64/passfrase). Sikkerhetskopieres — se under. |
| `ConnectionStrings__Postgres` | Direkte Postgres for migrasjoner + GDPR-jobber |
| `Supabase__Url` / `Supabase__Key` | Applikasjonens datatilgang (service_role) |

**Sikkerhetskopi av `Gdpr__FieldKey`:** nøkkelen oppbevares i [passordhvelv **utenfor Azure-tenanten**
— f.eks. 1Password/Bitwarden/HSM]. Kopien skal ligge utenfor samme Azure-tenant som databasen,
ellers forsvinner separasjonen mellom nøkkel og data (den som får tilgang til tenanten ville da
hatt både kryptert data og nøkkel). Mistes nøkkelen uten kopi, blir krypterte fødselsnummer
permanent uleselige.

## Migrasjoner
`0040_kundekort_anonymisert_at` · `0041_samtykke` · `0042_fnr_kryptering` (+ `fnr_hmac`-indeks) ·
`0043_revisjonsspor` (append-only trigger) · `0044_rls_policyer` · `0045_behandlingsgrunnlag` ·
`0046_webhook_payload`.

## Restanser / planlagt
- **Nøkkelrotasjon:** versjonert nøkkelring + re-krypteringsjobb er ikke implementert. Inntil videre
  skal `Gdpr__FieldKey` ikke byttes uten re-krypteringsplan (data blir ellers uleselig).
- **Payload-buffer og per-person-rettigheter:** feilsøkingsbufferet (siste 50 payloads) inngår ikke
  i innsyn/sletting per person, men er selvbegrensende (maks 50, fnr maskert, slettes etter 10 dager).
- **`.gitignore`** bør dekke `appsettings*.json`.
