using System.Numerics;

namespace FiltSyn.Core;

/// <summary>Dimensions that define a hairpin filter at circuit level.</summary>
public sealed record HairpinGeometry(
    Substrate Substrate,
    int Order,
    double ResonatorWidth,
    double ArmGap,
    double ArmLength,          // base centreline to open tip
    double[] Gaps,             // edge-to-edge between neighbouring hairpins
    double TapDistance,        // from resonator centre along the path
    double OpenEndExtension,
    double FeedZ0,
    double TipGap = 0.0,       // > 0: open-loop ring - each arm ends in a tail turned toward the other, the tips face across this gap
    bool Alternate = false)    // every second resonator is turned upside down (open end at the bottom), which couples more strongly
{
    public double BaseLength => ArmGap + ResonatorWidth;

    /// <summary>Length of each tail of an open-loop resonator (arm centreline to the tip); zero for a plain hairpin.</summary>
    public double TailLength => TipGap > 0 ? Math.Max(0.0, (BaseLength - TipGap) / 2.0) : 0.0;

    public static HairpinGeometry FromDesign(HairpinDesign d, double feedZ0 = 50.0)
        => new(d.Substrate, d.Resonators.Length, d.ResonatorWidth, d.ArmGap, d.ArmLength,
               (double[])d.Gaps.Clone(), d.TapDistance, d.OpenEndExtension, feedZ0, d.TipGap, d.Alternate);
}

/// <summary>Key figures of a bandpass response.</summary>
public readonly record struct ResponseMetrics(
    double PeakS21dB,
    double CenterHz,          // geometric mean of the -3 dB points below the peak
    double Bandwidth3dBHz,
    double WorstInBandReturnLossDb);

/// <summary>
/// Circuit-level model of a hairpin filter. All arms form one multiconductor coupled line (adjacent arms
/// of the same hairpin and of neighbouring hairpins couple through nearest-neighbour even/odd data); each
/// fold is a single line, each open end a shunt stub of the end extension, and the 50-ohm ports sit at the tap
/// nodes. Bends, tap junctions and non-adjacent coupling are not modelled.
/// </summary>
public sealed class HairpinCircuitModel
{
    private readonly HairpinGeometry _g;
    private readonly MulticonductorLine _arms;
    private readonly double _eStatic;
    private readonly bool _lossy;

    public HairpinGeometry Geometry => _g;

    public HairpinCircuitModel(HairpinGeometry g, bool lossy = false)
    {
        _g = g;
        _lossy = lossy;
        int conductors = 2 * g.Order;
        var gapList = new double[conductors - 1];
        for (int r = 0; r < g.Order; r++)
        {
            gapList[2 * r] = g.ArmGap;
            if (r < g.Order - 1) gapList[2 * r + 1] = g.Gaps[r];
        }
        _arms = MulticonductorLine.FromMicrostrip(g.Substrate, g.ResonatorWidth, gapList);
        _eStatic = Microstrip.Static(g.Substrate, g.ResonatorWidth).EpsEff;
    }

    public HairpinCircuitModel(HairpinDesign d, bool lossy = false) : this(HairpinGeometry.FromDesign(d), lossy) { }

    public (Complex S11, Complex S21) Evaluate(double fHz)
    {
        var (y, pin, pout) = Assemble(fHz);
        return PortS(y, pin, pout, _g.FeedZ0);
    }

    /// <summary>Reflection coefficient at the input tap with the output tap left open (a one-port).</summary>
    public Complex InputReflection(double fHz)
    {
        var (y, pin, _) = Assemble(fHz);
        var yin = Reduce(y, new[] { pin })[0, 0];
        double z0 = _g.FeedZ0;
        return (1.0 - z0 * yin) / (1.0 + z0 * yin);
    }

    /// <summary>
    /// Natural (open-port) resonant frequencies in [fLo, fHi]: zeros of the input susceptance at the input tap,
    /// where it rises through zero (falling jumps are poles). Lossless geometry assumed.
    /// </summary>
    public double[] NaturalFrequencies(double fLo, double fHi, int steps = 4000)
    {
        double B(double f)
        {
            var (y, pin, _) = Assemble(f);
            var yin = Reduce(y, new[] { pin })[0, 0];
            return yin.Imaginary;
        }
        var roots = new List<double>();
        double fPrev = fLo, bPrev = B(fLo);
        for (int i = 1; i <= steps; i++)
        {
            double f = fLo + (fHi - fLo) * i / steps;
            double b = B(f);
            if (bPrev < 0 && b >= 0)
            {
                double lo = fPrev, hi = f;
                for (int it = 0; it < 60; it++)
                {
                    double mid = 0.5 * (lo + hi);
                    if (B(mid) < 0) lo = mid; else hi = mid;
                }
                roots.Add(0.5 * (lo + hi));
            }
            fPrev = f; bPrev = b;
        }
        return roots.ToArray();
    }

