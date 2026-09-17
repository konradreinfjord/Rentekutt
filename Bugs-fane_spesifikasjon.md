# Bugs-fanen i Kundeservice — funksjonell spesifikasjon

Beskrivelse av hvordan fanen fungerer i PrismatchBase, skrevet for å bygge tilsvarende i et annet system. Teknologi her er Blazor Server, Dapper og Postgres, men ingenting i designet krever det.

---

## 1. Hva fanen er til for

Et internt meldingsspor mellom **kundeservice** (som ser feilene i det daglige) og **utvikling/admin** (som retter dem). Kundeservice melder inn, admin behandler, og begge parter skriver i hvert sitt kommentarfelt underveis.

Fanen er bevisst holdt enkel: ingen tildeling til person, ingen prioritet, ingen frister, ingen kobling til kunde eller ordre. Poenget er at terskelen for å melde inn skal være så lav at folk faktisk gjør det. Alt utover det håndteres i samtalen mellom partene.

---

## 2. Datamodell

Én tabell. Ingen relasjoner.

```sql
create table ks_bugs (
    id                bigserial primary key,
    kategori          text,
    beskrivelse       text        not null default '',
    status            text        not null default 'ikke_registrert',
    opprettet_av      text,                                    -- visningsnavn, ikke fremmednøkkel
    opprettet_at      timestamptz not null default now(),
    oppdatert_at      timestamptz not null default now(),
    teknisk_kommentar text,                                    -- skrives av admin
    info_fra_ks       text                                     -- skrives av kundeservice
);

create index ks_bugs_status_idx on ks_bugs (status, opprettet_at desc);
```

**`opprettet_av` er tekst, ikke fremmednøkkel.** Saken skal overleve at brukeren slettes eller bytter rolle — det er en logglinje, ikke en relasjon.

**`oppdatert_at` settes ved hver endring** av status, teknisk kommentar og info fra KS. Den brukes ikke i grensesnittet i dag, men gjør det mulig å svare på «når skjedde det noe sist» uten en egen historikktabell.

---

## 3. Statuser

Fem statuser i fast rekkefølge. Koden lagres, etiketten vises.

| Kode | Etikett | Farge | Betydning |
|---|---|---|---|
| `ikke_registrert` | Ikke registrert | grå | nettopp meldt inn, ingen har sett på den |
| `pagaar` | Pågår | gul | admin jobber med den |
| `utfort` | Utført | grønn | rettet, venter på bekreftelse fra den som meldte |
| `utfort_ikke_lost` | Utført ikke løst | rød | forsøkt rettet, men problemet består |
| `utfort_arkivert` | Utført arkivert | grå-blå | rettet og bekreftet — ute av arbeidslista |

**Skillet mellom `utfort` og `utfort_arkivert` er hele poenget med statusmodellen.** «Utført» betyr at utvikleren mener seg ferdig; «Utført arkivert» betyr at kundeservice har bekreftet det. Uten det skillet forsvinner saker ut av lista på utviklerens ord alene, og den som meldte inn får aldri vite om det faktisk ble løst.

`utfort_ikke_lost` finnes av samme grunn: den fanger opp «vi trodde vi fikset det, men nei» uten at saken må meldes inn på nytt og miste historikken sin.

Ingen begrensning på hvem som kan sette hvilken status, og ingen tvungen rekkefølge. Fanen speiler en samtale mellom to parter som stoler på hverandre — teknisk håndheving ville bare vært i veien.

---

## 4. Kategorier

Fri tekst i basen, men UI tilbyr sju faste valg:

`Feil / bug` · `Forbedring` · `Design / UX` · `Data / tall` · `Ytelse` · `Ny funksjon` · `Annet`

Første valg er standard. Kategorien brukes bare til visning og gruppering ved lesing — den styrer ingen logikk.

---

## 5. Skjermbildet

Fire deler under hverandre.

### 5.1 Innmeldingsboks (øverst)

Én rad: **kategori** (nedtrekk) · **beskrivelse** (flerlinjes tekstfelt, 2 rader) · **Meld inn**-knapp.

- Knappen er deaktivert når beskrivelsen er tom eller en innsending pågår.
- Ved innsending: lagre, tøm tekstfeltet, last lista på nytt. Kategorien beholdes — folk melder ofte inn flere saker av samme type på rad.
- `opprettet_av` settes fra innlogget brukers visningsnavn.

### 5.2 Statusfilter

Knapperad: **Alle (n)** + én knapp per status **unntatt arkivert**, hver med antall.

Knappene bærer sin egen statusfarge, slik at fargekoden læres samme sted som den brukes. Valgt knapp får en ring rundt seg.

Tellingen på «Alle» gjelder **ikke-arkiverte** saker — det er arbeidsmengden, og det er det tallet folk faktisk spør om.

### 5.3 Arbeidsliste (tabell)

Viser alle saker som **ikke** er arkivert, filtrert på valgt status.

| Kolonne | Innhold | Redigerbar |
|---|---|---|
| Kategori | merke med kategorinavn | nei |
| Beskrivelse | innmeldt tekst, flyter over flere linjer | nei |
| Meldt av | navn + dato (`dd.MM.åå`) | nei |
| Status | nedtrekk med statusfarge | **alle** |
| Info fra KS | tekstfelt | **alle** |
| Teknisk kommentar | tekstfelt for admin, ren tekst for øvrige | **kun admin** |
| — | slett-knapp | alle |

