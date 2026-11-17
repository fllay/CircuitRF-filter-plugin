using FiltSyn.Core;
using Xunit;

namespace FiltSyn.Core.Tests;

public class StriplineTests
{
    private static Substrate Thin(double er = 1.0, double b = 1e-3) => new Substrate(er, b, T: 0.0).AsStripline();

    [Fact]
    public void WideStripMatchesWheelerAsymptote()
    {
        // Z0 sqrt(er) = 30 pi b / (w + 0.441 b) for w/b > 0.35
        var s = Thin(1.0);
        double z = Stripline.Static(s, 1e-3).Z0;
        Assert.InRange(z, 65.0, 65.8);
    }

    [Fact]
    public void ImpedanceScalesWithInverseSqrtEr()
    {
        double z1 = Stripline.Static(Thin(1.0), 0.8e-3).Z0;
        double z4 = Stripline.Static(Thin(4.0), 0.8e-3).Z0;
        Assert.Equal(z1 / 2.0, z4, 6);
    }

    [Fact]
    public void EpsEffIsErAndDispersionFree()
    {
        var s = new Substrate(2.2, 1.5e-3, 17.5e-6, 0.0009).AsStripline();
        Assert.Equal(2.2, Microstrip.Static(s, 1e-3).EpsEff, 12);
        Assert.Equal(2.2, Microstrip.EpsEffAt(s, 1e-3, 20e9), 12);
        Assert.Equal(Microstrip.C0 / (3e9 * Math.Sqrt(2.2)), Microstrip.GuidedWavelength(s, 1e-3, 3e9), 9);
    }

    [Fact]
    public void CoupledModesMatchCohnAndFiniteDifference()
    {
        // b = 1, w = 0.5, s = 0.3, er = 1: Cohn 112.84 / 86.33 ohm; 2-D finite-difference (converging) 111.9 / 85.1 at n = 80.
        var p = Stripline.Coupled(Thin(1.0, 1.0), 0.5, 0.3);
        Assert.InRange(p.Z0e, 112.5, 113.2);
        Assert.InRange(p.Z0o, 86.0, 86.7);
        Assert.True(p.Homogeneous);
        Assert.Equal(p.EpsEffEven, p.EpsEffOdd);
    }

    [Fact]
    public void WideGapRecoversSingleLine()
    {
        var s = Thin(2.2, 1.5e-3);
        double single = Stripline.Static(s, 1e-3).Z0;
        var p = Stripline.Coupled(s, 1e-3, 30e-3);
        Assert.Equal(single, p.Z0e, 0);
        Assert.Equal(single, p.Z0o, 0);
        Assert.True(p.Z0e >= p.Z0o);
    }

    [Fact]
    public void ThicknessLowersImpedance()
    {
        var thin = new Substrate(2.2, 1.5e-3, 0.0).AsStripline();
        var thick = new Substrate(2.2, 1.5e-3, 35e-6).AsStripline();
        Assert.True(Stripline.Static(thick, 0.5e-3).Z0 < Stripline.Static(thin, 0.5e-3).Z0);
    }

    [Theory]
    [InlineData(50.0)]
    [InlineData(70.0)]
    [InlineData(30.0)]
    public void WidthForZ0RoundTrips(double z0)
    {
        var s = new Substrate(2.2, 1.524e-3, 17.5e-6, 0.0009).AsStripline();
        double w = Microstrip.WidthForZ0(s, z0);
        Assert.Equal(z0, Microstrip.Static(s, w).Z0, 6);
    }

    [Fact]
    public void LossIsPlausibleForFiftyOhmLine()
    {
        // 50 ohm copper stripline, er 2.2, b 60 mil at 10 GHz: dielectric 27.3 sqrt(er) tan(d)/lambda0 = 0.012 dB/cm;
        // conductor of order 0.02-0.1 dB/cm.
        var s = new Substrate(2.2, 1.524e-3, 17.5e-6, 0.0009).AsStripline();
        double w = Microstrip.WidthForZ0(s, 50.0);
        var p = Microstrip.Analyze(s, w, 10e9);
        double dbCm = 8.686 / 100.0;
        Assert.InRange(p.AlphaCondNpPerM * dbCm, 0.01, 0.12);
        Assert.InRange(p.AlphaDielNpPerM * dbCm, 0.010, 0.014);
        Assert.Equal(p.EpsEffStatic, p.EpsEff);
    }

    [Fact]
    public void OpenEndExtensionIsAFractionOfPlaneSpacing()
    {
        var s = new Substrate(2.2, 1.524e-3, 17.5e-6, 0.0009).AsStripline();
        double w = Microstrip.WidthForZ0(s, 50.0);
        double dl = HairpinSynthesizer.OpenEndExtension(s, w);
        Assert.InRange(dl / s.H, 0.05, 0.25);
    }

    [Fact]
    public void MicrostripUnaffectedByDefault()
    {
        var s = Substrate.RogersRo4003();
        Assert.False(s.IsStripline);
        Assert.True(Microstrip.EpsEffAt(s, 1.1e-3, 3e9) < 3.55);
    }

    [Fact]
    public void FromCoresDoublesPlaneSpacing()
    {
        var s = Substrate.RogersRt5880(0.787e-3).AsStriplineFromCores();
        Assert.True(s.IsStripline);
        Assert.Equal(1.574e-3, s.H, 6);
    }
}
