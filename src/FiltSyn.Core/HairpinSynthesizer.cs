namespace FiltSyn.Core;

/// <summary>Axis-aligned rectangle (metres), x0 &lt; x1 and y0 &lt; y1.</summary>
public readonly record struct Rect(double X0, double Y0, double X1, double Y1)
{
    public double Width => X1 - X0;
    public double Height => Y1 - Y0;
    public bool Overlaps(Rect o, double tol = 1e-12)
        => X0 < o.X1 - tol && o.X0 < X1 - tol && Y0 < o.Y1 - tol && o.Y0 < Y1 - tol;
}

public sealed record HairpinOptions
{
    /// <summary>Characteristic impedance of the resonator line (sets its width).</summary>
    public double ResonatorZ0 { get; init; } = 50.0;

    /// <summary>Impedance of the input/output feed lines.</summary>
    public double FeedZ0 { get; init; } = 50.0;

    /// <summary>Inner gap between the two arms of one hairpin (m). Null = 2 x resonator width.</summary>
    public double? ArmGap { get; init; }

    /// <summary>Length of each feed stub drawn outside the filter (m). Null = 5 x feed width.</summary>
    public double? FeedLength { get; init; }

    /// <summary>Electrical shortening per 90-degree fold corner, in units of strip width (calibrate with EM).</summary>
    public double BendShorteningPerCorner { get; init; } = 0.0;

    /// <summary>Inter-resonator coupling curve. Null = analytic estimate with scale 1 (uncalibrated).</summary>
    public ICouplingCurve? Coupling { get; init; }

    /// <summary>Multiplier applied to the analytic Qe (calibration hook). 1 = uncalibrated.</summary>
    public double QeScale { get; init; } = 1.0;

    /// <summary>Smallest gap the fabrication process allows (m); smaller results add a warning.</summary>
    public double MinFeature { get; init; } = 0.1e-3;

    /// <summary>
    /// Greater than zero makes the resonators open-loop rings: square loops whose two arm tips turn inward as tails and
    /// face each other across this gap (m). Zero keeps the U-shaped hairpin. Needs the calibrated synthesis.
    /// </summary>
    public double TipGap { get; init; } = 0.0;

    /// <summary>
    /// Turn every second resonator upside down (its open end at the bottom). Neighbouring open ends then do not face each
    /// other: the electric and magnetic couplings add instead of opposing, so much wider bandwidths are reachable.
    /// </summary>
    public bool Alternate { get; init; }
}

public sealed record HairpinResonator(
    int Index,
    double X,                 // left outer edge
    double OuterWidth,        // a + 2w
    double ArmLength,         // base centreline to open tip
    Rect Base, Rect LeftArm, Rect RightArm)
{
    /// <summary>Upside down (open end at the bottom, fold at the top).</summary>
    public bool Flipped { get; init; }

    /// <summary>The two tails of an open-loop ring (left, right); empty for a hairpin.</summary>
    public Rect[] Tails { get; init; } = Array.Empty<Rect>();
}

public sealed record HairpinTap(
    bool OnBase,
    double DistanceFromCentre,   // along the resonator path from its centre (m)
    double ElectricalAngleRad,   // angle from the virtual ground
    double Height,               // tap centreline height above the base bottom edge (arm taps)
    Rect Feed)
{
    /// <summary>The feed enters a flipped resonator from above (base taps only).</summary>
    public bool FromTop { get; init; }
}

