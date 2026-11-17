using System.Numerics;

namespace FiltSyn.Core;

/// <summary>Sides of a square ring, in counter-clockwise order from the bottom.</summary>
public enum RingSide { Bottom = 0, Right = 1, Top = 2, Left = 3 }

/// <summary>
/// A square open-loop ring: the lower-left corner of its centreline square, the side its open gap is on and, optionally,
/// its own centreline side length (NaN: the network's).
/// </summary>
public sealed record RingPlacement(double X0, double Y0, RingSide Gap, double Side = double.NaN);

/// <summary>A tap on a ring, at a distance along the ring's centreline path measured from its first tip.</summary>
public sealed record RingTap(int Ring, double PathCoord);

/// <summary>
/// Circuit model of any arrangement of identical square open-loop rings (strip width W, centreline side L, tip gap g)
/// in microstrip. Each ring is a path of straight segments (two tails either side of the gap, three full sides).
/// Wherever two rings face each other across a gap, the overlapping parts of the facing sides are modelled as a
/// coupled-line section (even/odd multiconductor line), which gives the electric (open ends facing), magnetic (folds
/// facing) and mixed (arms facing) couplings of Hong and Lancaster section 8.2 / 10.1 without any fitted constant.
/// The tips are joined by the quasi-static gap pi network; corners carry the EM-fitted shortening, half on each side.
/// Taps are ideal connections at the strip centreline (the T-junction is not modelled).
/// </summary>
public sealed class RingNetwork
{
    public Substrate Substrate { get; }
    public double Width { get; }
    public double Side { get; }
    public double TipGap { get; }
    public IReadOnlyList<RingPlacement> Rings { get; }
    public IReadOnlyList<RingTap> Taps { get; }
    public double CornerShortening { get; }
    public double SideOf(int r) => double.IsNaN(Rings[r].Side) ? Side : Rings[r].Side;
    public double TailLength => (Side - TipGap) / 2.0;
    public double PathLength => 4.0 * Side - TipGap;
    public double PathLengthOf(int r) => 4.0 * SideOf(r) - TipGap;
    /// <summary>Path coordinate of the middle of the fold (the side opposite the gap) of ring 0.</summary>
    public double FoldCentre => FoldCentreOf(0);
    public double FoldCentreOf(int r) => (SideOf(r) - TipGap) / 2.0 + 1.5 * SideOf(r);

    private readonly bool _lossy;
    private readonly double _eStatic;
    private readonly List<Seg> _segs = new();
    private readonly List<(int A, int B, double T0, double T1)> _pieceList = new();
    private readonly int _nodeCount;
    private readonly List<(int Seg, double Ta, double Tb, int Na, int Nb)> _single = new();
    private readonly List<(int[] Lo, int[] Hi, double Len, double Gap)> _coupled = new();
    private readonly List<(int TipA, int TipB)> _tips = new();
    private readonly int[] _tapNodes;
    private readonly int _probe;
    private readonly Dictionary<long, MulticonductorLine> _pairs = new();

    private sealed class Seg
    {
        public int Ring;
        public double X0, Y0, X1, Y1, Length, PathStart, DeadStart, DeadEnd;
        public bool Horizontal => Math.Abs(Y1 - Y0) < 1e-12;
        public double U0 => Horizontal ? X0 : Y0;        // axis coordinate of the start
        public double Dir => Horizontal ? Math.Sign(X1 - X0) : Math.Sign(Y1 - Y0);
        public double Line => Horizontal ? Y0 : X0;       // the fixed coordinate
        public double ULo => Math.Min(U0, U0 + Dir * Length);
        public double UHi => Math.Max(U0, U0 + Dir * Length);
        public double TAt(double u) => (u - U0) * Dir;
        public SortedSet<double> Splits = new();
        public List<int> Nodes = new();                   // one per split, in t order
        public int NodeAt(double t) => Nodes[Splits.ToList().FindIndex(x => Math.Abs(x - t) < 1e-12)];
        public double Electrical(double t) => Math.Max(0.0, Math.Min(t, Length - DeadEnd) - DeadStart);
    }

