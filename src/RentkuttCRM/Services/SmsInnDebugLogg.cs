using System.Collections.Concurrent;

namespace RentkuttCRM.Services;

/// <summary>
/// Liten in-memory logg over de siste treffene på inbound-SMS-endepunktet (MO-webhooken).
/// Brukes til feilsøking: ser vi treff her, NÅR LinkMobility faktisk oss — og vi ser utfallet
/// (mangler token / ugyldig token / parset ikke avsender / mottatt OK). Ser vi ingen treff,
/// er ikke MO-videresendingen satt opp hos LinkMobility ennå. Nullstilles ved omstart.
/// </summary>
public class SmsInnDebugLogg
{
    public record Treff(DateTime Tid, string Utfall, string Detalj);

    private readonly ConcurrentQueue<Treff> _treff = new();
    private const int Maks = 25;

    public void Logg(string utfall, string detalj)
    {
        _treff.Enqueue(new Treff(DateTime.UtcNow, utfall, detalj ?? ""));
        while (_treff.Count > Maks && _treff.TryDequeue(out _)) { }
    }

    public IReadOnlyList<Treff> Siste() => _treff.Reverse().ToList();
}