public sealed record HairpinDesign(
    Substrate Substrate,
    double F0Hz,
    double BandwidthHz,
    double ResonatorWidth,
    double FeedWidth,
    double ArmGap,
    double FeedLength,
    double PathLength,           // centreline path of one resonator
    double OpenEndExtension,     // per open end
    double GuidedWavelength,
    double[] Gaps,               // edge-to-edge between neighbouring hairpins
    double[] TargetK,
    double QeTarget,
    HairpinResonator[] Resonators,
    HairpinTap Input,
    HairpinTap Output,
    double SecondResonanceHz,
    IReadOnlyList<string> Warnings,
    bool Calibrated)
{
    /// <summary>What the coupling and tap data came from.</summary>
    public string CalibrationSource { get; init; } = "closed-form estimate (uncalibrated)";

    /// <summary>Centreline length of the fold joining the two arms.</summary>
    public double BaseLength => ArmGap + ResonatorWidth;

    /// <summary>Gap between the two tail tips of an open-loop ring (m); zero for a hairpin.</summary>
    public double TipGap { get; init; }

    /// <summary>Length of each tail of an open-loop ring, arm centreline to tip (m); zero for a hairpin.</summary>
    public double TailLength => TipGap > 0 ? Math.Max(0.0, (BaseLength - TipGap) / 2.0) : 0.0;

    public bool IsOpenLoop => TipGap > 0;

    /// <summary>Every second resonator is upside down.</summary>
    public bool Alternate { get; init; }

    /// <summary>Arm length (base centreline to open tip), common to all resonators.</summary>
    public double ArmLength => Resonators[0].ArmLength;

    /// <summary>Tap distance from the resonator centre along the path (m).</summary>
    public double TapDistance => Input.DistanceFromCentre;

    /// <summary>The same design with new arm length, gaps and tap distance; the layout is rebuilt.</summary>
    public HairpinDesign WithGeometry(double armLength, double[] gaps, double tapDistance)
    {
        if (gaps.Length != Gaps.Length) throw new ArgumentException("Wrong number of gaps.", nameof(gaps));
        double path = 2.0 * armLength + BaseLength + 2.0 * TailLength;
        var (res, input, output) = HairpinSynthesizer.BuildLayout(
            ResonatorWidth, FeedWidth, ArmGap, armLength, gaps, tapDistance, path, FeedLength, TipGap, Alternate);
        return this with { PathLength = path, Gaps = (double[])gaps.Clone(), Resonators = res, Input = input, Output = output };
    }

    public double TotalWidth => Resonators[^1].RightArm.X1 - Resonators[0].LeftArm.X0;
    public double Height => Resonators.Max(r => Math.Max(r.LeftArm.Y1, r.RightArm.Y1));

    public IEnumerable<Rect> AllRectangles()
    {
        foreach (var r in Resonators)
        {
            yield return r.Base; yield return r.LeftArm; yield return r.RightArm;
            foreach (var t in r.Tails) yield return t;
        }
        yield return Input.Feed;
        yield return Output.Feed;
    }
}

/// <summary>
/// Turns a lowpass prototype into hairpin-resonator dimensions: resonator width and path length from the
/// microstrip model, gaps from the required coupling coefficients, and tap heights from the external Q.
/// The default coupling and Qe models are closed-form estimates and are flagged uncalibrated.
/// </summary>
public static class HairpinSynthesizer
{
    /// <summary>Qe of a half-wave resonator tapped at electrical angle phi from its virtual ground.</summary>
    public static double QeFromTapAngle(double phi, double resonatorZ0, double feedZ0)
        => Math.PI * feedZ0 / (2.0 * resonatorZ0 * Math.Pow(Math.Sin(phi), 2));

    /// <summary>Inverse of <see cref="QeFromTapAngle"/>; phi in (0, pi/2].</summary>
    public static double TapAngleFromQe(double qe, double resonatorZ0, double feedZ0)
    {
        double s2 = Math.PI * feedZ0 / (2.0 * resonatorZ0 * qe);
        if (s2 > 1.0)
            throw new ArgumentOutOfRangeException(nameof(qe),
                $"Qe = {qe:F3} is below the minimum {Math.PI * feedZ0 / (2.0 * resonatorZ0):F3} reachable with a tap on this line; use a coupled feed.");
        return Math.Asin(Math.Sqrt(s2));
    }

    /// <summary>Hammerstad open-end extension of a microstrip line (m).</summary>
    public static double OpenEndExtension(Substrate s, double w)
    {
        if (s.IsStripline) return Stripline.OpenEndExtension(s, w);
        var (_, e) = Microstrip.Static(s, w);
        double u = w / s.H;
        return s.H * 0.412 * (e + 0.3) / (e - 0.258) * (u + 0.264) / (u + 0.8);
    }

