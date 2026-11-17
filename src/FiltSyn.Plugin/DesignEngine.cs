using System.Text.Json.Nodes;
using FiltSyn.Core;

namespace FiltSyn.Plugin;

/// <summary>One synthesised filter and everything the panel and the placed cell need from it.</summary>
internal sealed class FilterResult
{
    public required bool Hairpin { get; init; }
    /// <summary>Square open-loop ring chain (a hairpin variant); Hairpin is also true.</summary>
    public bool OpenLoop { get; init; }
    /// <summary>Four cross-coupled open-loop rings (quasi-elliptic); Hairpin and OpenLoop are also true.</summary>
    public bool CrossCoupled { get; init; }
    public required Substrate Substrate { get; init; }
    public required double F0 { get; init; }
    public required double Bandwidth { get; init; }
    /// <summary>The centre frequency and bandwidth the user specified; F0 and Bandwidth are what was synthesised (after EM calibration).</summary>
    public double SpecF0 { get; init; }
    public double SpecBandwidth { get; init; }
    public required double FeedWidth { get; init; }
    public required FilterLayout Layout { get; init; }
    public required HairpinVerification Verification { get; init; }
    public required string[] Columns { get; init; }
    public required string[] Units { get; init; }
    public required List<object?[]> Rows { get; init; }
    public required List<string> Warnings { get; init; }
    public required TechInfo Tech { get; init; }
    public List<string> Notes { get; init; } = new();
    public object? CoupledDesign { get; init; }
    /// <summary>The circuit model with conductor and dielectric loss on the preview grid (radiation not included).</summary>
    public LossInfo? Loss { get; set; }
}

/// <summary>Loss of the design: the lossy sweep and the resonator Q (Hong and Lancaster eq. 3.62 for the estimate).</summary>
internal sealed record LossInfo(double[] S11dB, double[] S21dB, double Qc, double Qd, double Qu, double EstimateDb, double MidbandDb)
{
    public static LossInfo Make(Substrate s, double w, double f0, double bw, double[] g, double[] freqs, Func<double[], (double[] S11, double[] S21)> lossySweep)
    {
        var line = Microstrip.Analyze(s, w, f0);
        double beta = 2 * Math.PI * f0 * Math.Sqrt(line.EpsEff) / Microstrip.C0;
        double Q(double a) => a > 0 ? beta / (2 * a) : double.PositiveInfinity;
        double qc = Q(line.AlphaCondNpPerM), qd = Q(line.AlphaDielNpPerM), qu = Q(line.AlphaTotalNpPerM);
        double sumG = 0; for (int i = 1; i < g.Length - 1; i++) sumG += g[i];
        double est = 4.343 * sumG / (bw / f0 * qu);
        var (s11, s21) = lossySweep(freqs);
        double mid = double.NegativeInfinity;
        for (int i = 0; i < freqs.Length; i++) if (Math.Abs(freqs[i] - f0) <= 0.25 * bw) mid = Math.Max(mid, s21[i]);
        return new LossInfo(s11, s21, qc, qd, qu, est, -mid);
    }
}

internal sealed class DesignEngine
{
    private HairpinCalibration? _cal;
    private double _calZ0 = 50.0;
    private (Substrate S, double F0, double Tip, bool Alt)? _calKey;
    private (string Key, CrossCoupledDesign D)? _ccCache;

    public static double Num(JsonObject p, string name, double def)
    {
        var n = p[name];
        if (n is JsonObject o && o["value"] is JsonValue ov && ov.TryGetValue<double>(out var d)) return d;
        if (n is JsonValue v && v.TryGetValue<double>(out var x)) return x;
        return def;
    }

