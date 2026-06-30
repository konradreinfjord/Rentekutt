namespace RentkuttCRM.Services;

/// <summary>Justerbare parametre i beregningsmodellen (lagres i innstillinger).</summary>
public record BeregningParametre(
    decimal Rente,        // nominell rente % (eff. lånekostnad)
    int LopetidAar,       // nedbetalingstid
    decimal Skattesats,   // effektiv skatt på inntekt %
    decimal Stresstest,   // påslag prosentpoeng for stresstest (Finanstilsynet)
    decimal SifoVoksen,   // levekostnad per voksen / mnd (forenklet SIFO)
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
    public const string KeySkatt = "beregn_skattesats";
    public const string KeyStress = "beregn_stresstest";
    public const string KeySifoVoksen = "beregn_sifo_voksen";
    public const string KeySifoBarn = "beregn_sifo_barn";

    public static readonly BeregningParametre Standard =
        new(Rente: 8.87m, LopetidAar: 30, Skattesats: 28m, Stresstest: 3m, SifoVoksen: 9000m, SifoBarn: 4000m);

    private readonly SettingsService _settings;
    public BeregningService(SettingsService settings) => _settings = settings;

    public async Task<BeregningParametre> HentParametreAsync() => new(
        await _settings.GetDecimalAsync(KeyRente, Standard.Rente),
        (int)await _settings.GetDecimalAsync(KeyLopetid, Standard.LopetidAar),
        await _settings.GetDecimalAsync(KeySkatt, Standard.Skattesats),
        await _settings.GetDecimalAsync(KeyStress, Standard.Stresstest),
        await _settings.GetDecimalAsync(KeySifoVoksen, Standard.SifoVoksen),
        await _settings.GetDecimalAsync(KeySifoBarn, Standard.SifoBarn));

    public async Task LagreParametreAsync(BeregningParametre p)
    {
        await _settings.SetAsync(KeyRente, p.Rente.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await _settings.SetAsync(KeyLopetid, p.LopetidAar.ToString());
        await _settings.SetAsync(KeySkatt, p.Skattesats.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await _settings.SetAsync(KeyStress, p.Stresstest.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await _settings.SetAsync(KeySifoVoksen, p.SifoVoksen.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await _settings.SetAsync(KeySifoBarn, p.SifoBarn.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Beregn likviditet, belåningsgrad og maks lån for et kundekort.</summary>
    public static BeregningResultat Beregn(Kundekort k, BeregningParametre p)
    {
        var bruttoAar = (k.AarsinntektBrutto ?? 0) + (k.EktefelleInntekt ?? 0) + (k.AndreInntekter ?? 0);
        var mndNetto = bruttoAar / 12m * (1 - p.Skattesats / 100m);

        var voksne = k.HarMedsoker ? 2 : 1;
        var levekostnad = p.SifoVoksen * voksne + p.SifoBarn * (k.AntallBarnUnder18 ?? 0);
        var boligkostnad = k.BoligkostnadMnd ?? 0;
        var barnebidrag = k.BarnebidragBetaltMnd ?? 0;

        var totalGjeld = (k.Boliggjeld ?? 0) + (k.Studielaan ?? 0) + (k.Billaan ?? 0)
                         + (k.Forbruksgjeld ?? 0) + (k.OnsketLaanebelop ?? 0);

        var stressMnd = (p.Rente + p.Stresstest) / 100m / 12m;
        var n = p.LopetidAar * 12;
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