    public RingNetwork(Substrate s, double w, double side, double tipGap, IReadOnlyList<RingPlacement> rings,
        IReadOnlyList<RingTap> taps, double cornerShortening = double.NaN, bool lossy = false)
    {
        if (s.IsStripline) throw new InvalidOperationException("The ring network model is microstrip only.");
        Substrate = s; Width = w; Side = side; TipGap = tipGap; Rings = rings; Taps = taps; _lossy = lossy;
        CornerShortening = double.IsNaN(cornerShortening) ? HairpinGeometry.DefaultCornerShortening(s, w, openLoop: true) : cornerShortening;
        _eStatic = Microstrip.Static(s, w).EpsEff;
        for (int r = 0; r < rings.Count; r++)
            if (tipGap <= 0 || tipGap >= SideOf(r) - 2 * w) throw new ArgumentOutOfRangeException(nameof(tipGap), "The tip gap must be positive and leave room for the tails.");

        // Segments, ring by ring, in path order from the first tip.
        var ringSegs = new List<int[]>();
        for (int r = 0; r < rings.Count; r++) ringSegs.Add(BuildRing(r, rings[r]));

        // Splits: segment ends and taps.
        foreach (var sg in _segs) { sg.Splits.Add(0.0); sg.Splits.Add(sg.Length); }
        var tapPos = new List<(int Seg, double T)>();
        foreach (var tap in taps)
        {
            var (si, t) = Locate(ringSegs[tap.Ring], tap.PathCoord, PathLengthOf(tap.Ring));
            _segs[si].Splits.Add(t);
            tapPos.Add((si, t));
        }

        // Facing sides of every pair of rings: overlapping intervals become coupled sections.
        var couplings = new List<(int A, int B, double U0, double U1, double Gap)>();
        for (int i = 0; i < rings.Count; i++)
            for (int j = i + 1; j < rings.Count; j++)
                foreach (var c in Facing(ringSegs[i], ringSegs[j])) couplings.Add(c);
        foreach (var (a, b, u0, u1, _) in couplings)
        {
            _segs[a].Splits.Add(_segs[a].TAt(u0)); _segs[a].Splits.Add(_segs[a].TAt(u1));
            _segs[b].Splits.Add(_segs[b].TAt(u0)); _segs[b].Splits.Add(_segs[b].TAt(u1));
        }
        // Mirror every split inside a coupled interval onto the partner, so the pieces line up.
        for (int pass = 0; pass < 2; pass++)
            foreach (var (a, b, u0, u1, _) in couplings)
            {
                var sa = _segs[a]; var sb = _segs[b];
                foreach (var t in sa.Splits.ToList()) { double u = sa.U0 + sa.Dir * t; if (u > u0 + 1e-12 && u < u1 - 1e-12) sb.Splits.Add(sb.TAt(u)); }
                foreach (var t in sb.Splits.ToList()) { double u = sb.U0 + sb.Dir * t; if (u > u0 + 1e-12 && u < u1 - 1e-12) sa.Splits.Add(sa.TAt(u)); }
            }
        foreach (var sg in _segs) Dedupe(sg.Splits);

        // Nodes, then joins at the corners (end of one segment = start of the next).
        int next = 0;
        foreach (var sg in _segs) foreach (var _ in sg.Splits) sg.Nodes.Add(next++);
        var uf = new UnionFind(next);
        foreach (var segIdx in ringSegs)
            for (int k = 0; k + 1 < segIdx.Length; k++) uf.Union(_segs[segIdx[k]].Nodes[^1], _segs[segIdx[k + 1]].Nodes[0]);

        // Pieces: coupled where inside a coupling interval, otherwise single lines.
        var coupledPieces = new HashSet<(int Seg, int Idx)>();
        var coupledRaw = new List<(int[] Lo, int[] Hi, double Len, double Gap)>();
        foreach (var (a, b, u0, u1, gap) in couplings)
        {
            var sa = _segs[a]; var sb = _segs[b];
            var us = sa.Splits.Select(t => sa.U0 + sa.Dir * t).Where(u => u >= u0 - 1e-12 && u <= u1 + 1e-12).OrderBy(u => u).ToList();
            for (int k = 0; k + 1 < us.Count; k++)
            {
                double ua = us[k], ub = us[k + 1];
                double ta0 = sa.TAt(ua), ta1 = sa.TAt(ub), tb0 = sb.TAt(ua), tb1 = sb.TAt(ub);
                int ia0 = IndexOf(sa, ta0), ia1 = IndexOf(sa, ta1), ib0 = IndexOf(sb, tb0), ib1 = IndexOf(sb, tb1);
                coupledPieces.Add((a, Math.Min(ia0, ia1))); coupledPieces.Add((b, Math.Min(ib0, ib1)));
                double la = Math.Abs(sa.Electrical(ta1) - sa.Electrical(ta0)), lb = Math.Abs(sb.Electrical(tb1) - sb.Electrical(tb0));
                coupledRaw.Add((new[] { sa.Nodes[ia0], sb.Nodes[ib0] }, new[] { sa.Nodes[ia1], sb.Nodes[ib1] }, 0.5 * (la + lb), gap));
            }
        }
        var singleRaw = new List<(int Na, int Nb, double Len)>();
        for (int si = 0; si < _segs.Count; si++)
        {
            var sg = _segs[si]; var ts = sg.Splits.ToList();
            for (int k = 0; k + 1 < ts.Count; k++)
            {
                if (coupledPieces.Contains((si, k))) continue;
                singleRaw.Add((sg.Nodes[k], sg.Nodes[k + 1], sg.Electrical(ts[k + 1]) - sg.Electrical(ts[k])));
            }
        }
        const double Tiny = 2e-6;
        foreach (var p in singleRaw) if (p.Len < Tiny) uf.Union(p.Na, p.Nb);
        foreach (var c in coupledRaw) if (c.Len < Tiny) { uf.Union(c.Lo[0], c.Hi[0]); uf.Union(c.Lo[1], c.Hi[1]); }

        // Compress node numbers.
        var map = new Dictionary<int, int>();
        int Id(int n) { int r = uf.Find(n); if (!map.TryGetValue(r, out var id)) { id = map.Count; map[r] = id; } return id; }
        foreach (var p in singleRaw) if (p.Len >= Tiny) _single.Add((-1, 0, p.Len, Id(p.Na), Id(p.Nb)));
        foreach (var c in coupledRaw) if (c.Len >= Tiny) _coupled.Add((new[] { Id(c.Lo[0]), Id(c.Lo[1]) }, new[] { Id(c.Hi[0]), Id(c.Hi[1]) }, c.Len, c.Gap));
        foreach (var segIdx in ringSegs) _tips.Add((Id(_segs[segIdx[0]].Nodes[0]), Id(_segs[segIdx[^1]].Nodes[^1])));
        _tapNodes = tapPos.Select(tp => Id(_segs[tp.Seg].NodeAt(tp.T))).ToArray();
        _probe = _tips[0].TipA;
        for (int n = 0; n < next; n++) Id(n);
        _nodeCount = map.Count;
    }

