using System.Numerics;

namespace FiltSyn.Core;

/// <summary>One quarter-wave coupled section of a parallel-coupled-line filter (all lengths in metres).</summary>
public sealed record CoupledSection(double Width, double Gap, double Length, double Z0e, double Z0o)
{
    /// <summary>Centre-to-centre spacing of the two strips.</summary>
    public double Pitch => Width + Gap;
}

/// <summary>
/// Parallel-coupled (edge-coupled) half-wave-resonator bandpass filter: N resonators need N+1 quarter-wave
/// coupled sections. Strip k of the staircase layout is the second half of section k-1 and the first half of section k.
/// </summary>
public sealed record CoupledLineDesign(
    Substrate Substrate,
    double F0Hz,
    double BandwidthHz,
    double FeedZ0,
    double FeedWidth,
    CoupledSection[] Sections,
    IReadOnlyList<string> Warnings)
{
    public int Order => Sections.Length - 1;
    public double TotalLength => Sections.Sum(s => s.Length);

    /// <summary>The same design with every section length multiplied by <paramref name="factor"/>.</summary>
    public CoupledLineDesign WithLengthScale(double factor)
        => this with { Sections = Sections.Select(s => s with { Length = s.Length * factor }).ToArray() };
}

/// <summary>
/// Design equations for parallel-coupled-line bandpass filters (J-inverter prototype, Hong and Lancaster / Pozar):
/// Z0 J01 = sqrt(pi FBW / (2 g0 g1)), Z0 J(j,j+1) = (pi FBW / 2) / sqrt(g_j g_(j+1)), Z0 J(N,N+1) = sqrt(pi FBW / (2 g_N g_(N+1))),
/// Z0e = Z0 (1 + Z0J + (Z0J)^2), Z0o = Z0 (1 - Z0J + (Z0J)^2). Widths and gaps follow from inverting the even/odd microstrip model.
/// </summary>
public static class CoupledLineSynthesizer
{
    /// <summary>Even/odd-mode impedances of the N+1 sections for prototype g at the given fractional bandwidth.</summary>
    public static (double Z0e, double Z0o)[] EvenOddImpedances(double[] g, double fbw, double z0)
    {
        int n = g.Length - 2;
        var res = new (double, double)[n + 1];
        for (int j = 0; j <= n; j++)
        {
            double zj;
            if (j == 0) zj = Math.Sqrt(Math.PI * fbw / (2.0 * g[0] * g[1]));
            else if (j == n) zj = Math.Sqrt(Math.PI * fbw / (2.0 * g[n] * g[n + 1]));
            else zj = (Math.PI * fbw / 2.0) / Math.Sqrt(g[j] * g[j + 1]);
            res[j] = (z0 * (1.0 + zj + zj * zj), z0 * (1.0 - zj + zj * zj));
        }
        return res;
    }

    public static CoupledLineDesign Synthesize(Substrate s, double[] g, double f0Hz, double bandwidthHz,
        double feedZ0 = 50.0, double minFeature = 0.1e-3)
    {
        int n = g.Length - 2;
        if (n < 1) throw new ArgumentException("Need at least one resonator.", nameof(g));
        if (bandwidthHz <= 0 || bandwidthHz >= f0Hz) throw new ArgumentOutOfRangeException(nameof(bandwidthHz));
        double fbw = bandwidthHz / f0Hz;

        var targets = EvenOddImpedances(g, fbw, feedZ0);
        var sections = new CoupledSection[n + 1];
        var warnings = new List<string>();
        for (int j = 0; j <= n; j++)
        {
            var (ze, zo) = targets[j];
            var (w, gap) = SolveWidthGap(s, ze, zo, fbw, j + 1);
            var pr = CoupledMicrostrip.Static(s, w, gap);
            double disp = Microstrip.EpsEffAt(s, w, f0Hz) / Microstrip.Static(s, w).EpsEff;
            double eAvg = 0.5 * (pr.EpsEffEven + pr.EpsEffOdd) * disp;
            double quarter = Microstrip.C0 / (4.0 * f0Hz * Math.Sqrt(eAvg));
            double length = quarter - HairpinSynthesizer.OpenEndExtension(s, w);
            sections[j] = new CoupledSection(w, gap, length, pr.Z0e, pr.Z0o);
            if (gap < minFeature)
                warnings.Add($"Section {j + 1} gap {gap * 1e3:F3} mm is below the minimum feature size {minFeature * 1e3:F2} mm.");
        }
        if (fbw < 0.03)
            warnings.Add($"Fractional bandwidth {fbw:P2} is narrow for a coupled-line filter: the middle gaps reach " +
                         $"{sections.Max(x => x.Gap) * 1e3:F1} mm, coupling is weak and hard to control, and a narrow band on this " +
                         "substrate gives high insertion loss. A hairpin or combline filter fits narrower bands better.");
        if (fbw > 0.30)
            warnings.Add($"Fractional bandwidth {fbw:P0} is wide for a coupled-line filter; the J-inverter model loses accuracy.");
        warnings.Add("Sections are sized from closed-form even/odd data; the open-end extension is included, but step " +
                     "discontinuities between sections, bends and radiation are not. The second passband is not at 2 f0 " +
                     "because the even and odd modes have different velocities. Verify with full-wave EM before building.");

        double wFeed = Microstrip.WidthForZ0(s, feedZ0);
        return new CoupledLineDesign(s, f0Hz, bandwidthHz, feedZ0, wFeed, sections, warnings);
    }

