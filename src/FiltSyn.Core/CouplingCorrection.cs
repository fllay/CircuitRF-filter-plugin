namespace FiltSyn.Core;

/// <summary>How two resonators couple across a gap (Hong and Lancaster section 8.2).</summary>
public enum CouplingKind
{
    /// <summary>Open-loop rings with their open gaps facing.</summary>
    Electric,
    /// <summary>Open-loop rings with their folds facing.</summary>
    Magnetic,
    /// <summary>Open-loop rings with arms facing (any other pair of sides, and the ring chain).</summary>
    Mixed,
    /// <summary>Adjacent hairpins (arms facing).</summary>
    Hairpin,
}

/// <summary>
/// EM-fitted correction of the coupling between resonators. The closed-form coupled-line model (Kirschning-Jansen
/// even/odd data, nearest neighbours, straight sections between the corners) overestimates the coupling between
/// resonators, most where the electric and magnetic parts nearly cancel. Planar EM of weakly probed resonator pairs on
/// RO4350B 0.508 mm gave the ratio for each kind against s/h; the coupled sections' mutual capacitances (static and air)
/// are scaled by the factor below, which reproduces those EM couplings in the circuit models. Microstrip only; the
/// factor is held at the end values outside the measured s/h range and is assumed to depend on s/h alone on other
/// substrates (not yet checked).
/// </summary>
public static class CouplingCorrection
{
    /// <summary>Switch for experiments and tests; on by default.</summary>
    public static bool Enabled { get; set; } = true;

    // (s/h, mutual scale) pairs, ascending in s/h. Filled from the EM pair runs (em_cmp K*, RPX, RPM, HP2 cells).
    private static readonly Dictionary<CouplingKind, (double G, double Scale)[]> Table = new()
    {
        // Rings W 1.09 mm, side 6.51 mm, 3.6 GHz; probes gap-coupled 0.4 mm.
        [CouplingKind.Electric] = new[] { (0.59, 1.176), (1.18, 1.146), (1.97, 1.130) },
        [CouplingKind.Magnetic] = new[] { (0.98, 0.775), (1.97, 0.724), (3.14, 0.647), (4.92, 0.429) },
        [CouplingKind.Mixed] = new[] { (0.59, 0.952), (1.18, 0.911), (1.93, 0.823), (3.15, 0.765) },
        // Hairpins W 1.07 mm, arm gap 2.15 mm, 3 GHz.
        [CouplingKind.Hairpin] = new[] { (0.98, 0.422), (1.97, 0.419), (2.76, 0.339), (3.71, 0.242) },
    };

    /// <summary>For fitting: when set, replaces the table (arguments: kind, s/h).</summary>
    public static Func<CouplingKind, double, double>? Override { get; set; }

    public static double Scale(CouplingKind kind, Substrate s, double gap)
    {
        if (!Enabled || s.IsStripline) return 1.0;
        if (Override is not null) return Override(kind, gap / s.H);
        var t = Table[kind];
        double g = gap / s.H;
        if (g <= t[0].G) return t[0].Scale;
        if (g >= t[^1].G) return t[^1].Scale;
        for (int i = 0; i + 1 < t.Length; i++)
            if (g <= t[i + 1].G)
            {
                double u = (Math.Log(g) - Math.Log(t[i].G)) / (Math.Log(t[i + 1].G) - Math.Log(t[i].G));
                return t[i].Scale + u * (t[i + 1].Scale - t[i].Scale);
            }
        return t[^1].Scale;
    }
}