    private static int IndexOf(Seg s, double t)
    {
        int i = 0;
        foreach (var x in s.Splits) { if (Math.Abs(x - t) < 1e-9) return i; i++; }
        throw new InvalidOperationException("internal: split not found");
    }

    private static void Dedupe(SortedSet<double> set)
    {
        var keep = new List<double>();
        foreach (var x in set) if (keep.Count == 0 || x - keep[^1] > 1e-9) keep.Add(x);
        set.Clear(); foreach (var x in keep) set.Add(x);
    }

    private int[] BuildRing(int r, RingPlacement p)
    {
        double L = double.IsNaN(p.Side) ? Side : p.Side, g = TipGap, c2 = CornerShortening / 2.0;
        var P = new (double X, double Y)[] { (p.X0, p.Y0), (p.X0 + L, p.Y0), (p.X0 + L, p.Y0 + L), (p.X0, p.Y0 + L) };
        int gs = (int)p.Gap;
        var a = P[gs]; var b = P[(gs + 1) % 4];
        double dx = (b.X - a.X) / L, dy = (b.Y - a.Y) / L;
        double mx = (a.X + b.X) / 2, my = (a.Y + b.Y) / 2;
        var list = new List<int>();
        double path = 0;
        void Add((double X, double Y) s0, (double X, double Y) s1, bool cs, bool ce)
        {
            var sg = new Seg
            {
                Ring = r, X0 = s0.X, Y0 = s0.Y, X1 = s1.X, Y1 = s1.Y, Length = Math.Abs(s1.X - s0.X) + Math.Abs(s1.Y - s0.Y),
                PathStart = path, DeadStart = cs ? c2 : 0, DeadEnd = ce ? c2 : 0,
            };
            path += sg.Length;
            _segs.Add(sg); list.Add(_segs.Count - 1);
        }
        Add((mx + dx * g / 2, my + dy * g / 2), b, false, true);
        for (int k = 1; k <= 3; k++) Add(P[(gs + k) % 4], P[(gs + k + 1) % 4], true, true);
        Add(a, (mx - dx * g / 2, my - dy * g / 2), true, false);
        return list.ToArray();
    }