    /// <summary>Strip width and gap that give the requested even and odd impedances.</summary>
    internal static (double W, double Gap) SolveWidthGap(Substrate s, double ze, double zo, double fbw, int section)
    {
        double h = s.H;
        double wMin = 0.03 * h, wMax = 12.0 * h, gMin = 0.02 * h, gMax = 40.0 * h;

        double[] Residual(double lw, double lg)
        {
            var p = CoupledMicrostrip.Static(s, Math.Exp(lw), Math.Exp(lg));
            return new[] { (p.Z0e - ze) / ze, (p.Z0o - zo) / zo };
        }
        double Norm(double[] r) => Math.Sqrt(r[0] * r[0] + r[1] * r[1]);

        // Coarse log-grid for a starting point, then damped Newton in (ln w, ln gap).
        double bw = 0, bg = 0, best = double.MaxValue;
        for (int i = 0; i <= 30; i++)
            for (int k = 0; k <= 40; k++)
            {
                double lw = Math.Log(wMin) + (Math.Log(wMax) - Math.Log(wMin)) * i / 30.0;
                double lg = Math.Log(gMin) + (Math.Log(gMax) - Math.Log(gMin)) * k / 40.0;
                double nrm;
                try { nrm = Norm(Residual(lw, lg)); } catch { continue; }
                if (double.IsNaN(nrm)) continue;
                if (nrm < best) { best = nrm; bw = lw; bg = lg; }
            }

        double x = bw, y = bg;
        double[] r = Residual(x, y);
        for (int it = 0; it < 80 && Norm(r) > 1e-10; it++)
        {
            const double d = 1e-5;
            var rw = Residual(x + d, y); var rg = Residual(x, y + d);
            double a = (rw[0] - r[0]) / d, b = (rg[0] - r[0]) / d, c = (rw[1] - r[1]) / d, e = (rg[1] - r[1]) / d;
            double det = a * e - b * c;
            if (Math.Abs(det) < 1e-18) break;
            double dx = -(e * r[0] - b * r[1]) / det, dy = -(-c * r[0] + a * r[1]) / det;
            double step = Math.Min(1.0, 0.4 / Math.Max(Math.Abs(dx), Math.Abs(dy)));
            double nx = Math.Clamp(x + step * dx, Math.Log(wMin), Math.Log(wMax));
            double ny = Math.Clamp(y + step * dy, Math.Log(gMin), Math.Log(gMax));
            var rn = Residual(nx, ny);
            if (Norm(rn) >= Norm(r) && step < 1e-3) break;
            x = nx; y = ny; r = rn;
        }
        if (Norm(r) > 1e-4)
        {
            double fbwMin = fbw;
            throw new InvalidOperationException(
                $"Section {section} needs Z0e = {ze:F2} ohm and Z0o = {zo:F2} ohm, which no strip width and gap on this substrate can give " +
                $"(closest miss {Norm(r) * 100:F2}%). A fractional bandwidth of {fbwMin:P2} is outside the range of a coupled-line filter here; " +
                "very narrow bands need gaps wider than 40 substrate heights and very wide bands need gaps below 0.02 heights.");
        }
        return (Math.Exp(x), Math.Exp(y));
    }
}

/// <summary>
/// Circuit-level model of a parallel-coupled-line filter: every section is a two-conductor coupled line built from the same
/// even/odd data as the synthesis, strips are joined end to end, each open end carries a shunt stub equal to the end extension,
/// and the 50-ohm ports sit at the outer ends of the first and last strips.
/// </summary>
public sealed class CoupledLineCircuitModel
{
    private readonly CoupledLineDesign _d;
    private readonly MulticonductorLine[] _lines;
    private readonly double[] _eStatic;
    private readonly double[] _dl;
    private readonly bool _lossy;