    public static bool Bool(JsonObject p, string name, bool def)
        => p[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;

    public static string Str(JsonObject p, string name, string def)
        => p[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : def;

    public FilterResult Run(JsonObject p, Action<string>? progress = null)
    {
        var tech = TechInfo.Read(p["technology"]);
        string filterType = Str(p, "filter", "Coupled-line");
        bool cross = filterType.StartsWith("Cross-coupled", StringComparison.Ordinal);
        bool openLoop = filterType == "Open-loop ring" || cross;
        bool hairpin = filterType == "Hairpin" || openLoop;
        double tipGap = openLoop ? Num(p, "tipGap", 0.3e-3) : 0.0;
        bool alternate = openLoop && Str(p, "orientation", "Alternating") == "Alternating";
        bool stripline = Str(p, "medium", "Microstrip").StartsWith("Stripline", StringComparison.Ordinal);
        bool cheb = Str(p, "response", "Chebyshev") == "Chebyshev";
        int n = (int)Num(p, "order", 4);
        double specF0 = Num(p, "f0", 3.5e9), specBw = Num(p, "bw", 200e6), ripple = Num(p, "ripple", 0.1);
        double f0Corr = Math.Clamp(Num(p, "f0Corr", 1.0), EmCalibration.MinFactor, EmCalibration.MaxFactor);
        double bwCorr = Math.Clamp(Num(p, "bwCorr", 1.0), EmCalibration.MinFactor, EmCalibration.MaxFactor);
        double f0 = specF0 * f0Corr, bw = specBw * bwCorr;
        var warnings = new List<string>();

        var sub = ReadSubstrate(p, tech, stripline, warnings);
        if (n < 2 && hairpin) throw new InvalidOperationException("A hairpin filter needs order 2 or more.");
        if (bw <= 0 || bw >= f0) throw new InvalidOperationException("The bandwidth must be positive and smaller than the centre frequency.");

        double[] g = cheb ? Prototype.Chebyshev(n, ripple) : Prototype.Butterworth(n);
        string? calNote = Math.Abs(f0Corr - 1) > 1e-9 || Math.Abs(bwCorr - 1) > 1e-9
            ? $"EM calibration applied: the filter is designed for {f0 / 1e9:0.####} GHz and {bw / 1e6:0.##} MHz (factors {f0Corr:0.####} and {bwCorr:0.####}) so that its EM response lands on {specF0 / 1e9:0.####} GHz and {specBw / 1e6:0.##} MHz."
            : null;

        if (cross) return CrossCoupled(p, tech, sub, specF0, specBw, f0, bw, n, cheb, ripple, tipGap, calNote, warnings, progress);

        if (hairpin)
        {
            if (openLoop && sub.IsStripline)
                throw new InvalidOperationException("Open-loop rings are designed for microstrip only (the gap model between the ring ends is a microstrip model). Set Medium to Microstrip.");
            if (openLoop && tipGap <= 0)
                throw new InvalidOperationException("The open gap of the rings must be positive.");
            progress?.Invoke(openLoop ? "Calibrating open-loop ring coupling against the circuit model" : "Calibrating hairpin coupling against the circuit model");
            var cal = _calKey is { } k && k.S == sub && k.F0 == f0 && k.Tip == tipGap && k.Alt == alternate ? _cal : null;
            if (cal is null)
            {
                // A 50 ohm line can be as wide as the resonator itself on thick substrates at high frequency (a square
                // ring's side is only about lambda_g / 8): fall back to narrower, higher-impedance resonator lines.
                Exception? last = null;
                foreach (double z in new[] { 50.0, 70.0, 90.0, 110.0 })
                {
                    double w = Microstrip.WidthForZ0(sub, z);
                    try
                    {
                        cal = HairpinCalibration.Run(sub, f0, w, 2.0 * w, HairpinSynthesizer.OpenEndExtension(sub, w),
                            gapMax: Math.Max(6e-3, 8.0 * sub.H), points: sub.H > 0.75e-3 ? 18 : 14,
                            tipGap: tipGap, square: openLoop, alternate: alternate);
                        _calZ0 = z; break;
                    }
                    catch (InvalidOperationException e) { last = e; }
                }
                if (cal is null)
                {
                    double w50 = Microstrip.WidthForZ0(sub, 50.0), lg = Microstrip.GuidedWavelength(sub, w50, f0);
                    throw new InvalidOperationException(
                        $"The {(openLoop ? "open-loop ring" : "hairpin")} does not fit on this substrate at {f0 / 1e9:0.###} GHz: a 50 ohm line is " +
                        $"{w50 * 1e3:0.##} mm wide against a guided wavelength of {lg * 1e3:0.#} mm, and even a 110 ohm resonator line did not tune. " +
                        $"Use a thinner substrate (h well under {lg / 20 * 1e3:0.##} mm) or the coupled-line type. ({last?.Message})");
                }
                _cal = cal; _calKey = (sub, f0, tipGap, alternate);
            }
            if (_calZ0 > 50.0)
                warnings.Add($"A 50 ohm line ({Microstrip.WidthForZ0(sub, 50.0) * 1e3:0.##} mm) is too wide for this resonator on this substrate at this frequency, " +
                             $"so the resonators use {_calZ0:0} ohm lines ({Microstrip.WidthForZ0(sub, _calZ0) * 1e3:0.##} mm); the feeds stay 50 ohm. " +
                             "A thinner substrate gives a more compact, lower-loss filter.");
            var opts = new HairpinOptions { ResonatorZ0 = _calZ0, TipGap = openLoop ? tipGap : 0.0, Alternate = openLoop && alternate };
            var d = HairpinTuner.CenterResponse(HairpinSynthesizer.SynthesizeCalibrated(sub, g, f0, bw, opts, cal));
            var notes = new List<string>();
            if (calNote is not null) notes.Add(calNote);
            if (Bool(p, "optimize", true))
            {
                progress?.Invoke("Optimising on the circuit model");
                var o = HairpinTuner.Optimize(d, g);
                if (o.FinalError < o.InitialError)
                {
                    d = o.Design;
                    notes.Add($"Optimised on the circuit model against the ideal response: RMS error {o.InitialError:0.0000} to {o.FinalError:0.0000} ({o.Evaluations} evaluations).");
                }
            }
            var v = HairpinVerification.Run(d, g);
            var lossH = LossInfo.Make(sub, d.ResonatorWidth, f0, bw, g, v.FrequenciesHz, ff => new HairpinCircuitModel(d, lossy: true).Sweep(ff));
            var layout = HairpinLayout.From(d);
            var rows = new List<object?[]>
            {
                new object?[] { "Resonator width", d.ResonatorWidth },
                new object?[] { "Feed width", d.FeedWidth },
            };
            if (openLoop)
            {
                rows.Add(new object?[] { "Ring side (centreline)", d.BaseLength });
                rows.Add(new object?[] { "Ring inner gap", d.ArmGap });
                rows.Add(new object?[] { "Arm length (fold to tail)", d.ArmLength });
                rows.Add(new object?[] { "Tail length", d.TailLength });
                rows.Add(new object?[] { "Open gap (tail tips)", d.TipGap });
            }
            else
            {
                rows.Add(new object?[] { "Arm gap (inside a hairpin)", d.ArmGap });
                rows.Add(new object?[] { "Arm length", d.ArmLength });
            }
            rows.Add(new object?[] { "Tap distance from centre", d.TapDistance });
            for (int i = 0; i < d.Gaps.Length; i++) rows.Add(new object?[] { $"Gap {i + 1}-{i + 2}", d.Gaps[i] });
            warnings.AddRange(d.Warnings);
            return new FilterResult
            {
                Hairpin = true, OpenLoop = openLoop, Substrate = sub, F0 = f0, Bandwidth = bw, SpecF0 = specF0, SpecBandwidth = specBw, FeedWidth = d.FeedWidth, Layout = layout,
                Verification = v, Loss = lossH, Columns = new[] { "Dimension", "Value" }, Units = new[] { "", "m" }, Rows = rows,
                Warnings = warnings, Tech = tech, Notes = notes,
            };
        }
        else
        {
            var d = CoupledLineTuner.CenterResponse(CoupledLineSynthesizer.Synthesize(sub, g, f0, bw));
            var notes = new List<string>();
            if (calNote is not null) notes.Add(calNote);
            if (Bool(p, "optimize", true))
            {
                progress?.Invoke("Optimising on the circuit model");
                var o = CoupledLineOptimizer.Optimize(d, g);
                if (o.FinalError < o.InitialError)
                {
                    d = o.Design;
                    notes.Add($"Optimised on the circuit model against the ideal response: RMS error {o.InitialError:0.0000} to {o.FinalError:0.0000} ({o.Evaluations} evaluations).");
                }
            }
            var v = CoupledLineVerification.Run(d, g);
            var lossC = LossInfo.Make(sub, d.Sections[d.Sections.Length / 2].Width, f0, bw, g, v.FrequenciesHz, ff => new CoupledLineCircuitModel(d, lossy: true).Sweep(ff));
            var layout = CoupledLineLayout.From(d);
            var rows = new List<object?[]>();
            for (int i = 0; i < d.Sections.Length; i++)
            {
                var s = d.Sections[i];
                rows.Add(new object?[] { (i + 1).ToString(), s.Width, s.Gap, s.Length, s.Z0e, s.Z0o });
            }
            warnings.AddRange(d.Warnings);
            return new FilterResult
            {
                Hairpin = false, Substrate = sub, F0 = f0, Bandwidth = bw, SpecF0 = specF0, SpecBandwidth = specBw, FeedWidth = d.FeedWidth, Layout = layout,
                Verification = v, Loss = lossC, Columns = new[] { "Section", "W", "S", "L", "Z0e", "Z0o" },
                Units = new[] { "", "m", "m", "m", "Ohm", "Ohm" }, Rows = rows, Warnings = warnings, Tech = tech,
                Notes = notes, CoupledDesign = d,
            };
        }
    }

    private FilterResult CrossCoupled(JsonObject p, TechInfo tech, Substrate sub, double specF0, double specBw, double f0, double bw,
        int n, bool cheb, double ripple, double tipGap, string? calNote, List<string> warnings, Action<string>? progress)
    {
        if (sub.IsStripline)
            throw new InvalidOperationException("Cross-coupled open-loop rings are designed for microstrip only. Set Medium to Microstrip.");
        if (n != 4) warnings.Add("The cross-coupled ring filter has four resonators; the order setting is not used for it.");
        double omegaA = Math.Clamp(Num(p, "zeroOmega", 2.0), 1.3, 6.0);
        // Equiripple return loss from the Chebyshev ripple; Butterworth has no ripple, so 20 dB (Hong's tables) is used.
        double rl = cheb ? Math.Clamp(-10 * Math.Log10(1 - Math.Pow(10, -ripple / 10)), 6, 35) : 20.0;
        bool optimize = Bool(p, "optimize", true);
        string key = $"{sub}|{f0:R}|{bw:R}|{omegaA:R}|{rl:R}|{tipGap:R}|{optimize}";
        CrossCoupledDesign? d = _ccCache is { } c && c.Key == key ? c.D : null;
        if (d is null)
        {
            Exception? last = null;
            foreach (double z in new[] { 50.0, 70.0, 90.0, 110.0 })
            {
                try
                {
                    d = CrossCoupledRingSynthesizer.Synthesize(sub, f0, bw, new CrossCoupledOptions
                    {
                        OmegaA = omegaA, ReturnLossDb = rl, TipGap = tipGap, ResonatorZ0 = z, Optimize = optimize,
                    }, progress);
                    if (z > 50) d = d with { Warnings = d.Warnings.Prepend($"A 50 ohm line is too wide for a square ring here, so the rings use {z:0} ohm lines ({d.Width * 1e3:0.##} mm); the feeds stay 50 ohm.").ToList() };
                    break;
                }
                catch (InvalidOperationException e) when (e.Message.Contains("does not fit")) { last = e; }
            }
            if (d is null) throw last ?? new InvalidOperationException("No cross-coupled ring design found.");
            _ccCache = (key, d);
        }
        warnings.AddRange(d.Warnings.Where(x => !x.StartsWith("Optimised", StringComparison.Ordinal)));
        var notes = new List<string>();
        if (calNote is not null) notes.Add(calNote);
        notes.AddRange(d.Warnings.Where(x => x.StartsWith("Optimised", StringComparison.Ordinal)));
        var (zl, zu) = d.Zeros;
        notes.Add($"Quasi-elliptic (Hong and Lancaster 10.1): transmission zeros at {zl / 1e9:0.####} and {zu / 1e9:0.####} GHz " +
                  $"(Omega_a {omegaA:0.##}), {rl:0.#} dB equiripple return loss. Couplings M12 = M34 = {d.Target.M[0, 1]:0.#####}, M23 = {d.Target.M[1, 2]:0.#####}, " +
                  $"M14 = {d.Target.M[0, 3]:0.#####} ({(d.CrossIsElectric ? "electric, open gaps facing" : "magnetic, folds facing")}), Qe = {d.Target.QeIn:0.##}.");

        var f = CrossCoupledRingSynthesizer.Grid(d, 801);
        var (c11, c21) = d.Network().Sweep(f);
        var (i11, i21) = d.Target.Sweep(f);
        double e21 = 0, e11 = 0;
        for (int i = 0; i < f.Length; i++)
        {
            double a = Math.Pow(10, c21[i] / 20) - Math.Pow(10, i21[i] / 20), b = Math.Pow(10, c11[i] / 20) - Math.Pow(10, i11[i] / 20);
            e21 += a * a; e11 += b * b;
        }
        var v = new HairpinVerification(f, c11, c21, i11, i21, HairpinCircuitModel.Metrics(f, c21, c11), HairpinCircuitModel.Metrics(f, i21, i11),
            Math.Sqrt(e21 / f.Length), Math.Sqrt(e11 / f.Length));
        var pr = d.Prototype;
        var loss = LossInfo.Make(sub, d.Width, f0, bw, new[] { 1.0, pr.G1, pr.G2, pr.G2, pr.G1, 1.0 }, f, ff => d.Network(lossy: true).Sweep(ff));
        var layout = CrossCoupledLayout.From(d);
        var rows = new List<object?[]>
        {
            new object?[] { "Ring line width", d.Width },
            new object?[] { "Feed width", d.FeedWidth },
            new object?[] { "Top rings: side (centreline)", d.Side },
            new object?[] { "Top rings: outer side", d.Side + d.Width },
            new object?[] { "Bottom rings: side (centreline)", d.SideBottom },
            new object?[] { "Bottom rings: outer side", d.SideBottom + d.Width },
            new object?[] { "Ring open gap", d.TipGap },
            new object?[] { "Gap 1-2 and 3-4 (between rows)", d.Gap12 },
            new object?[] { "Gap 2-3 (bottom row, main)", d.Gap23 },
            new object?[] { "Gap 1-4 (top row, cross)", d.Gap14 },
            new object?[] { "Tap from fold centre", d.Tap },
        };
        return new FilterResult
        {
            Hairpin = true, OpenLoop = true, CrossCoupled = true, Substrate = sub, F0 = f0, Bandwidth = bw, SpecF0 = specF0, SpecBandwidth = specBw,
            FeedWidth = d.FeedWidth, Layout = layout, Verification = v, Loss = loss,
            Columns = new[] { "Dimension", "Value" }, Units = new[] { "", "m" }, Rows = rows, Warnings = warnings, Tech = tech, Notes = notes,
        };
    }

    private static Substrate ReadSubstrate(JsonObject p, TechInfo tech, bool stripline, List<string> warnings)
    {
        double er, h, t, tand, sigma;
        string source = Str(p, "substrateSource", "From technology");
        bool fromTech = source == "From technology";
        if (source == "Rogers material")
        {
            var m = RogersMaterials.Find(Str(p, "material", "RO4350B"))
                ?? throw new InvalidOperationException("Unknown Rogers material.");
            er = m.Er; tand = m.TanD; h = Num(p, "h", 0.508e-3); t = Num(p, "copper", 17.5e-6); sigma = 5.8e7;
            if (!m.ThicknessMil.Any(mil => Math.Abs(mil * RogersMaterial.MetresPerMil - h) < 0.02 * h))
                warnings.Add($"{m.Name} is usually stocked in {string.Join(", ", m.ThicknessMil)} mil; h = {h * 1e3:0.###} mm is not one of them" +
                             (stripline ? " (for stripline, h is the plane spacing b)." : "."));
        }
        else if (fromTech)
        {
            if (tech.Dielectrics.Count == 0)
                throw new InvalidOperationException(
                    "Could not read the dielectric stackup from the selected technology. Set 'Substrate' to Manual and enter Er, thickness and tan d.");
            er = tech.Dielectrics[0].Er; tand = tech.Dielectrics[0].TanD;
            if (stripline)
            {
                if (tech.Dielectrics.Count < 2)
                    warnings.Add("The technology has a single dielectric; stripline needs two cores around the strip. Plane spacing taken as twice its thickness.");
                h = tech.Dielectrics.Count >= 2 ? tech.Dielectrics.Sum(x => x.Thickness) : 2.0 * tech.Dielectrics[0].Thickness;
            }
            else
            {
                if (tech.Dielectrics.Count >= 2)
                    warnings.Add("The technology has two dielectrics (a stripline stack-up); microstrip uses the first one only.");
                h = tech.Dielectrics[0].Thickness;
            }
            t = tech.CopperThickness ?? 35e-6;
            sigma = tech.Sigma ?? 5.8e7;
        }
        else
        {
            er = Num(p, "er", 2.2); h = Num(p, "h", 1.575e-3); tand = Num(p, "tand", 0.0009);
            t = Num(p, "copper", 35e-6); sigma = 5.8e7;
        }
        if (er < 1 || h <= 0) throw new InvalidOperationException("Er must be at least 1 and the thickness positive.");
        var sub = new Substrate(er, h, t, tand, sigma);
        return stripline ? sub.AsStripline() : sub;
    }
}
