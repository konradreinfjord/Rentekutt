namespace RentkuttCRM.Services;

/// <summary>Justerbare parametre i beregningsmodellen (lagres i innstillinger).
/// Skatt beregnes progressivt (2024-satser) — ikke en flat sats.</summary>
public record BeregningParametre(
    decimal Rente,        // nominell rente % (eff. lånekostnad)
    int LopetidAar,       // nedbetalingstid
    decimal Stresstest,   // påslag prosentpoeng for stresstest (Finanstilsynet)
    decimal SifoVoksen,   // levekostnad per voksen / mnd (SIFO referansebudsjett)
    decimal SifoBarn);    // levekostnad per barn / mnd

/// <summary>Resultat av beregningen for ett kundekort.</summary>
public record BeregningResultat(
    decimal MndInntektNetto,
    decimal Levekostnad,
    decimal Boligkostnad,
    decimal Gjeldsbetjening,
    decimal Likviditet,
    decimal Belaningsgrad,
    decimal Gjeldsgrad,
    decimal MaksLaan);

/// <summary>Én linje i den detaljerte breakdownen — for verifisering.</summary>
public record BeregningLinje(string Seksjon, string Komponent, string Kilde, string Formel, decimal Belop, bool ErSum = false);

/// <summary>
/// Forenklet finansieringsevne-/likviditetsmodell basert på Beregningsmodell.xlsx
/// (SIFO-levekostnader, stresstestet gjeldsbetjening, rentefradrag).
/// </summary>
public class BeregningService
{
    public const string KeyRente = "beregn_rente";
    public const string KeyLopetid = "beregn_lopetid_aar";
    public const string KeyStress = "beregn_stresstest";
    public const string KeySifoVoksen = "beregn_sifo_voksen";
    public const string KeySifoBarn = "beregn_sifo_barn";

    // SifoVoksen/SifoBarn brukes som fallback for individkostnad når fødselsnummer mangler.
    public static readonly BeregningParametre Standard =
        new(Rente: 8.87m, LopetidAar: 30, Stresstest: 3m, SifoVoksen: 5300m, SifoBarn: 3500m);

    private readonly SettingsService _settings;
    public BeregningService(SettingsService settings) => _settings = settings;

    public async Task<BeregningParametre> HentParametreAsync() => new(
        await _settings.GetDecimalAsync(KeyRente, Standard.Rente),
        (int)await _settings.GetDecimalAsync(KeyLopetid, Standard.LopetidAar),
        await _settings.GetDecimalAsync(KeyStress, Standard.Stresstest),
        await _settings.GetDecimalAsync(KeySifoVoksen, Standard.SifoVoksen),
        await _settings.GetDecimalAsync(KeySifoBarn, Standard.SifoBarn));

