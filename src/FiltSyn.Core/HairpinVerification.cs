namespace FiltSyn.Core;

/// <summary>Circuit-level check of a hairpin design against the ideal coupled-resonator response.</summary>
public sealed record HairpinVerification(
    double[] FrequenciesHz,
    double[] CircuitS11dB,
    double[] CircuitS21dB,
    double[] IdealS11dB,
    double[] IdealS21dB,
    ResponseMetrics Circuit,
    ResponseMetrics Ideal,
    double RmsS21Error,        // linear magnitude, over the comparison span
    double RmsS11Error)
{
    /// <summary>
    /// Sweep the design's circuit model and the ideal response of prototype g over f0 +/- spanBandwidths x BW.
    /// </summary>
    public static HairpinVerification Run(HairpinDesign design, double[] g, bool lossy = false,
        double spanBandwidths = 2.0, int points = 801)
    {
        var model = new HairpinCircuitModel(design, lossy);
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