    public CoupledLineDesign Design => _d;

    public CoupledLineCircuitModel(CoupledLineDesign d, bool lossy = false)
    {
        _d = d; _lossy = lossy;
        _lines = d.Sections.Select(sec => MulticonductorLine.FromMicrostrip(d.Substrate, sec.Width, new[] { sec.Gap })).ToArray();
        _eStatic = d.Sections.Select(sec => Microstrip.Static(d.Substrate, sec.Width).EpsEff).ToArray();
        _dl = d.Sections.Select(sec => HairpinSynthesizer.OpenEndExtension(d.Substrate, sec.Width)).ToArray();
    }

    private (Complex[,] Y, int P1, int P2) Assemble(double fHz)
    {
        int m = _d.Sections.Length;
        // Nodes: aL(j) = bR(j-1) for j > 0, so allocate per section: aL (first only), bL, aR, bR.
        int next = 0;
        var aL = new int[m]; var bL = new int[m]; var aR = new int[m]; var bR = new int[m];
        for (int j = 0; j < m; j++)
        {
            aL[j] = j == 0 ? next++ : bR[j - 1];
            bL[j] = next++; aR[j] = next++; bR[j] = next++;
        }
        var y = new Complex[next, next];

        for (int j = 0; j < m; j++)
        {
            var sec = _d.Sections[j];
            var line = Microstrip.Analyze(_d.Substrate, sec.Width, fHz);
            double rho = line.EpsEff / _eStatic[j];
            double alpha = _lossy ? line.AlphaTotalNpPerM : 0.0;
            var (y11, y12) = _lines[j].Y(sec.Length, fHz, alpha, rho);

            int[] left = { aL[j], bL[j] }, right = { aR[j], bR[j] };
            for (int i = 0; i < 2; i++)
                for (int k = 0; k < 2; k++)
                {
                    y[left[i], left[k]] += y11[i, k];
                    y[right[i], right[k]] += y11[i, k];
                    y[left[i], right[k]] += y12[i, k];
                    y[right[i], left[k]] += y12[i, k];
                }

            // Open ends: aR (strip j's right end) and bL (strip j+1's left end).
            double beta = 2.0 * Math.PI * fHz * Math.Sqrt(line.EpsEff) / Microstrip.C0;
            var th = new Complex(beta * _dl[j], -alpha * _dl[j]);
            var yOpen = Complex.ImaginaryOne / line.Z0 * Complex.Sin(th) / Complex.Cos(th);
            y[aR[j], aR[j]] += yOpen;
            y[bL[j], bL[j]] += yOpen;
        }
        return (y, aL[0], bR[m - 1]);
    }

    public (Complex S11, Complex S21) Evaluate(double fHz)
    {
        var (y, p1, p2) = Assemble(fHz);
        return HairpinCircuitModel.PortS(y, p1, p2, _d.FeedZ0);
    }

    public (double[] S11dB, double[] S21dB) Sweep(double[] freqsHz)
    {
        var s11 = new double[freqsHz.Length];
        var s21 = new double[freqsHz.Length];
        for (int i = 0; i < freqsHz.Length; i++)
        {
            var (a, b) = Evaluate(freqsHz[i]);
            s11[i] = 20.0 * Math.Log10(Math.Max(a.Magnitude, 1e-12));
            s21[i] = 20.0 * Math.Log10(Math.Max(b.Magnitude, 1e-12));
        }
        return (s11, s21);
    }
}

public static class CoupledLineTuner
{
    /// <summary>Scales all section lengths until the -3 dB band is centred on f0 (a common length error shifts the whole band).</summary>
    public static CoupledLineDesign CenterResponse(CoupledLineDesign d, int maxIterations = 8, double toleranceFractionOfBw = 0.002)
    {
        for (int it = 0; it < maxIterations; it++)
        {
            var model = new CoupledLineCircuitModel(d);
            double span = 1.5 * d.BandwidthHz + 0.08 * d.F0Hz;
            int points = 801;
            var f = new double[points];
            for (int i = 0; i < points; i++) f[i] = d.F0Hz - span + 2.0 * span * i / (points - 1);
            var (s11, s21) = model.Sweep(f);
            var m = HairpinCircuitModel.Metrics(f, s21, s11);
            if (double.IsNaN(m.CenterHz)) break;
            if (Math.Abs(m.CenterHz - d.F0Hz) < toleranceFractionOfBw * d.BandwidthHz) break;
            d = d.WithLengthScale(m.CenterHz / d.F0Hz);
        }
        return d;
    }
}

