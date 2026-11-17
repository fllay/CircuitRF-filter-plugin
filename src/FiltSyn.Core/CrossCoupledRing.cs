namespace FiltSyn.Core;

/// <summary>Options for the four-pole cross-coupled open-loop ring filter.</summary>
public sealed record CrossCoupledOptions
{
    /// <summary>Transmission zeros at +/- OmegaA on the lowpass prototype axis (1 = band edge).</summary>
    public double OmegaA { get; init; } = 2.0;
    public double ReturnLossDb { get; init; } = 20.0;
    public double TipGap { get; init; } = 0.3e-3;
    public double ResonatorZ0 { get; init; } = 50.0;
    public double FeedZ0 { get; init; } = 50.0;
    /// <summary>Smallest gap the board house can etch; coupling gaps are not made smaller.</summary>
    public double MinGap { get; init; } = 0.1e-3;
    public bool Optimize { get; init; } = true;
    public double BoardMargin { get; init; } = 5e-3;
}

/// <summary>
/// A synthesised four-pole cross-coupled open-loop ring filter (Hong and Lancaster section 10.1, Fig. 10.4a):
/// rings 1 and 4 on the top row, 2 and 3 below; 1-2 and 3-4 couple through their arms (mixed), 2-3 through the folds
/// (magnetic) or the open gaps, and 1-4 through the other kind, so the cross coupling has the opposite sign and puts a
/// transmission zero either side of the passband.
/// </summary>
public sealed record CrossCoupledDesign(
    Substrate Substrate, double F0Hz, double BandwidthHz, QuasiEllipticPrototype Prototype, GeneralCouplingMatrix Target,
    double Width, double FeedWidth, double Side, double SideBottom, double TipGap,
    double Gap12, double Gap23, double Gap14, double Tap, bool CrossIsElectric,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Horizontal shift of each bottom ring outward (negative: inward) so the main and cross gaps can differ.</summary>
    public double BottomShift => (Gap23 - Gap14) / 2.0 + (Side - SideBottom);

    public RingSide GapR1 => CrossIsElectric ? RingSide.Right : RingSide.Left;
    public RingSide GapR4 => CrossIsElectric ? RingSide.Left : RingSide.Right;
    public RingSide GapR2 => CrossIsElectric ? RingSide.Left : RingSide.Right;
    public RingSide GapR3 => CrossIsElectric ? RingSide.Right : RingSide.Left;

    /// <summary>Ring placements in coupling order 1, 2, 3, 4 (lower-left corners of the centreline squares).</summary>
    public RingPlacement[] Placements()
    {
        // Symmetric about the vertical line between the columns; top rings of side Side, bottom rings of SideBottom.
        double axis = Side + (Width + Gap14) / 2.0, top = SideBottom + Width + Gap12;
        double x2 = axis - (Width + Gap23) / 2.0 - SideBottom, x3 = axis + (Width + Gap23) / 2.0;
        return new[]
        {
            new RingPlacement(0, top, GapR1, Side), new RingPlacement(x2, 0, GapR2, SideBottom),
            new RingPlacement(x3, 0, GapR3, SideBottom), new RingPlacement(axis + (Width + Gap14) / 2.0, top, GapR4, Side),
        };
    }

    /// <summary>Tap path coordinates on ring 1 and ring 4: the tap distance from the fold centre, towards the top side.</summary>
    public (double In, double Out) TapCoords()
    {
        var p = Placements();
        double At(RingPlacement r)
        {
            var n = new RingNetwork(Substrate, Width, Side, TipGap, new[] { r }, new[] { new RingTap(0, Side) });
            double fc = n.FoldCentreOf(0);
            return fc + (n.PointAt(0, fc + 1e-5).Y > n.PointAt(0, fc - 1e-5).Y ? 1.0 : -1.0) * Tap;
        }
        return (At(p[0]), At(p[3]));
    }

    public RingNetwork Network(bool lossy = false)
    {
        var (ti, to) = TapCoords();
        return new RingNetwork(Substrate, Width, Side, TipGap, Placements(), new[] { new RingTap(0, ti), new RingTap(3, to) }, lossy: lossy);
    }

    /// <summary>The two transmission zeros (Hong eq. 10.2), Hz.</summary>
    public (double Lower, double Upper) Zeros
    {
        get
        {
            double fbw = BandwidthHz / F0Hz, a = Prototype.OmegaA * fbw, r = Math.Sqrt(a * a + 4);
            return (F0Hz * (-a + r) / 2, F0Hz * (a + r) / 2);
        }
    }
}

