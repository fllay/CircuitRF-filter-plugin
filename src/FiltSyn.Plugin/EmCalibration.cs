using FiltSyn.Core;

namespace FiltSyn.Plugin;

/// <summary>
/// Closes the loop between the circuit model and a full-wave result. The circuit model and the EM solver disagree by a
/// roughly constant fraction of the centre frequency and of the bandwidth, so the design is pre-distorted: the filter
/// is synthesised for f0 x f0Corr and bw x bwCorr, and each EM result updates the two factors until the EM response
/// lands on the specification.
/// </summary>
internal static class EmCalibration
{
    public const double MinFactor = 0.5, MaxFactor = 2.0;

    public readonly record struct Update(double F0Corr, double BwCorr, double MeasuredCenterHz, double MeasuredBw3dBHz,
        bool WithinTolerance);

    /// <param name="specF0Hz">The centre frequency the user asked for.</param>
    /// <param name="targetBw3dBHz">The -3 dB bandwidth of the ideal response of the specification.</param>
    /// <param name="f0Corr">The factors the EM-simulated cell was designed with.</param>
    public static Update Compute(double specF0Hz, double targetBw3dBHz, double f0Corr, double bwCorr,
        double[] freqHz, double[] s21dB, double[] s11dB)
    {
        if (freqHz.Length < 5) throw new InvalidOperationException("The EM result has too few frequency points.");
        var m = HairpinCircuitModel.Metrics(freqHz, s21dB, s11dB);
        if (double.IsNaN(m.CenterHz) || double.IsNaN(m.Bandwidth3dBHz) || m.Bandwidth3dBHz <= 0)
            throw new InvalidOperationException(
                "The EM sweep does not contain both -3 dB points of the passband. Place the cell again with a larger EM span, or pick the right result file.");
        if (Math.Abs(m.CenterHz / specF0Hz - 1) > 0.2)
            throw new InvalidOperationException(
                $"The EM result's passband is centred at {m.CenterHz / 1e9:0.###} GHz, more than 20 % from the specified {specF0Hz / 1e9:0.###} GHz: it does not look like this filter's result.");

        double dF = Math.Abs(m.CenterHz / specF0Hz - 1);
        double dB = Math.Abs(m.Bandwidth3dBHz / targetBw3dBHz - 1);
        bool within = dF < 0.001 && dB < 0.02;
        double nf = Math.Clamp(f0Corr * specF0Hz / m.CenterHz, MinFactor, MaxFactor);
        double nb = Math.Clamp(bwCorr * targetBw3dBHz / m.Bandwidth3dBHz, MinFactor, MaxFactor);
        return within
            ? new Update(f0Corr, bwCorr, m.CenterHz, m.Bandwidth3dBHz, true)
            : new Update(nf, nb, m.CenterHz, m.Bandwidth3dBHz, false);
    }
}
