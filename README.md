# Rentekutt CRM

Et CRM-system bygget med .NET 10, Supabase og Azure App Service.

## Tech Stack

- **Backend**: ASP.NET Core (.NET 10)
- **Database**: Supabase (PostgreSQL)
- **Hosting**: Azure App Service (Sweden Central)
- **CI/CD**: GitHub Actions → Azure

## Kom i gang

### Forutsetninger
- .NET 10 SDK
- Git
- Tilgang til Supabase-prosjektet

### Lokal utvikling

```bash
git clone https://github.com/konradreinfjord/Rentekutt.git
cd Rentekutt
dotnet restore
dotnet run
```

## Miljøvariabler

Legg til følgende i `appsettings.Development.json` eller som miljøvariabler:

```json
{
  "Supabase": {
      "Url": "din-supabase-url",
          "Key": "din-supabase-anon-key"
            }
            }
            ```

            ## Deploy

            Alle pushes til `main` deployes automatisk til Azure via GitHub Actions.