public static class CrossCoupledRingSynthesizer
{
    public static CrossCoupledDesign Synthesize(Substrate s, double f0Hz, double bandwidthHz, CrossCoupledOptions? options = null,
        Action<string>? progress = null)
    {
        options ??= new CrossCoupledOptions();
        if (s.IsStripline) throw new InvalidOperationException("Cross-coupled open-loop rings are designed for microstrip only.");
        if (bandwidthHz <= 0 || bandwidthHz >= 0.3 * f0Hz) throw new InvalidOperationException("The cross-coupled ring filter is a narrowband design: keep the bandwidth under 30 % of the centre frequency.");
        var warnings = new List<string>();
        var proto = QuasiEllipticPrototype.Synthesize(options.OmegaA, options.ReturnLossDb);
        var cm = proto.ToBandpass(f0Hz, bandwidthHz);
        double k12 = cm.M[0, 1], k23 = Math.Abs(cm.M[1, 2]), k14 = Math.Abs(cm.M[0, 3]), qe = cm.QeIn;
        double w = Microstrip.WidthForZ0(s, options.ResonatorZ0), wFeed = Microstrip.WidthForZ0(s, options.FeedZ0);
        double g = options.TipGap;

        // 1. Side length that tunes one ring to f0.
        progress?.Invoke("Tuning the ring size");
        double side = TuneSide(s, w, g, f0Hz);
        double pitch = side + w;

        // 2. Coupling curves k(s) on the strong (monotonic) branch from the smallest gap outwards.
        progress?.Invoke("Extracting electric, magnetic and mixed coupling curves");
        double gMin = Math.Max(options.MinGap, 0.03e-3), gMax = Math.Max(6e-3, 8 * s.H);
        var gaps = Enumerable.Range(0, 22).Select(i => gMin * Math.Pow(gMax / gMin, i / 21.0)).ToArray();
        double KPair(RingPlacement a, RingPlacement b)
        {
            var net = new RingNetwork(s, w, side, g, new[] { a, b }, new[] { new RingTap(0, side) });
            var f = net.NaturalFrequencies(0.7 * f0Hz, 1.3 * f0Hz, 1500).OrderBy(x => Math.Abs(x - f0Hz)).Take(2).OrderBy(x => x).ToArray();
            return f.Length < 2 ? 0.0 : (f[1] * f[1] - f[0] * f[0]) / (f[1] * f[1] + f[0] * f[0]);
        }
        var electric = Curve(gaps, sp => KPair(new RingPlacement(0, 0, RingSide.Right), new RingPlacement(pitch + sp, 0, RingSide.Left)));
        var magnetic = Curve(gaps, sp => KPair(new RingPlacement(0, 0, RingSide.Left), new RingPlacement(pitch + sp, 0, RingSide.Right)));

        // 3. Main (2-3) and cross (1-4) gaps: cross through the open ends (electric) if the curves allow, else swapped.
        bool crossElectric;
        double s23, s14;
        if (Invert(magnetic, k23) is { } a23 && Invert(electric, k14) is { } a14) { crossElectric = true; s23 = a23; s14 = a14; }
        else if (Invert(electric, k23) is { } b23 && Invert(magnetic, k14) is { } b14) { crossElectric = false; s23 = b23; s14 = b14; }
        else throw new InvalidOperationException(
            $"The couplings needed (main {k23:0.0000}, cross {k14:0.0000}) are outside what these rings reach with gaps of {gMin * 1e3:0.###} to {gMax * 1e3:0.#} mm. " +
            "Try a wider bandwidth, zeros further out, or a thinner substrate.");
        double shift = (s23 - s14) / 2.0;   // the bottom rings' outward offset while both rows have the same side
        if (Math.Abs(shift) > 0.6 * side)
            warnings.Add($"The main and cross gaps differ a lot ({s23 * 1e3:0.##} and {s14 * 1e3:0.##} mm), so the bottom rings are offset by {shift * 1e3:0.##} mm and overlap the top ones over only part of a side.");

        // 4. Arm-to-arm (mixed) coupling between the rows, with the bottom ring offset as it will be.
        RingSide t1 = crossElectric ? RingSide.Right : RingSide.Left, t2 = crossElectric ? RingSide.Left : RingSide.Right;
        var mixed = Curve(gaps, sp => KPair(new RingPlacement(0, pitch + sp, t1), new RingPlacement(-shift, 0, t2)));
        double s12 = Invert(mixed, k12) ?? throw new InvalidOperationException(
            $"The coupling between the rows ({k12:0.0000}) is outside what arm-to-arm coupling reaches here; try a narrower bandwidth or a different substrate.");

        // 5. Tap on ring 1 for the external Q (singly loaded ring, S11 group delay).
        progress?.Invoke("Locating the taps for the external Q");
        var r1 = new RingPlacement(0, 0, t1);
        var probe = new RingNetwork(s, w, side, g, new[] { r1 }, new[] { new RingTap(0, side) });
        double fc = probe.FoldCentre;
        double up = probe.PointAt(0, fc + 1e-5).Y > probe.PointAt(0, fc - 1e-5).Y ? 1.0 : -1.0;
        // Taps may sit on the outer sides only: the fold (cross through the gaps) or the top arm, never on a side that faces a neighbour.
        double tMin = crossElectric ? 0.05 * side : 0.5 * side + w, tMax = crossElectric ? 1.5 * side - w : 1.5 * side + 0.5 * (side - g) - w;
        double Qe(double t) => new RingNetwork(s, w, side, g, new[] { r1 }, new[] { new RingTap(0, fc + up * t) }).ExternalQ(f0Hz, 1500).Qe;
        double tap;
        if (Qe(tMin) < qe) { tap = tMin; warnings.Add($"The external Q needed ({qe:0.#}) is higher than the weakest tap gives ({Qe(tMin):0.#}); the response will be over-coupled."); }
        else if (Qe(tMax) > qe) { tap = tMax; warnings.Add($"The external Q needed ({qe:0.#}) is lower than the strongest tap gives ({Qe(tMax):0.#}); the response will be under-coupled. A wider feed or a coupled-line input would help."); }
        else
        {
            double lo = tMin, hi = tMax;
            for (int i = 0; i < 40; i++) { double m = 0.5 * (lo + hi); if (Qe(m) > qe) lo = m; else hi = m; }
            tap = 0.5 * (lo + hi);
        }

        foreach (var (name, val) in new[] { ("1-2 and 3-4", s12), ("2-3", s23), ("1-4", s14) })
            if (val <= gMin * 1.001) warnings.Add($"The {name} gap is at the {gMin * 1e3:0.###} mm minimum; the coupling there will be weaker than designed.");

        var d = new CrossCoupledDesign(s, f0Hz, bandwidthHz, proto, cm, w, wFeed, side, side, g, s12, s23, s14, tap, crossElectric, warnings);
        if (options.Optimize) d = Optimize(d, gMin, progress);
        return d;
    }

