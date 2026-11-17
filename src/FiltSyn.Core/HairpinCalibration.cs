namespace FiltSyn.Core;

/// <summary>
/// Design curves for one hairpin style (fixed width and fold gap) extracted from the circuit model the way an
/// EM tool would: the arm length that makes a single hairpin resonate at f0, the coupling coefficient between
/// two hairpins from the split of their natural frequencies, and the external Q of a tapped hairpin from the
/// group delay of its reflection. The same quantities can later be filled from EM data instead.
/// </summary>
public sealed class HairpinCalibration
{
    public Substrate Substrate { get; }
    public double F0Hz { get; }
    public double ResonatorWidth { get; }
    public double ArmGap { get; }
    public double OpenEndExtension { get; }
    public double FeedZ0 { get; }

    /// <summary>Gap between the tail tips of an open-loop ring (m); zero for a hairpin.</summary>
    public double TipGap { get; }

    /// <summary>Neighbouring resonators are turned upside down (see <see cref="HairpinOptions.Alternate"/>).</summary>
    public bool Alternate { get; }

    /// <summary>Arm length (base centreline to tip) at which a single hairpin resonates at f0.</summary>
    public double TunedArmLength { get; }

    /// <summary>
    /// k versus edge-to-edge gap, tabulated from the circuit model. Covers only the monotonic branch from the
    /// coupling maximum outward; for same-orientation hairpins the electric and magnetic parts oppose, so k
    /// peaks at some gap and falls again as the gap shrinks further.
    /// </summary>
    public TabulatedCouplingCurve CouplingCurve { get; }

    /// <summary>Largest coupling coefficient reachable between neighbouring hairpins in this style.</summary>
    public double MaxCoupling { get; }

    /// <summary>Gap at which <see cref="MaxCoupling"/> occurs (m); smaller gaps give less coupling.</summary>
    public double GapAtMaxCoupling { get; }

    private HairpinCalibration(Substrate s, double f0, double w, double a, double dl, double feedZ0, double tipGap, bool alternate,
        double arm, TabulatedCouplingCurve curve, double kMax, double gapAtKMax)
    {
        Alternate = alternate;
        Substrate = s; F0Hz = f0; ResonatorWidth = w; ArmGap = a; OpenEndExtension = dl; FeedZ0 = feedZ0; TipGap = tipGap;
        TunedArmLength = arm; CouplingCurve = curve; MaxCoupling = kMax; GapAtMaxCoupling = gapAtKMax;
    }

    private static HairpinGeometry Geometry(Substrate s, int order, double w, double a, double arm,
        double[] gaps, double tap, double dl, double feedZ0, double tipGap = 0.0, bool alternate = false)
        => new(s, order, w, a, arm, gaps, tap, dl, feedZ0, tipGap, alternate);

    private static double FirstResonance(HairpinGeometry g, double f0)
    {
        var roots = new HairpinCircuitModel(g).NaturalFrequencies(0.4 * f0, 2.6 * f0, 6000);
        if (roots.Length == 0) throw new InvalidOperationException("No resonance found near the design frequency.");
        return roots[0];
    }

