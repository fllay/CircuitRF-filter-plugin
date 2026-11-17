using System.Text.Json.Nodes;
using FiltSyn.Core;

namespace FiltSyn.Plugin;

/// <summary>One synthesised filter and everything the panel and the placed cell need from it.</summary>
internal sealed class FilterResult
{
    public required bool Hairpin { get; init; }
    /// <summary>Square open-loop ring chain (a hairpin variant); Hairpin is also true.</summary>
    public bool OpenLoop { get; init; }
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
}

internal sealed class DesignEngine
{
    private HairpinCalibration? _cal;
    private (Substrate S, double F0, double Tip, bool Alt)? _calKey;

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
        bool openLoop = filterType == "Open-loop ring";
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
                double w = Microstrip.WidthForZ0(sub, 50.0);
                cal = HairpinCalibration.Run(sub, f0, w, 2.0 * w, HairpinSynthesizer.OpenEndExtension(sub, w),
                    tipGap: tipGap, square: openLoop, alternate: alternate);
                _cal = cal; _calKey = (sub, f0, tipGap, alternate);
            }
            var opts = openLoop ? new HairpinOptions { TipGap = tipGap, Alternate = alternate } : null;
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
                Verification = v, Columns = new[] { "Dimension", "Value" }, Units = new[] { "", "m" }, Rows = rows,
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
                Verification = v, Columns = new[] { "Section", "W", "S", "L", "Z0e", "Z0o" },
                Units = new[] { "", "m", "m", "m", "Ohm", "Ohm" }, Rows = rows, Warnings = warnings, Tech = tech,
                Notes = notes, CoupledDesign = d,
            };
        }
    }

    private static Substrate ReadSubstrate(JsonObject p, TechInfo tech, bool stripline, List<string> warnings)
    {
        double er, h, t, tand, sigma;
        bool fromTech = Str(p, "substrateSource", "From technology") == "From technology";
        if (fromTech)
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
