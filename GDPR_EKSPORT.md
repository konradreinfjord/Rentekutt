# GDPR-eksport – Rentekutt-portalen

Generert ved kildekode-gjennomgang av `rentkutt`-repoet (ASP.NET Core Blazor Server, .NET 10, Supabase/Postgres).
Kun det som faktisk finnes i koden er rapportert. `IKKE IMPLEMENTERT` betyr at funksjonen ikke finnes i koden.
Alle funn har filsti + linjenummer for etterprøving. Ingen hemmelige verdier er tatt med — kun navn på miljøvariabler.

---

## 1. Datamodell

Personopplysninger ligger primært i tabellen `kundekort`. Oversikt over tabeller som inneholder persondata:

| Tabell | Kolonne(r) med persondata | Datatype | Persondata-kategori | Kryptert? | Metode | Nullable |
|---|---|---|---|---|---|---|
| `kundekort` | `foedselsnummer`, `medsoker_foedselsnummer` | text | Fødselsnummer (særlig kategori i praksis) | **Nei** | Klartekst | Ja |
| `kundekort` | `kunde_id` | text | Ofte = fødselsnummer/orgnr/mobil (identifikator) | **Nei** | Klartekst, **indeksert** | Nei (PK-lignende) |
| `kundekort` | `fullt_navn`, `mobilnummer`, `epost`, `adresse`, `postnummer`, `poststed`, `kommune`, `fylke` | text | Kontakt/identitet | Nei | Klartekst | Ja |
| `kundekort` | `medsoker_*` (navn, mobil, epost, adresse, inntekt …) | text/numeric | Medsøker | Nei | Klartekst | Ja |
| `kundekort` | `statsborgerskap`, `opprinnelsesland`, `sivilstatus`, `antall_barn_under_18`, `boforhold`, `arbeidssituasjon`, `arbeidsgiver`, `utdanning` | text/int | Sosioøkonomiske / husholdning | Nei | Klartekst | Ja |
| `kundekort` | `aarsinntekt_brutto`, `boliggjeld`, `studielaan`, `billaan`, `forbruksgjeld`, `samlet_gjeld`, `kontonummer`, `naavaerende_rente`, `boligverdi` … | numeric/text | Økonomi (finansiell profil) | Nei | Klartekst | Ja |
| `kundekort` | `kilde`, `status`, `eier`, `eier_navn`, `notater` | text | Metadata / fritekst-notater | Nei | Klartekst | Ja |
| `app_users` | `email`, `full_name`, `mobilnummer`, `password_hash` | text | Ansattbrukere | Passord: **ja** (hash). Øvrig: nei | `PasswordHasher` (PBKDF2) | Delvis |
| `saksnotat` | `tekst` (+ forfatter) | text | Fritekst-notater (kan inneholde PII) | Nei | Klartekst | Nei |
| `kundekort_logg` | `tekst`, `aktor` | text | Endringslogg (kan referere navn/verdier) | Nei | Klartekst | — |
| `banksending` | `kunde_navn` | text | Navn på sendt søknad | Nei | Klartekst | Ja |
| `alarm` | `detalj` | text | Kan inneholde kundenavn i feilmeldinger | Nei | Klartekst | Ja |
| `webhooks` | `token` | text | Ikke kunde-PII, men **hemmelighet i klartekst** | Nei | Klartekst | Nei |
| `hendelser` | – | – | Systemhendelser, **ingen PII** ([0009_hendelser.sql:1](src/RentkuttCRM/Migrations/0009_hendelser.sql#L1)) | – | – | – |
| `partnere`, `partner_produkt`, `rutingsregel`, `sms_maler`, `innstillinger` | – | – | Ingen kunde-PII | – | – | – |

**Hvor lagres fødselsnummer:** `kundekort.foedselsnummer` og `kundekort.medsoker_foedselsnummer`, begge `text` i **klartekst** ([src/RentkuttCRM/Services/Kundekort.cs:25](src/RentkuttCRM/Services/Kundekort.cs#L25), [:35](src/RentkuttCRM/Services/Kundekort.cs#L35); definert [0002_kundekort.sql](src/RentkuttCRM/Migrations/0002_kundekort.sql)). Ingen kryptering, hashing eller tokenisering.

**Fødselsnummer som nøkkel/indeks:**
- Opprinnelig var `kunde_id` (= fødselsnummer for B2C / orgnr for B2B) **primærnøkkel** ([0002_kundekort.sql:6](src/RentkuttCRM/Migrations/0002_kundekort.sql#L6)).
- PK ble senere endret til `id` (uuid), og `kunde_id` ble en **ikke-unik, indeksert kolonne** ([0011_kundekort_flere_saker.sql](src/RentkuttCRM/Migrations/0011_kundekort_flere_saker.sql) — `kundekort_kunde_id_lookup`). `kunde_id` er ofte fortsatt fødselsnummeret, så **fødselsnummer inngår i en indeks**.
- Fødselsnummer brukes ikke som fremmednøkkel eller unik constraint i nåværende skjema.

**Primærnøkkel for kunde/søknad:** `kundekort.id` (uuid) er unik per **søknad** ([0011](src/RentkuttCRM/Migrations/0011_kundekort_flere_saker.sql)). `kunde_id` grupperer flere søknader på samme **kunde**.

**Samtykke som egen entitet:** `IKKE IMPLEMENTERT`. Samtykke finnes kun som ett boolsk felt `samtykke_gjeldsregister_kredittsjekk` ([0019_kundekort_rentekutt_payload.sql:5](src/RentkuttCRM/Migrations/0019_kundekort_rentekutt_payload.sql#L5), [Kundekort.cs:87](src/RentkuttCRM/Services/Kundekort.cs#L87)). Ingen tekstversjon, tidsstempel, IP, formål eller utløp.

**Lead-kilde og behandlingsgrunnlag per lead:** Kilde lagres (`kundekort.kilde`, [0013_kundekort_kilde.sql](src/RentkuttCRM/Migrations/0013_kundekort_kilde.sql)) — «Prismatch» / «Rentekutt.no» / «Manuell». **Behandlingsgrunnlag per lead: `IKKE IMPLEMENTERT`** (ingen kolonne for rettslig grunnlag).

---

## 2. Autorisasjon og tilgang

**RLS per tabell:** Row Level Security er **aktivert på alle tabeller**, men det finnes **ingen policyer** (`create policy` gir null treff i migrasjonene). Uten policyer nektes `anon`/`authenticated` all tilgang; tilgang skjer utelukkende via `service_role`, som omgår RLS.

| Tabell | RLS | Policyer |
|---|---|---|
| kundekort, app_users, webhooks, hendelser, innstillinger, sms_maler, partnere, banksending, rutingsregel, saksnotat, schema_migrations, partner_produkt, alarm, kundekort_logg | **PÅ** | **Ingen** (kun service_role) |

Referanser: [0002:81](src/RentkuttCRM/Migrations/0002_kundekort.sql#L81), [0001:17](src/RentkuttCRM/Migrations/0001_app_users.sql#L17), [0022_rls_saksnotat_schema_migrations.sql](src/RentkuttCRM/Migrations/0022_rls_saksnotat_schema_migrations.sql), [0039:14](src/RentkuttCRM/Migrations/0039_kundekort_logg.sql#L14) m.fl.

**Nøkler/roller:**
- `Supabase__Url`, `Supabase__Key` — leses server-side ([Program.cs:10-14](src/RentkuttCRM/Program.cs#L10)). Nøkkelen brukes av alle datatjenester.
- `ConnectionStrings__Postgres` — direkte Postgres-tilkobling, kun til migrasjoner ([DatabaseMigrator.cs:25](src/RentkuttCRM/Services/DatabaseMigrator.cs#L25)).
- `Admin__SeedPassword`, `Admin__SeedEmail` ([SupabaseUserService.cs:61](src/RentkuttCRM/Services/SupabaseUserService.cs#L61)).
- `Instabank__Username`, `Instabank__PasswordTest`, `Instabank__PasswordProd`, `Instabank__AgentEmail` ([InstabankService.cs:47-50](src/RentkuttCRM/Services/InstabankService.cs#L47)).
- LinkMobility client-id/secret ([LinkMobilityService.cs](src/RentkuttCRM/Services/LinkMobilityService.cs)).

**service_role/nøkkel i frontend/bundle:** **Nei.** Appen er Blazor **Server** (`InteractiveServerRenderMode(prerender:false)`, [App.razor:17](src/RentkuttCRM/Components/App.razor#L17)) — all datakode og nøkler kjører på server; ingenting sendes til nettleseren. Supabase-klienten opprettes server-side ([Program.cs:12](src/RentkuttCRM/Program.cs#L12)). Ingen WASM-bundle. `appsettings.json` inneholder kun **tomme** plassholdere ([appsettings.json](src/RentkuttCRM/appsettings.json)).

**Intern autentisering:** E-post + passord, passord hashet med ASP.NET `PasswordHasher` (PBKDF2) ([SupabaseUserService.cs:25](src/RentkuttCRM/Services/SupabaseUserService.cs#L25), [:110](src/RentkuttCRM/Services/SupabaseUserService.cs#L110)). **2FA er valgfritt per bruker** (admin skrur på per konto), «fail-closed» når påslått uten leveringskanal ([Login.razor:94-102](src/RentkuttCRM/Components/Pages/Login.razor#L94), [TwoFactorService.cs](src/RentkuttCRM/Services/TwoFactorService.cs)). Ikke globalt påkrevd.

**Rollemodell:** Fire roller — `Saksbehandler`, `Compliance`, `Leder`, `Administrator` ([0001_app_users.sql](src/RentkuttCRM/Migrations/0001_app_users.sql), [SupabaseUserService.cs:19](src/RentkuttCRM/Services/SupabaseUserService.cs#L19)). Tilgangskontroll er **grov**: Admin- og Alarmer-sidene krever `Administrator` ([Admin.razor:946](src/RentkuttCRM/Components/Pages/Admin.razor#L946), [Alarmer.razor](src/RentkuttCRM/Components/Pages/Alarmer.razor)), men **alle innloggede brukere kan se og redigere alle kundekort** — ingen rad-/rollebasert begrensning på kundedata utover eierskap-visning. Roller `Compliance`/`Leder` gir ingen egne rettigheter i koden.

---

## 3. Tredjepartsintegrasjoner

| Tjeneste | Formål | Retning | Sendes | Mottas | Autentisering | Region |
|---|---|---|---|---|---|---|
| **Supabase (Postgres)** | Database, lagring, autentisering | Begge | All kundedata | All kundedata | `Supabase__Key` (service_role) / connection string | EU (oppgitt Sweden Central, [Admin.razor Supabase-fane](src/RentkuttCRM/Components/Pages/Admin.razor)) |
| **Instabank Agent API** | Sende lånesøknad til bank | Utgående | Fødselsnummer, e-post, mobil, beløp, sivilstatus, arbeids-/inntekts-/gjeldsfelt, orgnr (B2B) | ExternalReference, SigningUrl, status | Basic Auth (`Instabank__*`) | `netbank(pp).instabank.no` ([InstabankService.cs:28-29](src/RentkuttCRM/Services/InstabankService.cs#L28), payload [:198-252](src/RentkuttCRM/Services/InstabankService.cs#L198)) |
| **LinkMobility (SMS)** | 2FA-koder + SMS til kunde + 24-timers påminnelsesløp | Utgående | Mobilnummer + meldingstekst (avsender «Rentekutt») | Leveringsstatus | OAuth2 client credentials | `api.linkmobility.com`, `sso.linkmobility.com` ([LinkMobilityService.cs](src/RentkuttCRM/Services/LinkMobilityService.cs)) |
| **Klaviyo** | Markedsføringshendelser (klargjort) | Utgående | E-post + hendelsesegenskaper | Bekreftelse | Privat API-nøkkel (`Klaviyo__ApiKey`) | `a.klaviyo.com` ([KlaviyoService.cs](src/RentkuttCRM/Services/KlaviyoService.cs)) — **tredjeland (USA): krever overføringsgrunnlag + DPA** |
| **Gjeldsregisteret** | Samtykkebasert gjeldsoppslag via partner | Utgående | Fødselsnummer | Gjeldsopplysning | OAuth2 (partner) | ([GjeldsregisterService.cs](src/RentkuttCRM/Services/GjeldsregisterService.cs)) |
| **Norges Bank** | Henter styringsrente | Inngående | **Ingen PII** | Rentesatser (CSV) | Ingen | `data.norges-bank.no` ([StyringsrenteService.cs:87](src/RentkuttCRM/Services/StyringsrenteService.cs#L87)) |
| **Azure App Service** | Hosting | – | Kjøremiljø | – | Plattform | Sweden Central ([README.md](README.md)) |
| BankID | – | – | – | – | – | **IKKE IMPLEMENTERT** |
| Gjeldsregisteret | – | – | – | – | – | **IKKE IMPLEMENTERT** (kun boolsk samtykkefelt + Admin-fane) |
| PSD2/AISP (Kreditz) | – | – | – | – | – | **IKKE IMPLEMENTERT** (kun feltkatalog-plassholder, [CustomerFieldCatalog.cs:64](src/RentkuttCRM/Services/CustomerFieldCatalog.cs#L64)) |
| Eiendomsverdi | – | – | – | – | – | **IKKE IMPLEMENTERT** (boliglån gates, [InstabankService.cs:161](src/RentkuttCRM/Services/InstabankService.cs#L161)) |
| Kredittsjekk | – | – | – | – | – | **IKKE IMPLEMENTERT** |

**Sendes kundedata til noen LLM/AI-tjeneste?** **Nei.** Ingen referanse til OpenAI/Anthropic/Azure OpenAI/andre LLM-API i koden.

---

## 4. Frontend – sporing

- Skript lastet på alle sider ([App.razor:16-18](src/RentkuttCRM/Components/App.razor#L16)): `_framework/blazor.web.js` (rammeverk), `mi-chart.js`, `kundekort.js` — **alle førsteparts/lokale**.
- **Ingen** Meta Pixel, GA4, Hotjar, Clarity, LinkedIn Insight, tag manager eller session replay.
- **Ingen** skjemafeltverdier sendes som event-parametre (ingen analyseverktøy).
- **Cookie-samtykkeløsning: `IKKE IMPLEMENTERT`** — men det finnes heller ingen sporingsskript å blokkere. Appen bruker kun funksjonell tilstand (Blazor SignalR-tilkobling), ingen analyse-cookies.

---

## 5. Logging

- **Loggstack:** kun innebygd `Microsoft.Extensions.Logging` (`ILogger`) → stdout/konsoll, fanges av Azure App Service «Log stream». **Ingen** Sentry/Datadog/Application Insights/Serilog.
- **Region:** Azure App Service (Sweden Central); ikke persistert til ekstern tjeneste.
- **Maskering:** Loggmeldinger er generiske uten PII. Webhook logger kun **feltnavn** (ikke verdier): `"Mottatte felt: " + flat.Keys.Take(40)` ([WebhookController.cs:73](src/RentkuttCRM/Controllers/WebhookController.cs#L73)). Klient-IP logges kun ved **avvist** webhook ([:48](src/RentkuttCRM/Controllers/WebhookController.cs#L48), [:52](src/RentkuttCRM/Controllers/WebhookController.cs#L52)) og lagres ikke.
- **`console.log`/`logger.` mot request body/kunde/fnr:** Ingen treff som logger request body eller kundeobjekt. Alle `_log.*`-kall bruker generiske meldinger. Merk: [KundekortService.cs:167](src/RentkuttCRM/Services/KundekortService.cs#L167) returnerer `ex.Message` til UI ved lagringsfeil — lav PII-risiko, men bør vurderes.
- **Stack traces med request body til ekstern tjeneste:** Nei (ingen ekstern loggtjeneste).
- **Oppbevaringstid på logger:** `IKKE KONFIGURERT i kode` (styres av Azure-standard).

---

## 6. Revisjonsspor

- **Endringslogg finnes:** `kundekort_logg` ([0039_kundekort_logg.sql](src/RentkuttCRM/Migrations/0039_kundekort_logg.sql), [LoggService.cs](src/RentkuttCRM/Services/LoggService.cs)). Registrerer **bruker (`aktor`), tidspunkt (`opprettet`), handling/objekt (`tekst`)** ved: opprettelse, feltendringer (fra → til), statusendring, eierskap og sending ([KundekortService.cs SaveAsync-diff](src/RentkuttCRM/Services/KundekortService.cs)).
- **Mangler:** **Begrunnelse** logges ikke. **Innsyn/lesing logges ikke** — kun endringer. Det er altså ikke et fullstendig innsyns-revisjonsspor.
- **Kan endres/slettes av applikasjonen:** `LoggService` har ingen oppdater/slett-metode (append-only på app-nivå), **men** `kundekort_logg` har `ON DELETE CASCADE` mot `kundekort` ([0039:12](src/RentkuttCRM/Migrations/0039_kundekort_logg.sql#L12)) — loggen slettes når kundekortet slettes, og `service_role` kan endre/slette direkte i basen. Ikke manipuleringssikkert.
- `hendelser` er systemhendelser uten PII og per-kunde-kobling.

---

## 7. Sletting

- **Automatiske slettejobber: `IKKE IMPLEMENTERT`.** GDPR-fanen har innstillinger `gdpr_anonymize_months` og `gdpr_delete_months` ([Admin.razor:1090](src/RentkuttCRM/Components/Pages/Admin.razor#L1090), [:1096](src/RentkuttCRM/Components/Pages/Admin.razor#L1096)), men **ingen jobb/cron leser dem**. Eneste `BackgroundService` er `BankSendWorker` ([BankSendWorker.cs:9](src/RentkuttCRM/Services/BankSendWorker.cs#L9)) — den sletter/anonymiserer ikke. Lagringsbegrensning er altså konfigurerbar, men **ikke håndhevet**.
- **Manuell sletting:** `KundekortService.DeleteAsync` sletter ett kundekort ([KundekortService.cs:~415](src/RentkuttCRM/Services/KundekortService.cs)). Kaskade: `banksending`, `saksnotat`, `kundekort_logg` har alle `ON DELETE CASCADE` mot `kundekort(id)` ([0028](src/RentkuttCRM/Migrations/0028_banksending.sql), [0021:10](src/RentkuttCRM/Migrations/0021_oppfolging_notat_tredjepart.sql#L10), [0039:12](src/RentkuttCRM/Migrations/0039_kundekort_logg.sql#L12)). `alarm` har **ingen** FK mot kundekort → rader med kundenavn kan bli liggende.
- **Sletting fra backup:** Ikke håndtert i kode. Supabase-administrerte backuper beholder slettede data til backup utløper (backup-retensjon styres i Supabase, ikke i repo).
- **Eksporter alle data om én person (innsyn, art. 15): `IKKE IMPLEMENTERT`.**
- **Slett alle data om én person (art. 17):** Kun delvis — sletting av ett kundekort kaskaderer barn, men det finnes **ingen funksjon** som sletter alle søknader/spor for én person på tvers (fnr/`kunde_id`), og `alarm`-rader ryddes ikke.

---

## 8. Kryptering og nøkler

- **Kryptering i ro:** Disk-/plattformnivå levert av Supabase/Postgres (provider-administrert) — ikke konfigurert i kode. **Feltnivåkryptering: `IKKE IMPLEMENTERT`** — fødselsnummer og øvrige persondata lagres i klartekst.
- **Nøkler:** Miljøvariabler i Azure App Settings (`Supabase__Key`, `ConnectionStrings__Postgres`, `Instabank__*`, LinkMobility-secret, `Admin__SeedPassword`). Ikke i kodebasen (`appsettings.json` er tom). **KMS: nei. Nøkkelrotasjon: `IKKE IMPLEMENTERT`** (ingen rotasjonslogikk i kode).
- **Passord:** Hashet med ASP.NET `PasswordHasher` (PBKDF2) ([SupabaseUserService.cs:25](src/RentkuttCRM/Services/SupabaseUserService.cs#L25)).
- **TLS/HSTS:** `UseHsts()` + `UseHttpsRedirection()` i produksjon ([Program.cs:147-148](src/RentkuttCRM/Program.cs#L147)). TLS-terminering håndteres av Azure (TLS-versjon ikke satt i kode). Webhook- og tredjepart-API krever HTTPS ([WebhookController.cs:41](src/RentkuttCRM/Controllers/WebhookController.cs#L41), [TredjepartController.cs:41](src/RentkuttCRM/Controllers/TredjepartController.cs#L41)).

---

## 9. Samtykke- og oppslagssekvens

Faktisk søknadsflyt fra kildekoden:

1. **Lead inn** — via webhook (Prismatch/Rentekutt.no) [`POST /api/webhook/soknad`](src/RentkuttCRM/Controllers/WebhookController.cs#L37): token + IP-sjekk, payload mappes til `kundekort` inkl. `samtykke_gjeldsregister_kredittsjekk` (boolean). Alternativt manuelt via «Ny kunde».
2. **Lagring** — `KundekortService.SaveAsync` persisterer og logger «Registrert» ([KundekortService.cs](src/RentkuttCRM/Services/KundekortService.cs)).
3. **Saksbehandling** — agent åpner kundekort; `BeregningService` viser likviditet/belåningsgrad m.m. (kun visning).
4. **Sending til bank** — agent sender til Instabank; fødselsnummer, e-post, mobil, beløp m.m. overføres ([InstabankService.cs:198-252](src/RentkuttCRM/Services/InstabankService.cs#L198)).

**Samtykkesjekk før kall (OPPDATERT):** Samtykke håndheves nå i kode i **to lag** før overføring til bank: sendekøen (`BankSendWorker.BehandleAsync` → `SamtykkeService.HarGyldigEllerLegacyAsync`) og manuell sending (kundekortet). Uten gyldig samtykke (gjeldsregister + kredittsjekk) sendes ingenting; saken markeres i stedet. Samtykke er dessuten en egen entitet (`SamtykkeService`) med formål/tidsstempel/gyldighet, ikke bare en boolean.

**Tidsstemples samtykke til Gjeldsregisteret-oppslag før oppslaget?** Ikke relevant i praksis: **Gjeldsregisteret-oppslag er `IKKE IMPLEMENTERT`** (ingen tjeneste). Uansett har samtykket **ingen tidsstempel** (kun boolean, [0019:5](src/RentkuttCRM/Migrations/0019_kundekort_rentekutt_payload.sql#L5)), så et samtykke kan ikke dokumenteres tidfestet før et eventuelt oppslag.

---

## 10. Automatisert vurdering

- **Prekvalifiseringslogikk:** `BeregningService` ([BeregningService.cs:30](src/RentkuttCRM/Services/BeregningService.cs#L30)) beregner likviditet, belåningsgrad, gjeldsgrad og maks lån. Parametre er **konfigurerbare** (lagres i `innstillinger` via `SettingsService`, [:32-39](src/RentkuttCRM/Services/BeregningService.cs#L32)); regler (bl.a. progressiv skatt) ligger i kode.
- **Versjonert regelsett: `IKKE IMPLEMENTERT`** (ingen versjon på parametre eller avgjørelser).
- **Logging av hver avgjørelse (inndata/versjon/utfall): `IKKE IMPLEMENTERT`** — beregningen kjøres «on the fly» for visning, ikke persistert per avgjørelse.
- **Automatisk avslag uten menneske:** **Nei.** «Avslått» er en manuell status; beregningen vises kun (negativ likviditet markeres rødt, [Database.razor:163](src/RentkuttCRM/Components/Pages/Database.razor#L163)). Ingen kode setter status automatisk basert på beregning.
- **Kanal for manuell overprøving:** Ikke relevant (alle vurderinger er manuelle). Ingen egen «be om overprøving»-funksjon.

---

## 11. Miljøer og testdata

- **Miljøer:** Produksjon (Azure). Lokal utvikling kjører i **staging-fallback** (in-memory) når Supabase-nøkler mangler ([SupabaseUserService.cs:81](src/RentkuttCRM/Services/SupabaseUserService.cs#L81)). Ingen egen staging-instans i CI (CI deployer til én Azure-app, [.github/workflows/main_rentkutt-crm.yml](.github/workflows/main_rentkutt-crm.yml)).
- **Produksjonsdata i dev:** Lokal app bruker in-memory-data **med mindre** en utvikler setter ekte Supabase-nøkler lokalt — da treffer lokal kjøring prod. Ingen teknisk sperre mot dette.
- **Seed/fixtures med ekte fødselsnummer:** **Nei.** Seed-brukere er dummy (`dev@rentekutt.no` osv., [SupabaseUserService.cs:32-35](src/RentkuttCRM/Services/SupabaseUserService.cs#L32)). Ingen fnr-lignende 11-sifre i migrasjoner, `appsettings*.json` eller `Data/`.
- **Ekte kundedata i repo/migrasjoner/historikk:** **Ingen funnet** — ingen `INSERT` med PII, `appsettings.json` har tomme plassholdere, `bin/` er git-ignorert.

---

## 12. Hosting og drift

- **Leverandør/region:** Azure App Service, **Sweden Central** ([README.md](README.md)). Database: Supabase (Postgres), oppgitt **EU/Sweden Central** ([Admin.razor Supabase-fane](src/RentkuttCRM/Components/Pages/Admin.razor)). Backup-region: ikke i kode (Supabase-administrert).
- **Underleverandører:** Azure (hosting), Supabase (DB/auth), Instabank (banksending), LinkMobility (SMS), Klaviyo (markedsføringshendelser — **tredjeland USA**), Gjeldsregisteret (gjeldsoppslag via partner), Norges Bank (renter). CDN/DNS/WAF er ikke konfigurert i repoet. **Merk:** Klaviyo og Gjeldsregisteret er nyere databehandlere som må inn i behandlingsprotokollen (DPA; Klaviyo også SCC/overføringsgrunnlag).
- **Prod-tilgang i dag:** Kan ikke fastslås fra kode (Azure-/Supabase-konsolltilgang er organisasjons-/konsollkonfig). På app-nivå: brukere med rollen `Administrator`.
- **Avhengighetsskanning i CI: `IKKE IMPLEMENTERT`** — workflowen bygger og deployer kun ([main_rentkutt-crm.yml](.github/workflows/main_rentkutt-crm.yml)); ingen Dependabot/Snyk/Trivy.

---

## Toppfunn (fem alvorligste avvik, sortert etter risiko)

1. **Fødselsnummer i klartekst + i indeks.** `kundekort.foedselsnummer`/`medsoker_foedselsnummer` og ofte `kunde_id` lagres uten kryptering; `kunde_id` er indeksert ([Kundekort.cs:25](src/RentkuttCRM/Services/Kundekort.cs#L25), [0002_kundekort.sql](src/RentkuttCRM/Migrations/0002_kundekort.sql), [0011](src/RentkuttCRM/Migrations/0011_kundekort_flere_saker.sql)).
   *Utbedring:* innfør feltnivåkryptering eller tokenisering av fødselsnummer; slutt å bruke fnr som `kunde_id`/indeks (bruk uuid).

2. **Ingen sletting/anonymisering håndheves.** Kun innstillinger finnes; ingen jobb sletter eller anonymiserer ([Admin.razor:1090-1096](src/RentkuttCRM/Components/Pages/Admin.razor#L1090); ingen slette-`BackgroundService`).
   *Utbedring:* implementer en planlagt jobb som faktisk anonymiserer/sletter etter `gdpr_anonymize_months`/`gdpr_delete_months`, inkludert opprydding i `alarm`.

3. ~~**Samtykke er kun en boolean og sjekkes ikke før bankoverføring.**~~ **LØST:** samtykke er nå en egen entitet med formål/tidsstempel/gyldighet (`SamtykkeService` + migr. `0052`), og håndheves i to lag før bankoverføring (`BankSendWorker` + kundekortet). Fireøyne-godkjenning støttes.
   *Utbedring:* modeller samtykke som egen entitet (formål, tekstversjon, tidsstempel, IP) og legg en kode-sperre som krever gyldig samtykke før overføring til bank/oppslag.

4. **Ingen innsyn/eksport eller full sletting per person (registrertes rettigheter).** `IKKE IMPLEMENTERT` (art. 15/17); sletting av ett kundekort kaskaderer, men det finnes ingen tverrgående funksjon per person, og `alarm` beholder navn.
   *Utbedring:* bygg «eksporter alle data om person» og «slett alle data om person» (nøkkel på fnr/`kunde_id`), inkl. alle relaterte tabeller.

5. **Mangelfullt, ikke-manipuleringssikkert revisjonsspor.** `kundekort_logg` logger endringer, men ikke innsyn/lesing, mangler begrunnelse, og kaskade-slettes/kan endres av `service_role` ([0039](src/RentkuttCRM/Migrations/0039_kundekort_logg.sql)).
   *Utbedring:* logg også innsyn, legg til begrunnelse, og gjør revisjonsloggen append-only/immutabel (egen oppbevaring uten `ON DELETE CASCADE`).

*Tilleggsmerknad:* RLS er på men uten policyer — beskyttelsen hviler helt på hemmeligholdet av `service_role`-nøkkelen. Vurder eksplisitte policyer som ekstra forsvarslag, samt avhengighetsskanning i CI og obligatorisk 2FA for alle ansattkontoer.