    /// <param name="tipGap">Greater than zero for an open-loop ring: the gap between the tail tips (m).</param>
    /// <param name="square">
    /// With <paramref name="tipGap"/>, make the loop square: the arm length equals the base length (arm centreline to arm
    /// centreline), so the inner gap is the arm length minus the width and <paramref name="armGap"/> is ignored. The
    /// side length is then the quantity tuned to f0.
    /// </param>
    public static HairpinCalibration Run(Substrate s, double f0Hz, double w, double armGap, double openEndExt,
        double feedZ0 = 50.0, double gapMin = 0.05e-3, double gapMax = 6e-3, int points = 14,
        double tipGap = 0.0, bool square = false, bool alternate = false)
    {
        if (square && tipGap <= 0) throw new ArgumentException("A square loop needs a positive tip gap.", nameof(tipGap));
        // The inner gap follows the arm length for a square loop.
        double InnerGap(double arm) => square ? arm - w : armGap;

        // 1. Arm length for a single-hairpin resonance at f0 (resonance falls as the arm lengthens).
        // A hairpin's path is about a half wave (two arms and the fold: arm ~ lambda/4); a square ring's is four sides
        // less the gap, so a side is about lambda/8.
        double lgQuarter = Microstrip.GuidedWavelength(s, w, f0Hz) / (square ? 8.0 : 4.0);
        double lo = (square ? 0.5 : 0.6) * lgQuarter, hi = (square ? 1.6 : 1.4) * lgQuarter;
        if (square) lo = Math.Max(lo, tipGap * 1.05 + 2.0 * w);      // keep the tails and the inner gap positive
        // Observe from a tap well away from the fundamental's voltage null (open-port resonances do not depend on it).
        double Observe(double arm) => 0.35 * (2.0 * arm + InnerGap(arm) + w);
        double ResAt(double arm) => FirstResonance(Geometry(s, 1, w, InnerGap(arm), arm, Array.Empty<double>(), Observe(arm), openEndExt, feedZ0, tipGap), f0Hz);
        if (ResAt(lo) < f0Hz || ResAt(hi) > f0Hz)
            throw new InvalidOperationException("Could not bracket the hairpin arm length for this substrate and frequency.");
        for (int i = 0; i < 50; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (ResAt(mid) > f0Hz) lo = mid; else hi = mid;
        }
        double arm = 0.5 * (lo + hi);
        double ownGap = InnerGap(arm);

        // 2. k(gap) from the frequency split of a pair of hairpins.
        var all = new List<(double Gap, double K)>();
        for (int i = 0; i < points; i++)
        {
            double gap = gapMin * Math.Pow(gapMax / gapMin, i / (double)(points - 1));
            var g2 = Geometry(s, 2, w, ownGap, arm, new[] { gap }, Observe(arm), openEndExt, feedZ0, tipGap, alternate);
            var roots = new HairpinCircuitModel(g2).NaturalFrequencies(0.6 * f0Hz, 1.4 * f0Hz, 4000);
            if (roots.Length < 2) continue;
            double f1 = roots[0], f2 = roots[1];
            all.Add((gap, (f2 * f2 - f1 * f1) / (f2 * f2 + f1 * f1)));
        }
        if (all.Count < 3) throw new InvalidOperationException("Could not extract a coupling curve.");

        // Keep the monotonic branch from the maximum outward (larger gaps, smaller k).
        int peak = 0;
        for (int i = 1; i < all.Count; i++) if (all[i].K > all[peak].K) peak = i;
        var branch = new List<(double, double)> { all[peak] };
        for (int i = peak + 1; i < all.Count; i++)
        {
            if (all[i].K >= branch[^1].Item2 || all[i].K <= 1e-4) break;
            branch.Add(all[i]);
        }
        if (branch.Count < 2) throw new InvalidOperationException("Coupling curve has no usable monotonic branch.");
        return new HairpinCalibration(s, f0Hz, w, ownGap, openEndExt, feedZ0, tipGap, alternate, arm,
            new TabulatedCouplingCurve(branch), all[peak].K, all[peak].Gap);
    }

    /// <summary>External Q of a single tapped hairpin from the group delay of S11 at f0: Qe = w0 tau / 4.</summary>
    public double Qe(double tapDistance)
    {
        var model = new HairpinCircuitModel(Geometry(Substrate, 1, ResonatorWidth, ArmGap, TunedArmLength,
            Array.Empty<double>(), tapDistance, OpenEndExtension, FeedZ0, TipGap));
        double d = F0Hz * 2e-6;
        double p1 = model.InputReflection(F0Hz - d).Phase;   // one-port: the output tap stays open
        double p2 = model.InputReflection(F0Hz + d).Phase;
        double dphi = p2 - p1;
        while (dphi > Math.PI) dphi -= 2 * Math.PI;
        while (dphi < -Math.PI) dphi += 2 * Math.PI;
        double tau = -dphi / (2 * Math.PI * 2 * d);
        return 2 * Math.PI * F0Hz * tau / 4.0;
    }

    /// <summary>Tap distance from the resonator centre (m) that gives the requested external Q.</summary>
    public double TapDistanceForQe(double qe)
    {
        double path = 2 * TunedArmLength + ArmGap + ResonatorWidth;
        double lo = 2.5e-3 * path, hi = 0.48 * path;  // Qe falls as the tap moves out; below ~0.1 mm the measurement is unreliable (Qe > 1e4)
        if (Qe(hi) > qe || Qe(lo) < qe)
            throw new InvalidOperationException($"Qe = {qe:F2} cannot be reached with a tap on this hairpin ({Qe(hi):F2} to {Qe(lo):F2}).");
        for (int i = 0; i < 60; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (Qe(mid) > qe) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }
}