    private (Complex[,] Y, int Pin, int Pout) Assemble(double fHz)
    {
        var g = _g;
        int n2 = 2 * g.Order;
        bool onBase = g.TapDistance <= g.BaseLength / 2.0;
        bool Flipped(int ring) => g.Alternate && (ring & 1) == 1;

        var line = Microstrip.Analyze(g.Substrate, g.ResonatorWidth, fHz);
        double rho = line.EpsEff / _eStatic;
        double alpha = _lossy ? line.AlphaTotalNpPerM : 0.0;
        double beta = 2.0 * Math.PI * fHz * Math.Sqrt(line.EpsEff) / Microstrip.C0;
        double y0 = 1.0 / line.Z0;

        // Node numbering. Arm ends at y = 0 are bNodes, at the other end tNodes. An upright resonator has its fold at
        // y = 0 and its open end at the top; a flipped one (Alternate) the reverse.
        int next = 0;
        int[] Alloc(int count) { var a = new int[count]; for (int i = 0; i < count; i++) a[i] = next++; return a; }
        int[] bNodes = Alloc(n2);

        // Where the taps sit along the arms, measured from y = 0: the input on the first (upright) resonator at the tap
        // distance from its fold, the output on the last, which sits that far from its own fold.
        var splits = new List<double>();
        double tapIn = 0, tapOut = 0;
        if (!onBase)
        {
            double l1 = g.TapDistance - g.BaseLength / 2.0;
            if (g.ArmLength - l1 <= 0) throw new InvalidOperationException("Tap lies beyond the open end of the arm.");
            tapIn = l1;
            tapOut = Flipped(g.Order - 1) ? g.ArmLength - l1 : l1;
            splits.Add(Math.Min(tapIn, tapOut));
            if (Math.Abs(tapIn - tapOut) > 1e-12) splits.Add(Math.Max(tapIn, tapOut));
        }
        var chain = new List<int[]> { bNodes };
        foreach (var _ in splits) chain.Add(Alloc(n2));
        int[] tNodes = Alloc(n2);
        chain.Add(tNodes);

        int[] foldNodes = new int[n2], tipNodes = new int[n2];
        for (int k = 0; k < n2; k++)
        {
            bool fl = Flipped(k / 2);
            foldNodes[k] = fl ? tNodes[k] : bNodes[k];
            tipNodes[k] = fl ? bNodes[k] : tNodes[k];
        }
        double tail = g.TailLength;
        int[] eNodes = tail > 0 ? Alloc(n2) : tipNodes;    // open ends: the arm tips, or the tail tips of an open-loop ring
        int pin, pout;
        int extraIn = -1, extraOut = -1;
        if (onBase) { extraIn = next++; extraOut = next++; }

        var y = new Complex[next, next];

        void AddBlock(int[] a, int[] b, Complex[,] y11, Complex[,] y12)
        {
            for (int i = 0; i < a.Length; i++)
                for (int j = 0; j < a.Length; j++)
                {
                    y[a[i], a[j]] += y11[i, j];
                    y[b[i], b[j]] += y11[i, j];
                    y[a[i], b[j]] += y12[i, j];
                    y[b[i], a[j]] += y12[i, j];
                }
        }
        void AddLine(int a, int b, double length)
        {
            var th = new Complex(beta * length, -alpha * length);
            var sin = Complex.Sin(th);
            var y11 = y0 * (-Complex.ImaginaryOne) * Complex.Cos(th) / sin;
            var y12 = y0 * Complex.ImaginaryOne / sin;
            y[a, a] += y11; y[b, b] += y11; y[a, b] += y12; y[b, a] += y12;
        }

        // Coupled arms, in sections between the tap positions.
        var coords = new List<double> { 0.0 };
        coords.AddRange(splits);
        coords.Add(g.ArmLength);
        for (int i = 0; i + 1 < coords.Count; i++)
        {
            var (a11, a12) = _arms.Y(coords[i + 1] - coords[i], fHz, alpha, rho);
            AddBlock(chain[i], chain[i + 1], a11, a12);
        }

        if (tail > 0)
        {
            // Open-loop ring: each arm continues in a tail; the two tail tips of a ring face each other across the gap,
            // modelled as a pi network (series Cg, shunt Cp per end) that includes the end fringing.
            var (cp, cg) = MicrostripGap.Capacitances(g.Substrate, g.ResonatorWidth, g.TipGap);
            double om = 2.0 * Math.PI * fHz;
            var jwCp = new Complex(0, om * cp);
            var jwCg = new Complex(0, om * cg);
            for (int k = 0; k < n2; k++)
            {
                AddLine(tipNodes[k], eNodes[k], tail);
                y[eNodes[k], eNodes[k]] += jwCp;
            }
            for (int r = 0; r < g.Order; r++)
            {
                int a = eNodes[2 * r], b = eNodes[2 * r + 1];
                y[a, a] += jwCg; y[b, b] += jwCg; y[a, b] -= jwCg; y[b, a] -= jwCg;
            }
        }
        else
        {
            // Open-end extension: an open stub of length dl shunting each tip.
            var thExt = new Complex(beta * g.OpenEndExtension, -alpha * g.OpenEndExtension);
            var yOpen = y0 * Complex.ImaginaryOne * Complex.Sin(thExt) / Complex.Cos(thExt);
            for (int k = 0; k < n2; k++) y[eNodes[k], eNodes[k]] += yOpen;
        }

        // Folds.
        for (int r = 0; r < g.Order; r++)
        {
            int left = foldNodes[2 * r], right = foldNodes[2 * r + 1];
            if (onBase && g.Order == 1)
            {
                // Both taps on the same fold: left end -> input tap -> output tap -> right end.
                double d = g.BaseLength / 2.0 - g.TapDistance;
                AddLine(left, extraIn, d);
                AddLine(extraIn, extraOut, 2.0 * g.TapDistance);
                AddLine(extraOut, right, d);
            }
            else if (onBase && r == 0)
            {
                double d1 = g.BaseLength / 2.0 - g.TapDistance;      // left end to tap
                AddLine(left, extraIn, d1);
                AddLine(extraIn, right, g.BaseLength - d1);
            }
            else if (onBase && r == g.Order - 1)
            {
                double d2 = g.BaseLength / 2.0 - g.TapDistance;      // tap to right end
                AddLine(left, extraOut, g.BaseLength - d2);
                AddLine(extraOut, right, d2);
            }
            else AddLine(left, right, g.BaseLength);
        }

        if (onBase) { pin = extraIn; pout = extraOut; }
        else
        {
            int At(double coord) => splits.FindIndex(c => Math.Abs(c - coord) < 1e-12) + 1;
            pin = chain[At(tapIn)][0];
            pout = chain[At(tapOut)][n2 - 1];
        }

        return (y, pin, pout);
    }

