# Supabase — komme i gang (innlogging + brukeradmin)

Minimum oppsett for at innlogging og brukeradministrasjon skal bruke ekte data.

## 1. Kjør SQL-en
Supabase Dashboard → **SQL Editor** → lim inn innholdet i [`0001_init.sql`](0001_init.sql) → **Run**.
Dette lager tabellen `app_users` med RLS på.

## 2. Sett nøkler
Serveren snakker med databasen og trenger derfor **service_role**-nøkkelen
(Dashboard → Project Settings → API).

- **Azure** (App Service → Configuration → Application settings):
  - `Supabase__Url` = `https://<ditt-prosjekt>.supabase.co`
  - `Supabase__Key` = `<service_role-nøkkel>`
- **Lokalt** (valgfritt, for å teste mot ekte DB):
  ```bash
  cd src/RentkuttCRM
  dotnet user-secrets init
  dotnet user-secrets set "Supabase:Url" "https://<ditt-prosjekt>.supabase.co"
  dotnet user-secrets set "Supabase:Key" "<service_role-nøkkel>"
  ```

> ⚠️ service_role-nøkkelen er en superbruker. Den skal **kun** ligge i server-konfig
> (Azure App settings / user-secrets) — aldri i kildekoden eller i nettleseren.

## 3. Første innlogging
Ved første oppstart med Supabase konfigurert oppretter appen automatisk en admin
hvis tabellen er tom:

- **E-post:** `admin@rentekutt.no`
- **Passord:** `Rentekutt2026!`

Logg inn, gå til **Admin → Brukere**, opprett egne brukere og bytt/deaktiver admin etterpå.

## Uten nøkler (staging)
Er ikke `Supabase__Url`/`Supabase__Key` satt, kjører appen i staging-modus:
innlogging godtar hva som helst, og brukeradmin er midlertidig (in-memory).

## Hva som er koblet til så langt
- `app_users` — innlogging + brukeradmin (denne).
- Resten (CRM, statistikk, logikk-matrise) er fortsatt dummydata — kobles på etter hvert.
