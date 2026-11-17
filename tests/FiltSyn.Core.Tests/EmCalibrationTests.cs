using FiltSyn.Plugin;
using Xunit;

namespace FiltSyn.Core.Tests;

public class EmCalibrationTests
{
    // A smooth band-pass: Gaussian-in-dB centred at fc with the given -3 dB width.
    private static (double[] F, double[] S21, double[] S11) Band(double fc, double bw3, double lo = 3.0e9, double hi = 4.2e9)
    {
        var f = Enumerable.Range(0, 241).Select(i => lo + (hi - lo) * i / 240.0).ToArray();
        double k = 3.0103 / Math.Pow(bw3 / 2, 2);
        return (f, f.Select(x => -1.0 - k * Math.Pow(x - fc, 2)).ToArray(), f.Select(_ => -15.0).ToArray());
    }

    [Fact]
    public void Compute_ScalesDesignTowardsTheSpecification()
    {
        var (f, s21, s11) = Band(3.517e9, 275e6);
        var u = EmCalibration.Compute(3.6e9, 242.6e6, 1.0, 1.0, f, s21, s11);
        Assert.False(u.WithinTolerance);
        Assert.InRange(u.F0Corr, 3.6 / 3.517 * 0.997, 3.6 / 3.517 * 1.003);
        Assert.InRange(u.BwCorr, 242.6 / 275.0 * 0.995, 242.6 / 275.0 * 1.005);
    }

    [Fact]
    public void Compute_IsRelativeToTheFactorsTheCellWasDesignedWith()
    {
        var (f, s21, s11) = Band(3.595e9, 251e6);
        var u = EmCalibration.Compute(3.6e9, 242.6e6, 1.0235, 0.883, f, s21, s11);
        Assert.InRange(u.F0Corr, 1.0235 * 3.6 / 3.595 * 0.997, 1.0235 * 3.6 / 3.595 * 1.003);
        Assert.InRange(u.BwCorr, 0.883 * 242.6 / 251.0 * 0.995, 0.883 * 242.6 / 251.0 * 1.005);
    }

    [Fact]
    public void Compute_LeavesFactorsAloneWhenAlreadyOnTarget()
    {
        var (f, s21, s11) = Band(3.6005e9, 243e6);
        var u = EmCalibration.Compute(3.6e9, 242.6e6, 1.02, 0.87, f, s21, s11);
        Assert.True(u.WithinTolerance);
        Assert.Equal(1.02, u.F0Corr);
        Assert.Equal(0.87, u.BwCorr);
    }

    [Fact]
    public void Compute_RefusesAResultThatIsNotThisFilter()
    {
        var (f, s21, s11) = Band(2.0e9, 200e6, 1.5e9, 2.5e9);
        Assert.Throws<InvalidOperationException>(() => EmCalibration.Compute(3.6e9, 242.6e6, 1, 1, f, s21, s11));
    }

    [Fact]
    public void Compute_RefusesASweepThatMissesTheBandEdges()
    {
        var (f, s21, s11) = Band(3.6e9, 600e6, 3.5e9, 3.7e9);
        Assert.Throws<InvalidOperationException>(() => EmCalibration.Compute(3.6e9, 242.6e6, 1, 1, f, s21, s11));
    }
}
