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

    public static readonly BeregningParametre Standard =
        new(Rente: 8.87m, LopetidAar: 30, Stresstest: 3m, SifoVoksen: 9000m, SifoBarn: 4000m);

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
    public static BeregningResultat Beregn(Kundekort k, BeregningParametre p)
    {
        var sokerBrutto = k.AarsinntektBrutto ?? 0;
        // Medsøkers inntekt: medsoker_inntekt fra søknaden, ev. ektefelle_inntekt.
        var medsokerBrutto = k.MedsokerInntekt ?? k.EktefelleInntekt ?? 0;
        var bruttoAar = sokerBrutto + medsokerBrutto + (k.AndreInntekter ?? 0);

        // Progressiv skatt (2024-satser) — beregnes per person, siden trinnskatt er progressiv.
        var nettoAar = NettoArsinntekt(sokerBrutto) + NettoArsinntekt(medsokerBrutto) + (k.AndreInntekter ?? 0);
        var mndNetto = nettoAar / 12m;

        var voksne = (k.HarMedsoker || medsokerBrutto > 0) ? 2 : 1;
        var levekostnad = p.SifoVoksen * voksne + p.SifoBarn * (k.AntallBarnUnder18 ?? 0);
        var boligkostnad = k.BoligkostnadMnd ?? 0;
        var barnebidrag = k.BarnebidragBetaltMnd ?? 0;

        // Eksisterende gjeld. Ved refinansiering erstatter ønsket lån det som refinansieres,
        // så vi trekker fra refinansieres_belop for å unngå dobbelttelling.
        var eksisterendeGjeld = (k.Boliggjeld ?? 0) + (k.Studielaan ?? 0) + (k.Billaan ?? 0) + (k.Forbruksgjeld ?? 0);
        var totalGjeld = Math.Max(0, eksisterendeGjeld - (k.RefinansieresBelop ?? 0)) + (k.OnsketLaanebelop ?? 0);

        var stressMnd = (p.Rente + p.Stresstest) / 100m / 12m;
        var n = (k.OnsketLopetidMnd is > 0 ? k.OnsketLopetidMnd.Value : p.LopetidAar * 12);
        var gjeldsbetjening = Annuitet(totalGjeld, stressMnd, n);

        var likviditet = mndNetto - levekostnad - boligkostnad - barnebidrag - gjeldsbetjening;

        var belaning = (k.Boligverdi ?? 0) > 0
            ? ((k.Boliggjeld ?? 0) + (k.OnsketLaanebelop ?? 0)) / k.Boligverdi!.Value * 100m
            : 0;

        var gjeldsgrad = bruttoAar > 0 ? totalGjeld / bruttoAar : 0; // gjeld i x ganger inntekt

        // Maks lån: hvor mye til kan lånes så fri likviditet = 0 (ut fra dagens likviditet).
        var kostnadPerKr = Annuitet(1m, stressMnd, n);
        var maksLaan = kostnadPerKr > 0 ? Math.Max(0, likviditet / kostnadPerKr + (k.OnsketLaanebelop ?? 0)) : 0;

        return new BeregningResultat(
            Math.Round(mndNetto), Math.Round(levekostnad), Math.Round(boligkostnad),
            Math.Round(gjeldsbetjening), Math.Round(likviditet),
            Math.Round(belaning, 1), Math.Round(gjeldsgrad, 1), Math.Round(maksLaan));
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