**De to kommentarfeltene er bevisst adskilt.** «Info fra KS» er kundeservice sin egen beskrivelse av hva de så — hvilken kunde, hvilket steg, hva som er forsøkt. «Teknisk kommentar» er admins svar. Partene leser hverandres felt, men skriver hver sitt. Med ett delt felt overskriver de hverandre, og det blir umulig å se hvem som mente hva.

Beskrivelsen er ikke redigerbar etter innsending. Den er referansepunktet begge parter diskuterer rundt.

Alle endringer lagres ved `change` (når feltet forlates), ikke ved hvert tastetrykk, og lista lastes på nytt etterpå.

### 5.4 Arkiv (nederst, lukket som standard)

Sammenleggbar seksjon med alle `utfort_arkivert`-saker og antall i tittelen. Samme kolonner som arbeidslista, og statusen kan settes tilbake derfra hvis en sak må gjenåpnes.

**Arkivet er skilt ut fordi arbeidslista skal vise det som krever oppfølging.** Med 30 ferdige saker blant 8 aktive slutter folk å lese lista.

---

## 6. Sortering

Sorteres i spørringen, ikke i grensesnittet:

```sql
order by case status
           when 'ikke_registrert' then 0
           when 'pagaar'          then 1
           else 2
         end,
         opprettet_at desc
```

Ubehandlede først, deretter pågående, så resten — og nyeste først innenfor hver gruppe. **Det som ingen har sett på skal ligge øverst**, ikke det som tilfeldigvis er nyest.

---

## 7. Varsel på fanen

Fanetittelen viser antall saker i status `ikke_registrert`:

```
Bugs (3)
```

Ingenting vises når tallet er null.

**Varselet teller bare ubehandlede saker.** Et tall som inkluderer pågående saker står på det samme i dagevis og slutter å bety noe. Her forsvinner det i det øyeblikket noen setter status til «Pågår» — altså når noen har tatt ansvar for saken.

Tellingen skjer klientsiden fra lista som allerede er lastet; ingen egen spørring.

---

## 8. Tilganger

| Handling | Hvem |
|---|---|
| Se fanen | alle med tilgang til Kundeservice |
| Melde inn sak | alle |
| Endre status | alle |
| Skrive «Info fra KS» | alle |
| Skrive «Teknisk kommentar» | **kun admin** (øvrige ser feltet som tekst) |
| Slette sak | alle |

Bare ett felt er rollestyrt. Alt annet er åpent, fordi fanen er bygget for et lite team der friksjon koster mer enn feilbruk.

**Merk at sletting er åpen for alle og ikke har bekreftelsesdialog.** Det er en bevisst forenkling i vårt oppsett, men bør vurderes på nytt i et system med flere brukere — enten bekreftelse, myk sletting (`slettet_at`) eller begrensning til admin.

---

## 9. Tjenestelag

Seks operasjoner. Ingen forretningslogikk utover trimming og null-håndtering.

```
GetBugsAsync()                                    → alle saker, sortert som i pkt. 6
OpprettBugAsync(kategori, beskrivelse, opprettetAv) → ny sak, returnerer id
OppdaterBugStatusAsync(id, status)                → status + oppdatert_at
OppdaterBugTekniskKommentarAsync(id, verdi)       → admin-kommentar + oppdatert_at
OppdaterBugInfoFraKsAsync(id, verdi)              → KS-kommentar + oppdatert_at
SlettBugAsync(id)                                 → hard delete
```

Tomme strenger lagres som `null` i kommentarfeltene, slik at «tomt» er én tilstand og ikke to.

Lista lastes i sin helhet ved hver endring. Med saksmengden her (titalls, ikke tusener) er det raskere å skrive enn delvis oppdatering, og det fjerner en hel klasse feil der grensesnittet viser noe annet enn basen.

---

## 10. Ytelse og skala

Tabellen inneholder 38 rader i produksjon etter en måneds bruk (eldste sak 7. juli 2026). Alt lastes i én spørring uten paginering, og indeksen på `(status, opprettet_at desc)` dekker sorteringen.

**Trenger du paginering eller søk, er det et tegn på at fanen brukes til noe annet enn den er tenkt for** — da er et ordentlig saksverktøy riktigere valg enn å bygge dette videre.

---

## 11. Hvis du bygger dette på nytt

Rekkefølgen som gir noe brukbart raskest:

1. Tabell + de seks operasjonene
2. Innmeldingsboks og tabell med statusnedtrekk — nå kan folk melde inn og du kan behandle
3. Sortering og statusfilter
4. De to adskilte kommentarfeltene
5. Arkiv-skillet og fanevarselet

Punkt 1–3 er en fungerende fane. Punkt 4 og 5 er det som gjør at den fortsatt brukes etter tre måneder.

**Én ting jeg ville gjort annerledes fra start:** en enkel historikk over statusendringer (hvem, fra, til, når). Vi har `oppdatert_at`, men ikke hvem som endret hva. Når en sak går fram og tilbake mellom «Utført» og «Utført ikke løst», er det akkurat den historikken man vil se — og den kan ikke rekonstrueres i ettertid.