public static class CoupledLineOptimizer
{
    public readonly record struct Result(CoupledLineDesign Design, double InitialError, double FinalError, int Evaluations);

    /// <summary>
    /// Polish the synthesised sections against the ideal response of prototype g, using the full circuit model rather
    /// than the closed-form J-inverter equations the synthesis is built on. The design is mirror-symmetric, so each
    /// symmetric group of sections shares one length, one gap and one width factor (3 x ceil((N+1)/2) unknowns).
    /// The error is the RMS difference in linear |S11| and |S21| over f0 +/- 1.5 BW on the lossless model.
    /// </summary>
    public static Result Optimize(CoupledLineDesign d, double[] g, int maxEvaluations = 900, double minGap = 0.02e-3, double minWidth = 0.05e-3)
    {
        int m = d.Sections.Length;
        int groups = (m + 1) / 2;
        int GroupOf(int i) => Math.Min(i, m - 1 - i);
        var ideal = CouplingMatrix.FromPrototype(g, d.F0Hz, d.BandwidthHz);

        const int points = 121;
        var f = new double[points];
        double lo = d.F0Hz - 1.5 * d.BandwidthHz, hi = d.F0Hz + 1.5 * d.BandwidthHz;
        for (int i = 0; i < points; i++) f[i] = lo + (hi - lo) * i / (points - 1);
        var target = f.Select(fr => ideal.Evaluate(fr)).ToArray();

        var s0 = d.Sections;

        CoupledLineDesign Build(double[] x)
        {
            var secs = new CoupledSection[m];
            for (int i = 0; i < m; i++)
            {
                int k = GroupOf(i);
                double w = s0[i].Width * Math.Exp(x[2 * groups + k]);
                double gap = s0[i].Gap * Math.Exp(x[groups + k]);
                double len = s0[i].Length * Math.Exp(x[k]);
                secs[i] = s0[i] with { Width = w, Gap = gap, Length = len };
            }
            return d with { Sections = secs };
        }

        double Error(double[] x)
        {
            CoupledLineDesign cand;
            try { cand = Build(x); } catch { return 1e3; }
            double pen = 0;
            foreach (var sec in cand.Sections)
            {
                if (sec.Gap < minGap) pen += 1e3;
                if (sec.Width < minWidth) pen += 1e3;
                if (sec.Length <= 0) return 1e3;
            }
            if (pen > 0) return pen;
            double sum = 0;
            try
            {
                var model = new CoupledLineCircuitModel(cand);
                for (int i = 0; i < points; i++)
                {
                    var (s11, s21) = model.Evaluate(f[i]);
                    double a = s21.Magnitude - target[i].S21.Magnitude;
                    double b = s11.Magnitude - target[i].S11.Magnitude;
                    sum += a * a + b * b;
                }
            }
            catch { return 1e3; }
            return Math.Sqrt(sum / points);
        }

        var x0 = new double[3 * groups];
        double initial = Error(x0);
        var r = NelderMead.Minimize(Error, x0, step: 0.01, tolerance: 1e-9, maxEvaluations: maxEvaluations);
        if (r.Value >= initial) return new Result(d, initial, initial, r.Evaluations);

        var best = Build(r.X);
        // Keep the table honest: recompute the even and odd impedances of the new widths and gaps.
        var fixedSecs = best.Sections.Select(sec =>
        {
            var pr = CoupledMicrostrip.Static(d.Substrate, sec.Width, sec.Gap);
            return sec with { Z0e = pr.Z0e, Z0o = pr.Z0o };
        }).ToArray();
        return new Result(best with { Sections = fixedSecs }, initial, r.Value, r.Evaluations);
    }
}

public static class CoupledLineVerification
{
    /// <summary>Sweep the circuit model and the ideal response of prototype g over f0 +/- spanBandwidths x BW.</summary>
    public static HairpinVerification Run(CoupledLineDesign design, double[] g, bool lossy = false,
        double spanBandwidths = 2.0, int points = 801)
    {
        var model = new CoupledLineCircuitModel(design, lossy);
        var ideal = CouplingMatrix.FromPrototype(g, design.F0Hz, design.BandwidthHz);
        double lo = design.F0Hz - spanBandwidths * design.BandwidthHz;
        double hi = design.F0Hz + spanBandwidths * design.BandwidthHz;
        var f = new double[points];
        for (int i = 0; i < points; i++) f[i] = lo + (hi - lo) * i / (points - 1);
        var (c11, c21) = model.Sweep(f);
        var (i11, i21) = ideal.Sweep(f);
        double e21 = 0, e11 = 0;
        for (int i = 0; i < points; i++)
        {
            double d21 = Math.Pow(10, c21[i] / 20) - Math.Pow(10, i21[i] / 20);
            double d11 = Math.Pow(10, c11[i] / 20) - Math.Pow(10, i11[i] / 20);
            e21 += d21 * d21; e11 += d11 * d11;
        }
        return new HairpinVerification(f, c11, c21, i11, i21,
            HairpinCircuitModel.Metrics(f, c21, c11), HairpinCircuitModel.Metrics(f, i21, i11),
            Math.Sqrt(e21 / points), Math.Sqrt(e11 / points));
    }
}