    public async Task LagreParametreAsync(BeregningParametre p)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        await _settings.SetAsync(KeyRente, p.Rente.ToString(inv));
        await _settings.SetAsync(KeyLopetid, p.LopetidAar.ToString());
        await _settings.SetAsync(KeyStress, p.Stresstest.ToString(inv));
        await _settings.SetAsync(KeySifoVoksen, p.SifoVoksen.ToString(inv));
        await _settings.SetAsync(KeySifoBarn, p.SifoBarn.ToString(inv));
    }

    /// <summary>Beregn likviditet, belåningsgrad og maks lån for et kundekort.</summary>
    public static BeregningResultat Beregn(Kundekort k, BeregningParametre p) => Detaljert(k, p).Resultat;

    /// <summary>Full breakdown med formler og kildefelt — for verifisering.</summary>
    public static (BeregningResultat Resultat, List<BeregningLinje> Linjer) Detaljert(Kundekort k, BeregningParametre p)
    {
        var L = new List<BeregningLinje>();
        void Add(string s, string komp, string kilde, string formel, decimal belop, bool sum = false)
            => L.Add(new BeregningLinje(s, komp, kilde, formel, Math.Round(belop), sum));

        // ---- Inntekt ----
        var sokerBrutto = k.AarsinntektBrutto ?? 0;
        var medsokerBrutto = k.MedsokerInntekt ?? k.EktefelleInntekt ?? 0;
        var andreInntekt = k.AndreInntekter ?? 0;
        var bruttoAar = sokerBrutto + medsokerBrutto + andreInntekt;

        var nettoSoker = NettoArsinntekt(sokerBrutto);
        var nettoMedsoker = NettoArsinntekt(medsokerBrutto);
        var nettoAar = nettoSoker + nettoMedsoker + andreInntekt;
        var mndNetto = nettoAar / 12m;

        Add("Inntekt", "Brutto søker", "aarsinntekt_brutto", "fra søknad", sokerBrutto);
        if (medsokerBrutto > 0) Add("Inntekt", "Brutto medsøker", "medsoker_inntekt / ektefelle_inntekt", "fra søknad", medsokerBrutto);
        if (andreInntekt > 0) Add("Inntekt", "Andre inntekter", "andre_inntekter", "fra søknad", andreInntekt);
        Add("Inntekt", "Netto søker", "—", "etter progressiv skatt 2024", nettoSoker);
        if (medsokerBrutto > 0) Add("Inntekt", "Netto medsøker", "—", "etter progressiv skatt 2024", nettoMedsoker);
        Add("Inntekt", "Netto pr. mnd", "—", "netto årsinntekt ÷ 12", mndNetto, true);

        // ---- Levekostnad (SIFO) ----
        var barn = k.AntallBarnUnder18 ?? 0;
        var (alderSoker, kjonnSoker) = FnrInfo(k.Foedselsnummer);
        var indivSoker = alderSoker is int aS ? IndividKostnad(aS, kjonnSoker ?? 'K') : p.SifoVoksen;
        var levekostnad = indivSoker;
        var voksne = 1;
        Add("Levekostnad (SIFO)", "Individkostnad søker",
            alderSoker is int ? $"foedselsnummer → {alderSoker} år {(kjonnSoker == 'M' ? "mann" : "kvinne")}" : "fnr mangler → fallback",
            "SIFO sum 1-5 (alder/kjønn)", indivSoker);

        var harMedsoker = k.HarMedsoker || medsokerBrutto > 0 || !string.IsNullOrWhiteSpace(k.MedsokerFoedselsnummer);
        if (harMedsoker)
        {
            voksne = 2;
            var (alderM, kjonnM) = FnrInfo(k.MedsokerFoedselsnummer);
            var indivM = alderM is int aM ? IndividKostnad(aM, kjonnM ?? 'M') : p.SifoVoksen;
            levekostnad += indivM;
            Add("Levekostnad (SIFO)", "Individkostnad medsøker",
                alderM is int ? $"medsoker_foedselsnummer → {alderM} år {(kjonnM == 'M' ? "mann" : "kvinne")}" : "fnr mangler → fallback",
                "SIFO sum 1-5 (alder/kjønn)", indivM);
        }

        if (barn > 0)
        {
            var barnKost = p.SifoBarn * barn;
            levekostnad += barnKost;
            Add("Levekostnad (SIFO)", "Barns individkostnad", "antall_barn_under_18", $"{barn} barn × {p.SifoBarn:N0}", barnKost);
        }

        var personer = voksne + barn;
        var husholdKost = HusholdKostnad(personer);
        levekostnad += husholdKost;
        Add("Levekostnad (SIFO)", "Husholdsfelleskostnad", $"{personer} personer i husstand", "dagligvarer + artikler + møbler + telefon", husholdKost);

        var biler = k.AntallBiler ?? 0;
        if (biler > 0)
        {
            var bilKost = BilKostnad(personer) * biler;
            levekostnad += bilKost;
            Add("Levekostnad (SIFO)", "Bilkostnad", "antall_biler", $"{biler} bil × {BilKostnad(personer):N0} (drift/vedlikehold)", bilKost);
        }
        Add("Levekostnad (SIFO)", "Sum levekostnad", "—", "sum av postene over", levekostnad, true);

        // ---- Faste bokostnader ----
        var boligkostnad = k.BoligkostnadMnd ?? 0;
        var barnebidrag = k.BarnebidragBetaltMnd ?? 0;
        if (boligkostnad > 0) Add("Bokostnader", "Boligkostnad/mnd", "boligkostnad_mnd", "fra søknad", boligkostnad);
        if (barnebidrag > 0) Add("Bokostnader", "Barnebidrag/mnd", "barnebidrag_betalt_mnd", "fra søknad", barnebidrag);

        // ---- Gjeld og betjening ----
        var eksisterendeGjeld = (k.Boliggjeld ?? 0) + (k.Studielaan ?? 0) + (k.Billaan ?? 0) + (k.Forbruksgjeld ?? 0);
        var refi = k.RefinansieresBelop ?? 0;
        var onsket = k.OnsketLaanebelop ?? 0;
        var totalGjeld = Math.Max(0, eksisterendeGjeld - refi) + onsket;

        Add("Gjeld", "Eksisterende gjeld", "boliggjeld + studielaan + billaan + forbruksgjeld", "sum fra søknad", eksisterendeGjeld);
        if (refi > 0) Add("Gjeld", "− Refinansieres", "refinansieres_belop", "erstattes av ønsket lån", -refi);
        Add("Gjeld", "+ Ønsket lån", "onsket_laanebelop", "fra søknad", onsket);
        Add("Gjeld", "Total gjeld for betjening", "—", "eksisterende − refi + ønsket", totalGjeld, true);

        var stressMnd = (p.Rente + p.Stresstest) / 100m / 12m;
        var n = k.OnsketLopetidMnd is > 0 ? k.OnsketLopetidMnd.Value : p.LopetidAar * 12;
        var gjeldsbetjening = Annuitet(totalGjeld, stressMnd, n);
        Add("Gjeld", "Gjeldsbetjening/mnd", $"onsket_lopetid_mnd ({n} mnd)", $"annuitet @ stressrente {p.Rente + p.Stresstest:0.##}%", gjeldsbetjening, true);

        // ---- Resultat ----
        var likviditet = mndNetto - levekostnad - boligkostnad - barnebidrag - gjeldsbetjening;
        Add("Resultat", "Fri likviditet/mnd", "—", "netto − levekostnad − bolig − barnebidrag − gjeldsbetjening", likviditet, true);

        var belaning = (k.Boligverdi ?? 0) > 0
            ? ((k.Boliggjeld ?? 0) + onsket) / k.Boligverdi!.Value * 100m
            : 0;
        var gjeldsgrad = bruttoAar > 0 ? totalGjeld / bruttoAar : 0;
        var kostnadPerKr = Annuitet(1m, stressMnd, n);
        var maksLaan = kostnadPerKr > 0 ? Math.Max(0, likviditet / kostnadPerKr + onsket) : 0;

        var res = new BeregningResultat(
            Math.Round(mndNetto), Math.Round(levekostnad), Math.Round(boligkostnad),
            Math.Round(gjeldsbetjening), Math.Round(likviditet),
            Math.Round(belaning, 1), Math.Round(gjeldsgrad, 1), Math.Round(maksLaan));
        return (res, L);
    }

    // Eksponert for referansevisning i admin.
    public static IReadOnlyList<int> SifoAldersTerskel => AldersTerskel;
    public static IReadOnlyList<decimal> SifoIndivKvinne => IndivKvinne;
    public static IReadOnlyList<decimal> SifoIndivMann => IndivMann;
    public static IReadOnlyList<decimal> SifoHushold => HusholdDel;
    public static IReadOnlyList<decimal> SifoBil => BilDrift;

    // ---- SIFO referansebudsjett (fra Beregningsmodell.xlsx, ark X_Data) ----
    // Individkostnad (mat, klær, helse, fritid, reise) — sum 1-5 per måned, etter aldersterskel.
    private static readonly int[] AldersTerskel = { 0, 1, 2, 3, 4, 5, 6, 10, 14, 18, 20, 51, 60, 67 };
    private static readonly decimal[] IndivKvinne = { 1380, 2050, 2380, 2520, 2780, 2780, 3530, 4060, 4870, 4930, 5270, 5210, 4980, 4640 };
    private static readonly decimal[] IndivMann = { 1380, 2050, 2380, 2520, 2780, 2780, 3530, 4130, 5120, 5190, 5530, 5530, 5100, 4760 };
    // Husholdsfelleskostnad (andre dagligvarer + husholdningsartikler + møbler + telefon/media) etter antall personer 1-7.
    private static readonly decimal[] HusholdDel = { 2400, 2550, 2830, 3340, 3610, 3950, 4160 };
    // Bilkostnad (drift, vedlikehold) per bil etter antall personer 1-7.
    private static readonly decimal[] BilDrift = { 2280, 2280, 2280, 2280, 3130, 3130, 3130 };

    private static decimal IndividKostnad(int alder, char kjonn)
    {
        var tab = kjonn == 'M' ? IndivMann : IndivKvinne;
        var idx = 0;
        for (var i = 0; i < AldersTerskel.Length; i++) if (alder >= AldersTerskel[i]) idx = i;
        return tab[idx];
    }

    private static decimal HusholdKostnad(int personer) => HusholdDel[Math.Clamp(personer, 1, 7) - 1];
    private static decimal BilKostnad(int personer) => BilDrift[Math.Clamp(personer, 1, 7) - 1];

    /// <summary>Utleder alder og kjønn (K/M) fra norsk 11-sifret fødselsnummer / D-nummer.</summary>
    public static (int? Alder, char? Kjonn) FnrInfo(string? fnr)
    {
        if (string.IsNullOrWhiteSpace(fnr)) return (null, null);
        var d = new string(fnr.Where(char.IsDigit).ToArray());
        if (d.Length != 11) return (null, null);

        var dd = int.Parse(d.Substring(0, 2));
        var mm = int.Parse(d.Substring(2, 2));
        var yy = int.Parse(d.Substring(4, 2));
        var ind = int.Parse(d.Substring(6, 3)); // individnummer
        if (dd > 40) dd -= 40;                   // D-nummer
        if (mm > 40) mm -= 40;                   // H-/syntetisk nummer

        int aar;
        if (ind <= 499) aar = 1900 + yy;
        else if (ind is >= 500 and <= 749 && yy >= 54) aar = 1800 + yy;
        else if (ind is >= 500 and <= 999 && yy <= 39) aar = 2000 + yy;
        else if (ind is >= 900 and <= 999 && yy >= 40) aar = 1900 + yy;
        else aar = 1900 + yy;

        var kjonn = (d[8] - '0') % 2 == 0 ? 'K' : 'M';
        if (mm is < 1 or > 12 || dd is < 1 or > 31) return (null, kjonn);

        DateTime fodt;
        try { fodt = new DateTime(aar, mm, dd); } catch { return (null, kjonn); }
        var i_dag = DateTime.Today;
        var alder = i_dag.Year - fodt.Year;
        if (fodt.Date > i_dag.AddYears(-alder)) alder--;
        if (alder is < 0 or > 120) return (null, kjonn);
        return (alder, kjonn);
    }

    /// <summary>Netto årsinntekt etter norsk lønnsskatt (2024-satser): trygdeavgift,
    /// minstefradrag, personfradrag, alminnelig inntektsskatt 22 % og trinnskatt.</summary>
    public static decimal NettoArsinntekt(decimal brutto)
    {
        if (brutto <= 0) return 0;
        const decimal trygdeavgift = 0.078m;          // lønnsinntekt
        const decimal minstefradragSats = 0.46m;
        const decimal minstefradragMaks = 109950m;    // 2024
        const decimal personfradrag = 88250m;         // 2024
        const decimal alminneligSats = 0.22m;

        var trygde = brutto * trygdeavgift;
        var minstefradrag = Math.Min(brutto * minstefradragSats, minstefradragMaks);
        var grunnlag = Math.Max(0, brutto - minstefradrag - personfradrag);
        var alminnelig = grunnlag * alminneligSats;
        var skatt = trygde + alminnelig + Trinnskatt(brutto);
        return brutto - skatt;
    }

    /// <summary>Trinnskatt 2024 (personinntekt), progressivt per trinn.</summary>
    private static decimal Trinnskatt(decimal pi)
    {
        decimal Band(decimal lo, decimal hi, decimal sats) => pi > lo ? (Math.Min(pi, hi) - lo) * sats : 0;
        return Band(208050m, 292850m, 0.017m)
             + Band(292850m, 670000m, 0.040m)
             + Band(670000m, 937900m, 0.136m)
             + Band(937900m, 1350000m, 0.166m)
             + Band(1350000m, decimal.MaxValue, 0.176m);
    }

    /// <summary>Annuitetslån: månedlig terminbeløp.</summary>
    private static decimal Annuitet(decimal hovedstol, decimal mndRente, int terminer)
    {
        if (hovedstol <= 0 || terminer <= 0) return 0;
        if (mndRente <= 0) return hovedstol / terminer;
        var r = (double)mndRente;
        var faktor = r / (1 - Math.Pow(1 + r, -terminer));
        return hovedstol * (decimal)faktor;
    }
}
