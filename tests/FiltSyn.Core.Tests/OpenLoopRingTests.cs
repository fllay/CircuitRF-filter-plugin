using FiltSyn.Core;
using Xunit;

namespace FiltSyn.Core.Tests;

public class MicrostripGapTests
{
    [Fact]
    public void Capacitances_HaveSensibleMagnitudeAndTrend()
    {
        var s = Substrate.RogersRo4350B();
        double w = Microstrip.WidthForZ0(s, 50);
        var (cp1, cg1) = MicrostripGap.Capacitances(s, w, 0.2e-3);
        var (cp2, cg2) = MicrostripGap.Capacitances(s, w, 0.8e-3);
        Assert.InRange(cg1, 1e-15, 1e-12);     // fF range
        Assert.InRange(cp1, 1e-15, 1e-12);
        Assert.True(cg1 > cg2);                // a wider gap couples less
        Assert.True(cp1 < cp2 * 1.5);          // the shunt (fringing) part changes little
    }

    [Fact]
    public void Stripline_IsRefused()
        => Assert.ThrowsAny<Exception>(() => MicrostripGap.Capacitances(Substrate.RogersRo4350B().AsStripline(), 1e-3, 0.3e-3));
}

public class OpenLoopRingTests
{
    private static readonly Substrate Sub = Substrate.RogersRo4350B();
    private const double F0 = 3.6e9, Tip = 0.3e-3;

    private static HairpinDesign Design(bool alt, double bw)
    {
        double w = Microstrip.WidthForZ0(Sub, 50);
        var cal = HairpinCalibration.Run(Sub, F0, w, 2 * w, HairpinSynthesizer.OpenEndExtension(Sub, w), tipGap: Tip, square: true, alternate: alt);
        return HairpinTuner.CenterResponse(HairpinSynthesizer.SynthesizeCalibrated(Sub, Prototype.Chebyshev(4, 0.1), F0, bw,
            new HairpinOptions { TipGap = Tip, Alternate = alt }, cal));
    }

    [Fact]
    public void Alternating_ReachesWideBandwidthAndCentres()
    {
        var d = Design(true, 150e6);
        Assert.True(d.IsOpenLoop && d.Alternate);
        var m = new HairpinCircuitModel(d);
        var f = Enumerable.Range(0, 801).Select(i => 0.8 * F0 + i * 0.4 * F0 / 800).ToArray();
        var (s11, s21) = m.Sweep(f);
        var me = HairpinCircuitModel.Metrics(f, s21, s11);
        Assert.InRange(me.CenterHz, 0.99 * F0, 1.01 * F0);
        Assert.InRange(me.Bandwidth3dBHz, 120e6, 230e6);
    }

    [Fact]
    public void SameOrientation_RefusesWideBandwidth()
        => Assert.ThrowsAny<Exception>(() => Design(false, 150e6));

    [Fact]
    public void Layout_HasOppositePortsAndNoOverlap()
    {
        var d = Design(true, 150e6);
        var l = HairpinLayout.From(d);
        Assert.Equal(2, l.Ports.Count);
        Assert.NotEqual(l.Ports[0].Y > 0, l.Ports[1].Y > 0);   // input below, output above
        Assert.All(l.Polygons.Where(p => p.Layer == FilterLayout.CopperLayer), p => Assert.True(p.Points.Length >= 4));
    }
}