/// <summary>Layout of a parallel-coupled-line filter: a staircase of half-wave strips with 50-ohm feeds to the board edge.</summary>
public static class CoupledLineLayout
{
    public static FilterLayout From(CoupledLineDesign d, double boardMargin = 5e-3)
    {
        int m = d.Sections.Length;                      // N+1 sections, N+2 strips
        var x = new double[m + 1];                      // section k spans x[k] .. x[k+1]
        for (int k = 0; k < m; k++) x[k + 1] = x[k] + d.Sections[k].Length;
        var yc = new double[m + 1];                     // strip centres
        for (int k = 0; k < m; k++) yc[k + 1] = yc[k] + d.Sections[k].Pitch;

        // Strip k: second half of section k-1 (width of that section) then first half of section k.
        var strips = new List<LayoutPolygon>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Extent(double x0, double x1, double y0, double y1)
        {
            minX = Math.Min(minX, x0); maxX = Math.Max(maxX, x1);
            minY = Math.Min(minY, y0); maxY = Math.Max(maxY, y1);
        }
        for (int k = 0; k <= m; k++)
        {
            var segs = new List<(double X0, double X1, double W)>();
            if (k >= 1) segs.Add((x[k - 1], x[k], d.Sections[k - 1].Width));
            if (k <= m - 1) segs.Add((x[k], x[k + 1], d.Sections[k].Width));
            foreach (var sg in segs) Extent(sg.X0, sg.X1, yc[k] - sg.W / 2, yc[k] + sg.W / 2);
            strips.Add(new LayoutPolygon(FilterLayout.CopperLayer, StepPolygon(segs, yc[k])));
        }

        var bounds = new Rect(minX, minY, maxX, maxY);
        // Leave room for the feed width at both ends.
        double fw = d.FeedWidth / 2.0;
        var board = new Rect(minX - boardMargin, Math.Min(minY, yc[0] - fw) - boardMargin,
                             maxX + boardMargin, Math.Max(maxY, yc[m] + fw) + boardMargin);

        // Feeds run to the board edge and overlap the end strips by half the strip width.
        double ov0 = d.Sections[0].Width / 2.0, ov1 = d.Sections[m - 1].Width / 2.0;
        var feedIn = new Rect(board.X0, yc[0] - fw, x[0] + ov0, yc[0] + fw);
        var feedOut = new Rect(x[m] - ov1, yc[m] - fw, board.X1, yc[m] + fw);
        var polys = new List<LayoutPolygon>(strips)
        {
            new(FilterLayout.CopperLayer, FilterLayout.RectPoints(feedIn)),
            new(FilterLayout.CopperLayer, FilterLayout.RectPoints(feedOut)),
            new(FilterLayout.BoardLayer, FilterLayout.RectPoints(board)),
        };
        var ports = new List<(double, double)> { (board.X0, yc[0]), (board.X1, yc[m]) };
        return new FilterLayout(polys, ports, bounds, board);
    }

    /// <summary>Outline of consecutive collinear segments of different widths sharing one centreline.</summary>
    private static (double, double)[] StepPolygon(List<(double X0, double X1, double W)> segs, double yc)
    {
        var pts = new List<(double, double)>();
        foreach (var s in segs) { pts.Add((s.X0, yc - s.W / 2)); pts.Add((s.X1, yc - s.W / 2)); }
        for (int i = segs.Count - 1; i >= 0; i--)
        { pts.Add((segs[i].X1, yc + segs[i].W / 2)); pts.Add((segs[i].X0, yc + segs[i].W / 2)); }
        // Drop collinear duplicates (equal widths) so the outline stays minimal.
        var clean = new List<(double, double)>();
        foreach (var p in pts)
        {
            if (clean.Count > 0 && Math.Abs(clean[^1].Item1 - p.Item1) < 1e-12 && Math.Abs(clean[^1].Item2 - p.Item2) < 1e-12) continue;
            clean.Add(p);
        }
        return clean.ToArray();
    }
}
