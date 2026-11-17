namespace FiltSyn.Core;

/// <summary>Final tuning of a hairpin design against the circuit model of the complete filter.</summary>
public static class HairpinTuner
{
    /// <summary>
    /// Shorten or lengthen all arms until the -3 dB centre of the circuit response sits on f0. Each hairpin was
    /// tuned alone, but in the filter the neighbours load it, so the passband lands slightly low or high.
    /// </summary>
    public static HairpinDesign CenterResponse(HairpinDesign d, int maxIterations = 8, double toleranceFractionOfBw = 0.002)
    {
        for (int it = 0; it < maxIterations; it++)
        {
            double fc = Center(d);
            double err = d.F0Hz - fc;
            if (Math.Abs(err) <= toleranceFractionOfBw * d.BandwidthHz) break;
            // Resonance scales as 1 / path; a path change of dP moves it by -dP / P.
            double dArm = -(d.PathLength / 2.0) * err / d.F0Hz;
            d = d.WithGeometry(d.ArmLength + dArm, d.Gaps, d.TapDistance);
        }
        return d;
    }

    private static double Center(HairpinDesign d)
    {
        var model = new HairpinCircuitModel(d);
        int points = 801;
        double lo = d.F0Hz - 2.0 * d.BandwidthHz, hi = d.F0Hz + 2.0 * d.BandwidthHz;
        var f = new double[points];
        for (int i = 0; i < points; i++) f[i] = lo + (hi - lo) * i / (points - 1);
        var (s11, s21) = model.Sweep(f);
        return HairpinCircuitModel.Metrics(f, s21, s11).CenterHz;
    }

    public readonly record struct TuneResult(HairpinDesign Design, double InitialError, double FinalError, int Evaluations);

    /// <summary>
    /// Polish arm length, gaps and tap against the ideal response of prototype g. Parameters are multiplicative
    /// factors, shared symmetrically (mirror gaps equal, both taps equal). The error is the RMS difference in
    /// linear |S11| and |S21| over f0 +/- 1.5 BW.
    /// </summary>
    public static TuneResult Optimize(HairpinDesign d, double[] g, int maxEvaluations = 600)
    {
        int nGaps = d.Gaps.Length;
        int groups = (nGaps + 1) / 2;
        var ideal = CouplingMatrix.FromPrototype(g, d.F0Hz, d.BandwidthHz);

        int points = 121;
        var f = new double[points];
        double lo = d.F0Hz - 1.5 * d.BandwidthHz, hi = d.F0Hz + 1.5 * d.BandwidthHz;
        for (int i = 0; i < points; i++) f[i] = lo + (hi - lo) * i / (points - 1);
        var target = f.Select(fr => ideal.Evaluate(fr)).ToArray();

        double arm0 = d.ArmLength, tap0 = d.TapDistance;
        double[] gap0 = (double[])d.Gaps.Clone();

        HairpinDesign Build(double[] x)
        {
            double arm = arm0 * Math.Exp(x[0]);
            var gaps = new double[nGaps];
            for (int i = 0; i < nGaps; i++) gaps[i] = gap0[i] * Math.Exp(x[1 + Math.Min(i, nGaps - 1 - i)]);
            double tap = Math.Min(tap0 * Math.Exp(x[1 + groups]), 0.45 * (2 * arm + d.BaseLength));
            return d.WithGeometry(arm, gaps, tap);
        }

        double Error(double[] x)
        {
            HairpinDesign cand;
            try { cand = Build(x); } catch { return 1e3; }
            double pen = 0;
            foreach (var gp in cand.Gaps) if (gp < 0.02e-3) pen += 1e3;
            if (cand.ArmLength <= 0) return 1e3;
            HairpinCircuitModel model;
            double sum = 0;
            try
            {
                model = new HairpinCircuitModel(cand);
                for (int i = 0; i < points; i++)
                {
                    var (s11, s21) = model.Evaluate(f[i]);
                    double a = s21.Magnitude - target[i].S21.Magnitude;
                    double b = s11.Magnitude - target[i].S11.Magnitude;
                    sum += a * a + b * b;
                }
            }
            catch { return 1e3; }
            return Math.Sqrt(sum / points) + pen;
        }

        var x0 = new double[2 + groups];
        double initial = Error(x0);
        var r = NelderMead.Minimize(Error, x0, step: 0.01, tolerance: 1e-9, maxEvaluations: maxEvaluations);
        if (r.Value >= initial) return new TuneResult(d, initial, initial, r.Evaluations);
        return new TuneResult(Build(r.X), initial, r.Value, r.Evaluations);
    }
}
