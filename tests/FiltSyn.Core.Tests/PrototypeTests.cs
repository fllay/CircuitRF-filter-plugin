using FiltSyn.Core;
using Xunit;

namespace FiltSyn.Core.Tests;

public class PrototypeTests
{
    // Reference values from Matthaei, Young, Jones (Chebyshev tables) and Pozar Table 8.4.

    [Fact]
    public void Butterworth_N3()
    {
        var g = Prototype.Butterworth(3);
        Assert.Equal(new[] { 1.0, 1.0, 2.0, 1.0, 1.0 }, g.Select(v => Math.Round(v, 6)).ToArray());
    }

    [Theory]
    [InlineData(0.1, 3, new[] { 1.0316, 1.1474, 1.0316, 1.0 })]
    [InlineData(0.5, 3, new[] { 1.5963, 1.0967, 1.5963, 1.0 })]
    [InlineData(0.1, 5, new[] { 1.1468, 1.3712, 1.9750, 1.3712, 1.1468, 1.0 })]
    [InlineData(0.1, 4, new[] { 1.1088, 1.3061, 1.7703, 0.8180, 1.3554 })]
    public void Chebyshev_MatchesTables(double ripple, int n, double[] expected)
    {
        var g = Prototype.Chebyshev(n, ripple);
        Assert.Equal(n + 2, g.Length);
        Assert.Equal(1.0, g[0]);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], g[i + 1], 3);
    }

    [Fact]
    public void ChebyshevOrder_Pozar_Example()
    {
        // 3 dB-ripple-free check: 0.5 dB ripple, 50 dB at Omega = 1.9 -> classic N = 5? verify monotonic and sane.
        int n = Prototype.ChebyshevOrder(2.0, 40.0, 0.1);
        Assert.InRange(n, 4, 6);
        // Resulting order must meet the spec.
        var cm = CouplingMatrix.FromPrototype(Prototype.Chebyshev(n, 0.1), 1e9, 1e8);
        // convert Omega = 2 back to frequency: solve f/f0 - f0/f = Omega*FBW
        double fbw = 0.1;
        double x = 2.0 * fbw;
        double f = 1e9 * (x + Math.Sqrt(x * x + 4)) / 2.0;
        var (_, s21) = cm.Evaluate(f);
        Assert.True(20 * Math.Log10(s21.Magnitude) <= -40.0);
    }
}

public class CouplingMatrixTests
{
    [Fact]
    public void Butterworth_ThreeDbAtBandEdge()
    {
        var g = Prototype.Butterworth(3);
        var cm = CouplingMatrix.FromPrototype(g, 1e9, 1e8); // FBW = 0.1
        double f = EdgeFrequency(1e9, 0.1, +1.0);
        var (_, s21) = cm.Evaluate(f);
        Assert.Equal(-3.0103, 20 * Math.Log10(s21.Magnitude), 2);
    }

    [Fact]
    public void Chebyshev_RippleAtBandEdge_And_CenterReturnLoss()
    {
        const double ripple = 0.1;
        var g = Prototype.Chebyshev(3, ripple);
        var cm = CouplingMatrix.FromPrototype(g, 2e9, 2e8);
        double f = EdgeFrequency(2e9, 0.1, +1.0);
        var (_, s21Edge) = cm.Evaluate(f);
        Assert.Equal(-ripple, 20 * Math.Log10(s21Edge.Magnitude), 3);

        // Odd order: perfect match at center.
        var (s11, s21) = cm.Evaluate(2e9);
        Assert.True(s11.Magnitude < 1e-9);
        Assert.Equal(1.0, s21.Magnitude, 9);
    }

    [Fact]
    public void Lossless_PowerConservation()
    {
        var cm = CouplingMatrix.FromPrototype(Prototype.Chebyshev(5, 0.2), 1e9, 1e8);
        foreach (double f in new[] { 0.9e9, 0.97e9, 1.0e9, 1.04e9, 1.2e9 })
        {
            var (s11, s21) = cm.Evaluate(f);
            Assert.Equal(1.0, s11.Magnitude * s11.Magnitude + s21.Magnitude * s21.Magnitude, 9);
        }
    }

    [Fact]
    public void Couplings_FromG()
    {
        var g = Prototype.Chebyshev(3, 0.1);
        var cm = CouplingMatrix.FromPrototype(g, 1e9, 1e8);
        Assert.Equal(0.1 / Math.Sqrt(g[1] * g[2]), cm.K[0], 12);
        Assert.Equal(g[1] / 0.1, cm.QeInPhysical, 9);
    }

    // Frequency where Omega = +sign (normalized band edge).
    private static double EdgeFrequency(double f0, double fbw, double omega)
    {
        double x = omega * fbw;
        return f0 * (x + Math.Sqrt(x * x + 4)) / 2.0;
    }
}
