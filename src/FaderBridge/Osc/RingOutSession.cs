namespace Fader.Bridge.Osc;

/// <summary>
/// System ring-out on the X32: read the console's RTA, find the strongest ring,
/// and cut the matching band on a 31-band GEQ (an insert FX slot). This is the
/// mains-side complement to the feedback engine's mic-side notches (§6).
///
/// It only ever <em>cuts</em> a band and never boosts, and it remembers what it
/// cut so a band deepens on repeat rather than stacking, and can be reset to flat.
/// </summary>
public sealed class RingOutSession
{
    private readonly Action<OscMessage> _send;
    private readonly X32Rta _rta;
    private readonly int _geqSlot;
    private readonly Dictionary<int, float> _cutDb = new();   // GEQ band (1..31) -> current cut (negative dB)

    public RingOutSession(Action<OscMessage> send, X32Rta rta, int geqSlot)
    {
        _send = send;
        _rta = rta;
        _geqSlot = geqSlot;
    }

    public int GeqSlot => _geqSlot;

    /// <summary>A ring the RTA is showing, mapped to the GEQ band that would cut it.</summary>
    public sealed record Ring(int RtaBand, float FreqHz, float Db, int GeqBand);

    /// <summary>The current strongest ring above the threshold, or null. Read-only.</summary>
    public Ring? Detect(float thresholdDb)
    {
        if (_rta.Peak(thresholdDb) is not { } peak)
        {
            return null;
        }
        return new Ring(peak.Band, peak.FreqHz, peak.Db, X32Geq.NearestBand(peak.FreqHz));
    }

    /// <summary>Deepen the cut on a GEQ band by <paramref name="stepDb"/>, down to <paramref name="maxCutDb"/>.</summary>
    public float Cut(int geqBand, float stepDb, float maxCutDb)
    {
        var current = _cutDb.GetValueOrDefault(geqBand, 0f);
        var next = MathF.Max(maxCutDb, current - MathF.Abs(stepDb));   // maxCutDb is negative
        _cutDb[geqBand] = next;
        _send(new OscMessage(X32Geq.Band(_geqSlot, geqBand), X32Geq.DbToPar(next)));
        return next;
    }

    /// <summary>One automatic pass: detect the strongest ring and cut it. Returns what it did.</summary>
    public Ring? Step(float thresholdDb, float stepDb, float maxCutDb)
    {
        if (Detect(thresholdDb) is not { } ring)
        {
            return null;
        }
        Cut(ring.GeqBand, stepDb, maxCutDb);
        return ring;
    }

    /// <summary>Bands this session has cut, with their current cut depth in dB.</summary>
    public IReadOnlyDictionary<int, float> Cuts => _cutDb;

    /// <summary>Return every band this session touched to flat (0 dB).</summary>
    public void ResetAll()
    {
        foreach (var band in _cutDb.Keys)
        {
            _send(new OscMessage(X32Geq.Band(_geqSlot, band), X32Geq.DbToPar(0f)));
        }
        _cutDb.Clear();
    }
}