    private (int Seg, double T) Locate(int[] ring, double pathCoord, double pathLength)
    {
        if (pathCoord <= 0 || pathCoord >= pathLength) throw new ArgumentOutOfRangeException(nameof(pathCoord), "Tap lies outside the ring.");
        foreach (var si in ring)
        {
            var sg = _segs[si];
            if (pathCoord <= sg.PathStart + sg.Length + 1e-12) return (si, pathCoord - sg.PathStart);
        }
        return (ring[^1], _segs[ring[^1]].Length);
    }

    /// <summary>The (x, y) of a path coordinate on ring r and the side of the ring it lies on.</summary>
    public (double X, double Y, RingSide On) PointAt(int r, double pathCoord)
    {
        double acc = 0;
        var p = Rings[r];
        double L = SideOf(r), g = TipGap;
        var P = new (double X, double Y)[] { (p.X0, p.Y0), (p.X0 + L, p.Y0), (p.X0 + L, p.Y0 + L), (p.X0, p.Y0 + L) };
        int gs = (int)p.Gap;
        var a = P[gs]; var b = P[(gs + 1) % 4];
        double dx = (b.X - a.X) / L, dy = (b.Y - a.Y) / L;
        var pts = new List<((double X, double Y) S, (double X, double Y) E, int Side)>
        {
            (((a.X + b.X) / 2 + dx * g / 2, (a.Y + b.Y) / 2 + dy * g / 2), b, gs),
            (P[(gs + 1) % 4], P[(gs + 2) % 4], (gs + 1) % 4),
            (P[(gs + 2) % 4], P[(gs + 3) % 4], (gs + 2) % 4),
            (P[(gs + 3) % 4], P[gs], (gs + 3) % 4),
            (a, ((a.X + b.X) / 2 - dx * g / 2, (a.Y + b.Y) / 2 - dy * g / 2), gs),
        };
        foreach (var (s0, s1, side) in pts)
        {
            double len = Math.Abs(s1.X - s0.X) + Math.Abs(s1.Y - s0.Y);
            if (pathCoord <= acc + len + 1e-12)
            {
                double f = len > 0 ? (pathCoord - acc) / len : 0;
                return (s0.X + (s1.X - s0.X) * f, s0.Y + (s1.Y - s0.Y) * f, (RingSide)side);
            }
            acc += len;
        }
        throw new ArgumentOutOfRangeException(nameof(pathCoord));
    }