    /// <summary>
    /// Refines side, gaps and tap on the full four-ring circuit model against the ideal coupling-matrix response:
    /// the curves were taken pair by pair, the full filter also has the couplings loading each other.
    /// </summary>
    public static CrossCoupledDesign Optimize(CrossCoupledDesign d, double gMin = 0.1e-3, Action<string>? progress = null)
    {
        progress?.Invoke("Optimising the full filter on the circuit model");
        var f = Grid(d, 121);
        var (i11, i21) = d.Target.Sweep(f);
        // Top and bottom rows tune separately: their neighbours load them differently (asynchronous tuning, Hong 8.2.2).
        double[] x0 = { 0, 0, Math.Log(d.Gap12), Math.Log(d.Gap23), Math.Log(d.Gap14), d.Tap / d.Side };
        CrossCoupledDesign Make(double[] x) => d with
        {
            Side = d.Side * (1 + 0.02 * x[0]), SideBottom = d.SideBottom * (1 + 0.02 * x[1]),
            Gap12 = Math.Max(gMin, Math.Exp(x[2])), Gap23 = Math.Max(gMin, Math.Exp(x[3])),
            Gap14 = Math.Max(gMin, Math.Exp(x[4])), Tap = d.Side * x[5],
        };
        double Err(double[] x)
        {
            try
            {
                var c = Make(x);
                if (c.Tap <= 0.02 * c.Side || c.Tap >= 2.2 * c.Side) return 10;
                var (c11, c21) = c.Network().Sweep(f);
                double e = 0;
                for (int i = 0; i < f.Length; i++)
                {
                    double a = Math.Pow(10, c21[i] / 20) - Math.Pow(10, i21[i] / 20), b = Math.Pow(10, c11[i] / 20) - Math.Pow(10, i11[i] / 20);
                    e += a * a + b * b;
                }
                return Math.Sqrt(e / f.Length);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return 10; }
        }
        double e0 = Err(x0);
        var best = NelderMead.Minimize(Err, x0, 0.05, 1e-10, 700);
        if (best.Value >= e0) return d;
        var o = Make(best.X);
        return o with { Warnings = o.Warnings.Append($"Optimised on the four-ring circuit model against the ideal response: RMS error {e0:0.0000} to {best.Value:0.0000} ({best.Evaluations} evaluations).").ToList() };
    }