    public static HairpinDesign Synthesize(Substrate s, double[] g, double f0Hz, double bandwidthHz,
        HairpinOptions? options = null)
    {
        options ??= new HairpinOptions();
        if (options.TipGap > 0 || options.Alternate) throw new NotSupportedException("Open-loop rings and alternating orientation need the calibrated synthesis (SynthesizeCalibrated).");
        int n = g.Length - 2;
        if (n < 2) throw new ArgumentException("Hairpin filters need at least two resonators.", nameof(g));
        if (bandwidthHz <= 0 || bandwidthHz >= f0Hz) throw new ArgumentOutOfRangeException(nameof(bandwidthHz));

        var warnings = new List<string>();
        double fbw = bandwidthHz / f0Hz;
        if (fbw > 0.20)
            warnings.Add($"Fractional bandwidth {fbw:P1} is wide; the narrowband coupling model loses accuracy above about 20%.");

        var cm = CouplingMatrix.FromPrototype(g, f0Hz, bandwidthHz);

        // Widths.
        double w = Microstrip.WidthForZ0(s, options.ResonatorZ0);
        double wFeed = Microstrip.WidthForZ0(s, options.FeedZ0);
        double a = options.ArmGap ?? 2.0 * w;
        double feedLen = options.FeedLength ?? 5.0 * wFeed;

        // Resonator length: half a guided wavelength minus open-end extensions.
        double lg = Microstrip.GuidedWavelength(s, w, f0Hz);
        double dl = OpenEndExtension(s, w);
        double bendLoss = 2.0 * options.BendShorteningPerCorner * w; // two fold corners
        double path = lg / 2.0 - 2.0 * dl + bendLoss;
        double baseLen = a + w;                        // centreline length of the fold
        double arm = (path - baseLen) / 2.0;           // each arm, base centreline to tip
        if (arm <= 0)
            throw new InvalidOperationException("Resonator is too short for the requested arm gap; reduce ArmGap or lower the frequency.");

        // Coupling gaps.
        var curve = options.Coupling ?? new AnalyticCouplingCurve(s, w);
        bool calibrated = options.Coupling is TabulatedCouplingCurve;
        if (!calibrated)
            warnings.Add("Coupling gaps and tap position use closed-form estimates and have not been calibrated against EM; verify with a full-wave simulation before building.");

        var gaps = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            try { gaps[i] = curve.GapFor(cm.K[i]); }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new InvalidOperationException(
                    $"Coupling k{i + 1},{i + 2} = {cm.K[i]:F4} cannot be realised on this substrate and width: {ex.Message}", ex);
            }
            if (gaps[i] < options.MinFeature)
                warnings.Add($"Gap {i + 1}-{i + 2} = {gaps[i] * 1e3:F3} mm is below the minimum feature size {options.MinFeature * 1e3:F3} mm.");
        }

        // External coupling: tapped feed on the outer arm of the first and last hairpin.
        double qe = cm.QeInPhysical * options.QeScale;
        double phi = TapAngleFromQe(qe, options.ResonatorZ0, options.FeedZ0);
        double beta = Math.PI / path;                   // path is half a wavelength at f0
        double tapDist = phi / beta;                    // from the resonator centre along the path

        var (res, input, output) = BuildLayout(w, wFeed, a, arm, gaps, tapDist, path, feedLen);

        double f2 = EstimateSecondResonance(s, w, f0Hz);

        return new HairpinDesign(s, f0Hz, bandwidthHz, w, wFeed, a, feedLen, path, dl, lg, gaps, cm.K, qe,
            res, input, output, f2, warnings, calibrated);
    }

    /// <summary>Single-resonator second resonance: f * sqrt(eps_eff(f)) = 2 f0 sqrt(eps_eff(f0)).</summary>
    internal static double EstimateSecondResonance(Substrate s, double w, double f0Hz)
    {
        double target = 2.0 * f0Hz * Math.Sqrt(Microstrip.EpsEffAt(s, w, f0Hz));
        double lo = f0Hz, hi = 2.0 * f0Hz;
        for (int i = 0; i < 80; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (mid * Math.Sqrt(Microstrip.EpsEffAt(s, w, mid)) < target) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>
    /// Synthesis from design curves extracted from the circuit model instead of closed-form estimates: the arm
    /// length that tunes one hairpin to f0, gaps from the extracted k(gap), and the tap from the extracted Qe.
    /// Still not EM-verified: pass a ready <paramref name="calibration"/> to reuse curves between designs.
    /// </summary>
    public static HairpinDesign SynthesizeCalibrated(Substrate s, double[] g, double f0Hz, double bandwidthHz,
        HairpinOptions? options = null, HairpinCalibration? calibration = null)
    {
        options ??= new HairpinOptions();
        int n = g.Length - 2;
        if (n < 2) throw new ArgumentException("Hairpin filters need at least two resonators.", nameof(g));
        if (bandwidthHz <= 0 || bandwidthHz >= f0Hz) throw new ArgumentOutOfRangeException(nameof(bandwidthHz));

        double fbw = bandwidthHz / f0Hz;
        double w = Microstrip.WidthForZ0(s, options.ResonatorZ0);
        double wFeed = Microstrip.WidthForZ0(s, options.FeedZ0);
        double a = options.ArmGap ?? 2.0 * w;
        double feedLen = options.FeedLength ?? 5.0 * wFeed;
        double dl = OpenEndExtension(s, w);
        double tipGap = options.TipGap;
        bool openLoop = tipGap > 0;
        if (openLoop && s.IsStripline) throw new InvalidOperationException("Open-loop rings are modelled on microstrip only (the gap model is a microstrip one).");

        var cal = calibration ?? HairpinCalibration.Run(s, f0Hz, w, a, dl, options.FeedZ0, tipGap: tipGap, square: openLoop, alternate: options.Alternate);
        if (openLoop) a = cal.ArmGap;             // the square loop sets its own inner gap
        var cm = CouplingMatrix.FromPrototype(g, f0Hz, bandwidthHz);

        double kNeeded = cm.K.Max();
        if (kNeeded > cal.MaxCoupling)
            throw new InvalidOperationException(
                $"Required coupling k = {kNeeded:F4} exceeds the {cal.MaxCoupling:F4} these {(openLoop ? "open-loop rings" : "hairpins")} can reach (best at a gap of " +
                $"{cal.GapAtMaxCoupling * 1e3:F2} mm), so a fractional bandwidth of {fbw:P2} is out of range for this prototype. " +
                $"Reduce the bandwidth to about {fbw * cal.MaxCoupling / kNeeded:P2} or less, or change the substrate, resonator impedance or layout.");

        var warnings = new List<string>();
        if (fbw > 0.20)
            warnings.Add($"Fractional bandwidth {fbw:P1} is wide; the narrowband coupling model loses accuracy above about 20%.");

        var gaps = new double[n - 1];
        for (int i = 0; i < gaps.Length; i++)
        {
            try { gaps[i] = cal.CouplingCurve.GapFor(cm.K[i]); }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new InvalidOperationException(
                    $"Coupling k{i + 1},{i + 2} = {cm.K[i]:F4} is outside the extracted range: {ex.Message}", ex);
            }
            if (gaps[i] < options.MinFeature)
                warnings.Add($"Gap {i + 1}-{i + 2} = {gaps[i] * 1e3:F3} mm is below the minimum feature size {options.MinFeature * 1e3:F3} mm.");
        }

        double qe = cm.QeInPhysical * options.QeScale;
        double tap = cal.TapDistanceForQe(qe);
        double arm = cal.TunedArmLength;
        double tail = openLoop ? Math.Max(0.0, (a + w - tipGap) / 2.0) : 0.0;
        double path = 2.0 * arm + a + w + 2.0 * tail;
        var (res, input, output) = BuildLayout(w, wFeed, a, arm, gaps, tap, path, feedLen, openLoop ? tipGap : 0.0, options.Alternate);

        warnings.Add("Dimensions are calibrated against the multiconductor circuit model (closed-form coupled-line data, " +
                     "nearest-neighbour coupling). Bends, tap junctions and radiation are not modelled; verify with full-wave EM before building.");
        if (openLoop && tipGap < 0.1e-3 * 0.999)
            warnings.Add($"The open gap {tipGap * 1e3:F3} mm is below 0.1 mm; check it against your fabrication limits.");

        return new HairpinDesign(s, f0Hz, bandwidthHz, w, wFeed, a, feedLen, path, dl,
            Microstrip.GuidedWavelength(s, w, f0Hz), gaps, cm.K, qe, res, input, output,
            EstimateSecondResonance(s, w, f0Hz), warnings, true)
        {
            CalibrationSource = "circuit-model design curves",
            TipGap = openLoop ? tipGap : 0.0,
            Alternate = options.Alternate,
        };
    }

    /// <summary>Places hairpins left to right and the two tapped feeds; all dimensions in metres.</summary>
    internal static (HairpinResonator[] Resonators, HairpinTap Input, HairpinTap Output) BuildLayout(
        double w, double wFeed, double a, double arm, double[] gaps, double tapDist, double path, double feedLen,
        double tipGap = 0.0, bool alternate = false)
    {
        int n = gaps.Length + 1;
        double baseLen = a + w;
        double outer = a + 2.0 * w;
        bool onBase = tapDist <= baseLen / 2.0;
        double phi = tapDist * Math.PI / path;
        // An open-loop ring's arms run up to the top side (centreline at w/2 + arm) and end flush with its tails.
        double top = tipGap > 0 ? arm + w : w / 2.0 + arm;

        var res = new HairpinResonator[n];
        double x = 0;
        for (int i = 0; i < n; i++)
        {
            bool flip = alternate && (i & 1) == 1;
            var left = new Rect(x, 0, x + w, top);
            var right = new Rect(x + w + a, 0, x + outer, top);
            var bas = flip ? new Rect(x, top - w, x + outer, top) : new Rect(x, 0, x + outer, w);
            res[i] = new HairpinResonator(i + 1, x, outer, arm, bas, left, right) { Flipped = flip };
            if (tipGap > 0)
            {
                double t = Math.Max(0.0, (baseLen - tipGap) / 2.0);       // arm centreline to tail tip
                double y0 = flip ? 0.0 : arm;                              // tails sit opposite the fold
                res[i] = res[i] with
                {
                    Tails = new[]
                    {
                        new Rect(x, y0, x + w / 2.0 + t, y0 + w),
                        new Rect(x + outer - w / 2.0 - t, y0, x + outer, y0 + w),
                    },
                };
            }
            if (i < n - 1) x += outer + gaps[i];
        }

        HairpinTap MakeTap(HairpinResonator r, bool leftSide)
        {
            if (onBase)
            {
                // Feed enters at horizontal offset tapDist from the resonator centre, from below (above for a flipped one).
                double cx = r.X + outer / 2.0 + (leftSide ? -tapDist : tapDist);
                if (r.Flipped)
                    return new HairpinTap(true, tapDist, phi, top - w / 2.0,
                        new Rect(cx - wFeed / 2.0, top, cx + wFeed / 2.0, top + feedLen)) { FromTop = true };
                var feed = new Rect(cx - wFeed / 2.0, -feedLen, cx + wFeed / 2.0, 0);
                return new HairpinTap(true, tapDist, phi, w / 2.0, feed);
            }
            double along = w / 2.0 + (tapDist - baseLen / 2.0);          // from the fold's outer edge
            double yc = r.Flipped ? top - along : along;
            var f = leftSide
                ? new Rect(r.X - feedLen, yc - wFeed / 2.0, r.X, yc + wFeed / 2.0)
                : new Rect(r.X + outer, yc - wFeed / 2.0, r.X + outer + feedLen, yc + wFeed / 2.0);
            return new HairpinTap(false, tapDist, phi, yc, f);
        }

        return (res, MakeTap(res[0], true), MakeTap(res[^1], false));
    }
}