    /// <summary>Schur-complement reduction of an admittance matrix onto the given port nodes.</summary>
    internal static Complex[,] Reduce(Complex[,] y, int[] ports)
    {
        int n = y.GetLength(0), p = ports.Length;
        var internalIdx = Enumerable.Range(0, n).Where(i => !ports.Contains(i)).ToArray();
        int m = internalIdx.Length;
        var yr = new Complex[p, p];
        for (int a = 0; a < p; a++) for (int b = 0; b < p; b++) yr[a, b] = y[ports[a], ports[b]];
        if (m == 0) return yr;

        var yii = new Complex[m, m];
        var yip = new Complex[m, p];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < m; j++) yii[i, j] = y[internalIdx[i], internalIdx[j]];
            for (int b = 0; b < p; b++) yip[i, b] = y[internalIdx[i], ports[b]];
        }
        var x = Linalg.Solve(yii, yip);
        for (int a = 0; a < p; a++)
            for (int b = 0; b < p; b++)
            {
                Complex acc = Complex.Zero;
                for (int i = 0; i < m; i++) acc += y[ports[a], internalIdx[i]] * x[i, b];
                yr[a, b] -= acc;
            }
        return yr;
    }

    internal static (Complex S11, Complex S21) PortS(Complex[,] y, int p1, int p2, double z0)
    {
        var y2 = Reduce(y, new[] { p1, p2 });
        var d = (1 + z0 * y2[0, 0]) * (1 + z0 * y2[1, 1]) - z0 * z0 * y2[0, 1] * y2[1, 0];
        var s11 = ((1 - z0 * y2[0, 0]) * (1 + z0 * y2[1, 1]) + z0 * z0 * y2[0, 1] * y2[1, 0]) / d;
        var s21 = -2 * z0 * y2[1, 0] / d;
        return (s11, s21);
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

    /// <summary>Peak, -3 dB centre and bandwidth from a dense sweep; return loss is the worst S11 inside the -3 dB band.</summary>
    public static ResponseMetrics Metrics(double[] freqsHz, double[] s21dB, double[] s11dB)
    {
        int peak = 0;
        for (int i = 1; i < s21dB.Length; i++) if (s21dB[i] > s21dB[peak]) peak = i;
        double level = s21dB[peak] - 3.0103;

        double Cross(int from, int step)
        {
            int i = from;
            while (i + step >= 0 && i + step < s21dB.Length && s21dB[i + step] >= level) i += step;
            if (i + step < 0 || i + step >= s21dB.Length) return double.NaN;
            double f0 = freqsHz[i], f1 = freqsHz[i + step], v0 = s21dB[i], v1 = s21dB[i + step];
            return f0 + (level - v0) / (v1 - v0) * (f1 - f0);
        }

        double lo = Cross(peak, -1), hi = Cross(peak, +1);
        // Worst (smallest) return loss over the band.
        double minRl = double.PositiveInfinity;
        if (!double.IsNaN(lo) && !double.IsNaN(hi))
            for (int i = 0; i < freqsHz.Length; i++)
                if (freqsHz[i] >= lo && freqsHz[i] <= hi) minRl = Math.Min(minRl, -s11dB[i]);
        return new ResponseMetrics(s21dB[peak], Math.Sqrt(lo * hi), hi - lo, minRl);
    }
}