    /// <summary>A sweep grid that shows the passband and both transmission zeros.</summary>
    public static double[] Grid(CrossCoupledDesign d, int points)
    {
        double span = (d.Prototype.OmegaA + 1.5) * d.BandwidthHz / 2;
        return Enumerable.Range(0, points).Select(i => d.F0Hz - span + 2 * span * i / (points - 1)).ToArray();
    }

    private static double TuneSide(Substrate s, double w, double g, double f0)
    {
        double lg = Microstrip.GuidedWavelength(s, w, f0);
        double lo = Math.Max(0.5 * lg / 8, g + 2.2 * w), hi = 1.8 * lg / 8;
        double F(double L) => new RingNetwork(s, w, L, g, new[] { new RingPlacement(0, 0, RingSide.Top) }, new[] { new RingTap(0, L) })
            .NaturalFrequencies(0.4 * f0, 2.6 * f0, 2000).DefaultIfEmpty(0).First();
        if (lo >= hi || F(lo) < f0 || F(hi) > f0)
            throw new InvalidOperationException($"A square ring does not fit at {f0 / 1e9:0.###} GHz with {w * 1e3:0.##} mm lines on this substrate; use a narrower resonator line or a thinner substrate.");
        for (int i = 0; i < 50; i++) { double m = 0.5 * (lo + hi); if (F(m) > f0) lo = m; else hi = m; }
        return 0.5 * (lo + hi);
    }

    /// <summary>k against gap, kept from the smallest gap until k stops falling (the electric curve passes through a null).</summary>
    private static List<(double Gap, double K)> Curve(double[] gaps, Func<double, double> k)
    {
        var list = new List<(double, double)>();
        foreach (var sp in gaps)
        {
            double v = k(sp);
            if (list.Count > 0 && v >= list[^1].Item2) break;
            list.Add((sp, v));
            if (v < 1e-4) break;
        }
        return list;
    }