    private IEnumerable<(int A, int B, double U0, double U1, double Gap)> Facing(int[] ra, int[] rb)
    {
        int ia0 = _segs[ra[0]].Ring, ib0 = _segs[rb[0]].Ring;
        var pa = Rings[ia0]; var pb = Rings[ib0];
        double La = SideOf(ia0), Lb = SideOf(ib0);
        bool ySep = pa.Y0 > pb.Y0 + Lb + 1e-12 || pb.Y0 > pa.Y0 + La + 1e-12;
        bool xSep = pa.X0 > pb.X0 + Lb + 1e-12 || pb.X0 > pa.X0 + La + 1e-12;
        if (ySep == xSep) yield break;      // diagonal neighbours (or overlapping rings): no facing sides
        double lineA, lineB;
        if (ySep) { (lineA, lineB) = pa.Y0 > pb.Y0 ? (pa.Y0, pb.Y0 + Lb) : (pa.Y0 + La, pb.Y0); }
        else { (lineA, lineB) = pa.X0 > pb.X0 ? (pa.X0, pb.X0 + Lb) : (pa.X0 + La, pb.X0); }
        double gap = Math.Abs(lineA - lineB) - Width;
        if (gap <= 0) throw new InvalidOperationException("Rings overlap.");
        foreach (var ia in ra)
        {
            var sa = _segs[ia];
            if (sa.Horizontal != ySep || Math.Abs(sa.Line - lineA) > 1e-12) continue;
            foreach (var ib in rb)
            {
                var sb = _segs[ib];
                if (sb.Horizontal != ySep || Math.Abs(sb.Line - lineB) > 1e-12) continue;
                double u0 = Math.Max(sa.ULo, sb.ULo), u1 = Math.Min(sa.UHi, sb.UHi);
                if (u1 - u0 > 1e-9) yield return (ia, ib, u0, u1, gap);
            }
        }
    }

    private MulticonductorLine Pair(double gap)
    {
        long key = (long)Math.Round(gap * 1e10);
        if (!_pairs.TryGetValue(key, out var m)) { m = MulticonductorLine.FromMicrostrip(Substrate, Width, new[] { gap }); _pairs[key] = m; }
        return m;
    }

