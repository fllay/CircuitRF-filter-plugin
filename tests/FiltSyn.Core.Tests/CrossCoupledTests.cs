using FiltSyn.Core;
using Xunit;

namespace FiltSyn.Core.Tests;

public class CrossCoupledTests
{
    private static readonly Substrate Ro4350 = new(3.66, 0.508e-3, 35e-6, 0.0037);

    [Theory]   // Hong and Lancaster Table 10.1 (n = 4, 20 dB return loss)
    [InlineData(1.80, 0.95974, 1.42192, -0.21083, 1.11769)]
    [InlineData(2.00, 0.95449, 1.38235, -0.16271, 1.06062)]
    [InlineData(2.40, 0.94772, 1.34408, -0.10642, 1.00086)]
    public void PrototypeMatchesHongTable(double a, double g1, double g2, double j1, double j2)
    {
        var p = QuasiEllipticPrototype.Synthesize(a, 20.0);
        Assert.Equal(g1, p.G1, 3); Assert.Equal(g2, p.G2, 3); Assert.Equal(j1, p.J1, 3); Assert.Equal(j2, p.J2, 3);
        Assert.Equal(a, p.ZeroOmega, 4);
    }

    [Theory]
    [InlineData(1.4, 20.0)]
    [InlineData(3.0, 16.4)]
    public void PrototypeIsEquirippleOutsideTheTableRange(double a, double rl)
    {
        var p = QuasiEllipticPrototype.Synthesize(a, rl);
        double worst = Enumerable.Range(0, 401).Max(i => p.S11dB(i / 400.0));
        Assert.InRange(worst, -rl - 0.05, -rl + 0.05);
        Assert.True(p.Evaluate(a).S21.Magnitude < 1e-6);
        Assert.True(p.J1 < 0 && p.J2 > 0);
    }

    [Fact]
    public void RingNetworkAgreesWithTheChainModelAndIgnoresOrientation()
    {
        double w = 1.0734e-3, L = 6.1221e-3, g = 0.3e-3;
        var hp = new HairpinGeometry(Ro4350, 1, w, L - w, L, Array.Empty<double>(), L, HairpinSynthesizer.OpenEndExtension(Ro4350, w), 50, g);
        double fChain = new HairpinCircuitModel(hp).NaturalFrequencies(2e9, 6e9)[0];
        foreach (var side in new[] { RingSide.Top, RingSide.Left, RingSide.Bottom, RingSide.Right })
        {
            var rn = new RingNetwork(Ro4350, w, L, g, new[] { new RingPlacement(0, 0, side) }, new[] { new RingTap(0, 1e-3) });
            Assert.Equal(fChain, rn.NaturalFrequencies(2e9, 6e9)[0], fChain * 0.005);
        }
    }

    [Fact]
    public void QeFallsAsTheTapMovesFromTheFoldCentre()
    {
        double w = 1.0734e-3, L = 6.1221e-3, g = 0.3e-3;
        var probe = new RingNetwork(Ro4350, w, L, g, new[] { new RingPlacement(0, 0, RingSide.Right) }, new[] { new RingTap(0, 1e-3) });
        double prev = double.MaxValue;
        foreach (double t in new[] { 1e-3, 2e-3, 3e-3, 4e-3 })
        {
            var r = new RingNetwork(Ro4350, w, L, g, new[] { new RingPlacement(0, 0, RingSide.Right) }, new[] { new RingTap(0, probe.FoldCentre - t) });
            double q = r.ExternalQ(3.86e9).Qe;
            Assert.True(q < prev);
            prev = q;
        }
    }

    [Fact]
    public void FourPoleDesignHasZerosEitherSideAndAMatchedPassband()
    {
        var d = CrossCoupledRingSynthesizer.Synthesize(Ro4350, 3.6e9, 120e6, new CrossCoupledOptions());
        Assert.True(d.CrossIsElectric);
        var f = CrossCoupledRingSynthesizer.Grid(d, 601);
        var (s11, s21) = d.Network().Sweep(f);
        // Transmission zeros within 1.5 percent of the ideal ones.
        var (zl, zu) = d.Zeros;
        int lo = Array.IndexOf(s21, f.Select((x, i) => (x, i)).Where(t => t.x < 3.6e9).Min(t => s21[t.i]));
        int hi = Array.IndexOf(s21, f.Select((x, i) => (x, i)).Where(t => t.x > 3.6e9).Min(t => s21[t.i]));
        Assert.InRange(f[lo], zl * 0.985, zl * 1.015);
        Assert.InRange(f[hi], zu * 0.985, zu * 1.015);
        Assert.True(s21[lo] < -40 && s21[hi] < -40);
        // Return loss across the middle 80 percent of the passband.
        double worst = f.Select((x, i) => (x, i)).Where(t => Math.Abs(t.x - 3.6e9) < 48e6).Max(t => s11[t.i]);
        Assert.True(worst < -15, $"in-band S11 {worst:0.0} dB");
        var layout = CrossCoupledLayout.From(d);
        Assert.Equal(2, layout.Ports.Count);
        Assert.Equal(6, layout.Polygons.Count(p => p.Layer == FilterLayout.CopperLayer));
    }
}