    private static double? Invert(List<(double Gap, double K)> c, double k)
    {
        if (c.Count < 2 || k > c[0].K || k < c[^1].K) return null;
        for (int i = 0; i + 1 < c.Count; i++)
            if (k <= c[i].K && k >= c[i + 1].K)
            {
                double t = (Math.Log(k) - Math.Log(c[i].K)) / (Math.Log(c[i + 1].K) - Math.Log(c[i].K));
                return Math.Exp(Math.Log(c[i].Gap) + t * (Math.Log(c[i + 1].Gap) - Math.Log(c[i].Gap)));
            }
        return null;
    }
}

/// <summary>Layout of a cross-coupled ring filter: four C-shaped rings and two feeds out to the board edges.</summary>
public static class CrossCoupledLayout
{
    public static FilterLayout From(CrossCoupledDesign d, double boardMargin = 5e-3)
    {
        var polys = new List<LayoutPolygon>();
        var place = d.Placements();
        double w = d.Width, g = d.TipGap;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in place)
        {
            double L = p.Side, x0 = p.X0, y0 = p.Y0, cx = x0 + L / 2, cy = y0 + L / 2;
            double xo0 = x0 - w / 2, xo1 = x0 + L + w / 2, yo0 = y0 - w / 2, yo1 = y0 + L + w / 2;
            double xi0 = x0 + w / 2, xi1 = x0 + L - w / 2, yi0 = y0 + w / 2, yi1 = y0 + L - w / 2;
            // Drawn with the gap on top, then turned about the ring's centre.
            var pts = new[]
            {
                (xo0, yo0), (xo1, yo0), (xo1, yo1), (cx + g / 2, yo1), (cx + g / 2, yi1), (xi1, yi1), (xi1, yi0),
                (xi0, yi0), (xi0, yi1), (cx - g / 2, yi1), (cx - g / 2, yo1), (xo0, yo1),
            };
            int quarter = p.Gap switch { RingSide.Top => 0, RingSide.Left => 1, RingSide.Bottom => 2, _ => 3 };
            (double, double) Turn((double X, double Y) q)
            {
                double dx = q.X - cx, dy = q.Y - cy;
                for (int k = 0; k < quarter; k++) (dx, dy) = (-dy, dx);
                return (cx + dx, cy + dy);
            }
            polys.Add(new LayoutPolygon(FilterLayout.CopperLayer, pts.Select(Turn).ToArray()));
            minX = Math.Min(minX, xo0); minY = Math.Min(minY, yo0); maxX = Math.Max(maxX, xo1); maxY = Math.Max(maxY, yo1);
        }
        var bounds = new Rect(minX, minY, maxX, maxY);
        var board = new Rect(minX - boardMargin, minY - boardMargin, maxX + boardMargin, maxY + boardMargin);

        var net = d.Network();
        var (ti, to) = d.TapCoords();
        var ports = new List<(double, double)>();
        double hf = d.FeedWidth / 2;
        foreach (var (ring, coord) in new[] { (0, ti), (3, to) })
        {
            var (x, y, on) = net.PointAt(ring, coord);
            Rect f;
            switch (on)
            {
                case RingSide.Left: f = new Rect(board.X0, y - hf, x, y + hf); ports.Add((board.X0, y)); break;
                case RingSide.Right: f = new Rect(x, y - hf, board.X1, y + hf); ports.Add((board.X1, y)); break;
                case RingSide.Top: f = new Rect(x - hf, y, x + hf, board.Y1); ports.Add((x, board.Y1)); break;
                default: f = new Rect(x - hf, board.Y0, x + hf, y); ports.Add((x, board.Y0)); break;
            }
            polys.Add(new LayoutPolygon(FilterLayout.CopperLayer, FilterLayout.RectPoints(f)));
        }
        polys.Add(new LayoutPolygon(FilterLayout.BoardLayer, FilterLayout.RectPoints(board)));
        return new FilterLayout(polys, ports, bounds, board);
    }
}