    private Complex[,] Assemble(double fHz)
    {
        var line = Microstrip.Analyze(Substrate, Width, fHz);
        double rho = line.EpsEff / _eStatic;
        double alpha = _lossy ? line.AlphaTotalNpPerM : 0.0;
        double beta = 2.0 * Math.PI * fHz * Math.Sqrt(line.EpsEff) / Microstrip.C0;
        double y0 = 1.0 / line.Z0;
        var y = new Complex[_nodeCount, _nodeCount];
        foreach (var (_, _, len, a, b) in _single)
        {
            var th = new Complex(beta * len, -alpha * len);
            var sin = Complex.Sin(th);
            var y11 = y0 * (-Complex.ImaginaryOne) * Complex.Cos(th) / sin;
            var y12 = y0 * Complex.ImaginaryOne / sin;
            y[a, a] += y11; y[b, b] += y11; y[a, b] += y12; y[b, a] += y12;
        }
        foreach (var (lo, hi, len, gap) in _coupled)
        {
            var (a11, a12) = Pair(gap).Y(len, fHz, alpha, rho);
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    y[lo[i], lo[j]] += a11[i, j]; y[hi[i], hi[j]] += a11[i, j];
                    y[lo[i], hi[j]] += a12[i, j]; y[hi[i], lo[j]] += a12[i, j];
                }
        }
        var (cp, cg) = MicrostripGap.Capacitances(Substrate, Width, TipGap);
        double om = 2.0 * Math.PI * fHz;
        var jwCp = new Complex(0, om * cp); var jwCg = new Complex(0, om * cg);
        foreach (var (a, b) in _tips)
        {
            y[a, a] += jwCp + jwCg; y[b, b] += jwCp + jwCg; y[a, b] -= jwCg; y[b, a] -= jwCg;
        }
        return y;
    }

    /// <summary>Two-port S-parameters between the first two taps, 50 ohm (or z0) terminations at the taps.</summary>
    public (Complex S11, Complex S21) Evaluate(double fHz, double z0 = 50.0)
    {
        if (_tapNodes.Length < 2) throw new InvalidOperationException("Two taps are needed for a two-port.");
        return HairpinCircuitModel.PortS(Assemble(fHz), _tapNodes[0], _tapNodes[1], z0);
    }

    public (double[] S11dB, double[] S21dB) Sweep(double[] freqsHz)
    {
        var s11 = new double[freqsHz.Length]; var s21 = new double[freqsHz.Length];
        for (int i = 0; i < freqsHz.Length; i++)
        {
            var (a, b) = Evaluate(freqsHz[i]);
            s11[i] = 20 * Math.Log10(Math.Max(a.Magnitude, 1e-12));
            s21[i] = 20 * Math.Log10(Math.Max(b.Magnitude, 1e-12));
        }
        return (s11, s21);
    }

    /// <summary>Reflection at the first tap with every other tap open (a singly loaded resonator).</summary>
    public Complex OnePort(double fHz, double z0 = 50.0)
    {
        var yin = HairpinCircuitModel.Reduce(Assemble(fHz), new[] { _tapNodes[0] })[0, 0];
        return (1.0 - z0 * yin) / (1.0 + z0 * yin);
    }

    /// <summary>
    /// External quality factor of the first tap from the group delay of S11 of the singly loaded ring
    /// (Hong and Lancaster eq. 8.62: tau(w0) = 4 Qe / w0), searched over f0 +/- 25 percent.
    /// </summary>
    public (double Qe, double F) ExternalQ(double f0Hz, int points = 3000)
    {
        double lo = 0.75 * f0Hz, hi = 1.25 * f0Hz;
        double best = 0, fBest = f0Hz, prevPh = 0, prevF = 0;
        for (int i = 0; i <= points; i++)
        {
            double f = lo + (hi - lo) * i / points;
            double ph = OnePort(f).Phase;
            if (i > 0)
            {
                double d = ph - prevPh;
                while (d > Math.PI) d -= 2 * Math.PI;
                while (d < -Math.PI) d += 2 * Math.PI;
                double tau = -d / (2 * Math.PI * (f - prevF));
                if (tau > best) { best = tau; fBest = 0.5 * (f + prevF); }
            }
            prevPh = ph; prevF = f;
        }
        return (2 * Math.PI * fBest * best / 4.0, fBest);
    }

    /// <summary>Natural (open, untapped) resonances in [fLo, fHi], seen from the first tip of ring 0.</summary>
    public double[] NaturalFrequencies(double fLo, double fHi, int steps = 3000)
    {
        double B(double f) => HairpinCircuitModel.Reduce(Assemble(f), new[] { _probe })[0, 0].Imaginary;
        var roots = new List<double>();
        double fPrev = fLo, bPrev = B(fLo);
        for (int i = 1; i <= steps; i++)
        {
            double f = fLo + (fHi - fLo) * i / steps, b = B(f);
            if (bPrev < 0 && b >= 0)
            {
                double l = fPrev, h = f;
                for (int it = 0; it < 50; it++) { double m = 0.5 * (l + h); if (B(m) < 0) l = m; else h = m; }
                roots.Add(0.5 * (l + h));
            }
            fPrev = f; bPrev = b;
        }
        return roots.ToArray();
    }

    private sealed class UnionFind
    {
        private readonly int[] _p;
        public UnionFind(int n) { _p = Enumerable.Range(0, n).ToArray(); }
        public int Find(int x) { while (_p[x] != x) { _p[x] = _p[_p[x]]; x = _p[x]; } return x; }
        public void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) _p[b] = a; }
    }
}
