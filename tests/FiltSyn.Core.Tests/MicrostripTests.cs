using FiltSyn.Core;
using Xunit;

namespace FiltSyn.Core.Tests;

public class MicrostripTests
{
    // Reference values: scikit-rf MLine (Hammerstad-Jensen static, Kirschning-Jansen dispersion),
    // generated independently of this code base.

    [Theory]
    [InlineData(4.4, 1.6e-3, 35e-6, 3.0e-3, 3.3008)]
    [InlineData(3.55, 0.508e-3, 17.5e-6, 1.1e-3, 2.7556)]
    [InlineData(9.8, 0.635e-3, 5e-6, 0.6e-3, 6.5052)]
    [InlineData(2.2, 0.787e-3, 17.5e-6, 2.4e-3, 1.8745)]
    public void StaticEpsEff_MatchesReference(double er, double h, double t, double w, double expected)
    {
        var (_, eps) = Microstrip.Static(new Substrate(er, h, t), w);
        Assert.InRange(eps, expected * 0.999, expected * 1.001);
    }

    [Theory]
    [InlineData(2e9, 3.3411)]
    [InlineData(10e9, 3.6075)]
    public void Dispersion_Fr4_MatchesReference(double f, double expected)
    {
        var s = new Substrate(4.4, 1.6e-3, 35e-6);
        double eps = Microstrip.EpsEffAt(s, 3.0e-3, f);
        Assert.InRange(eps, expected * 0.997, expected * 1.003);
    }

    [Fact]
    public void Dispersion_IsMonotonicAndBounded()
    {
        var s = Substrate.Alumina();
        double prev = Microstrip.EpsEffAt(s, 0.6e-3, 1e6);
        foreach (double f in new[] { 1e9, 5e9, 10e9, 20e9, 40e9 })
        {
            double e = Microstrip.EpsEffAt(s, 0.6e-3, f);
            Assert.True(e >= prev);
            Assert.True(e < s.Er);
            prev = e;
        }
    }

    [Theory]
    [InlineData(25.0)]
    [InlineData(50.0)]
    [InlineData(100.0)]
    public void WidthForZ0_RoundTrips(double z0)
    {
        var s = Substrate.RogersRo4003();
        double w = Microstrip.WidthForZ0(s, z0);
        Assert.Equal(z0, Microstrip.Static(s, w).Z0, 4);
    }

    [Fact]
    public void FiftyOhmWidth_Fr4_IsAbout3mm()
    {
        double w = Microstrip.WidthForZ0(Substrate.Fr4(), 50.0);
        Assert.InRange(w * 1e3, 2.9, 3.1);
    }

    [Theory]
    [InlineData(1e9, 0.9607, 0.3625)]
    [InlineData(2e9, 1.3588, 0.7261)]
    [InlineData(5e9, 2.1486, 1.8254)]
    public void Loss_MatchesReference_Ro4003(double f, double acDbPerM, double adDbPerM)
    {
        // scikit-rf: w = 1.1143 mm, h = 0.508 mm, t = 17.5 um, er = 3.55, tand = 0.0027, rho = 1.724e-8
        var s = new Substrate(3.55, 0.508e-3, 17.5e-6, 0.0027, 1.0 / 1.724e-8);
        var p = Microstrip.Analyze(s, 1.1143e-3, f);
        Assert.InRange(p.AlphaCondNpPerM * 8.6859, acDbPerM * 0.95, acDbPerM * 1.05);
        Assert.InRange(p.AlphaDielNpPerM * 8.6859, adDbPerM * 0.95, adDbPerM * 1.05);
    }

    [Fact]
    public void UnloadedQ_DropsWithLossTangent_AndRoughness()
    {
        var clean = new Substrate(3.55, 0.508e-3, 17.5e-6, 0.0, 5.8e7, 0.0);
        var lossy = clean with { TanD = 0.0027 };
        var rough = lossy with { RoughnessRms = 1.5e-6 };
        double w = 1.1e-3, f = 2e9;
        double q0 = Microstrip.UnloadedQ(clean, w, f);
        double q1 = Microstrip.UnloadedQ(lossy, w, f);
        double q2 = Microstrip.UnloadedQ(rough, w, f);
        Assert.True(q0 > q1 && q1 > q2);
    }

    [Fact]
    public void GuidedWavelength_IsConsistentWithEpsEff()
    {
        var s = Substrate.Fr4();
        double lg = Microstrip.GuidedWavelength(s, 3.0e-3, 2e9);
        double e = Microstrip.EpsEffAt(s, 3.0e-3, 2e9);
        Assert.Equal(Microstrip.C0 / 2e9 / Math.Sqrt(e), lg, 9);
    }
}

public class CoupledMicrostripTests
{
    // Reference: tools/fd_coupled_check.py (independent 2D finite-difference solver, zero-thickness strips).
    // Closed forms are good to a few percent, so tolerances are loose; eps_eff is the quantity that sets k.

    [Theory]
    [InlineData(3.55, 0.508e-3, 1.1e-3, 0.2e-3, 60.44, 40.47, 2.940, 2.479)]
    [InlineData(3.55, 0.508e-3, 1.1e-3, 0.5e-3, 56.22, 44.66, 2.939, 2.550)]
    [InlineData(3.55, 0.508e-3, 1.1e-3, 1.0e-3, 54.59, 48.71, 2.894, 2.619)]
    [InlineData(9.8, 0.635e-3, 0.6e-3, 0.15e-3, 63.93, 31.27, 7.048, 5.604)]
    [InlineData(2.2, 0.787e-3, 2.4e-3, 0.3e-3, 58.51, 40.60, 1.953, 1.736)]
    public void EvenOdd_AgreeWithFiniteDifference(double er, double h, double w, double gap,
        double z0e, double z0o, double ee, double eo)
    {
        var p = CoupledMicrostrip.Static(new Substrate(er, h, 0.0), w, gap);
        Assert.InRange(p.Z0e, z0e * 0.97, z0e * 1.03);
        Assert.InRange(p.Z0o, z0o * 0.95, z0o * 1.05);
        Assert.InRange(p.EpsEffEven, ee * 0.985, ee * 1.015);
        Assert.InRange(p.EpsEffOdd, eo * 0.985, eo * 1.015);
    }

    [Fact]
    public void ModeOrdering_AndLargeGapLimit()
    {
        var s = Substrate.RogersRo4003();
        double w = 1.1e-3;
        var tight = CoupledMicrostrip.Static(s, w, 0.15e-3);
        Assert.True(tight.Z0e > tight.Z0o);
        Assert.True(tight.EpsEffEven > tight.EpsEffOdd);
        Assert.True(tight.CouplingCoefficient > 0);

        var far = CoupledMicrostrip.Static(s, w, 20e-3);
        var (z0, e0) = Microstrip.Static(s, w);
        Assert.InRange(far.Z0e, z0 * 0.99, z0 * 1.01);
        Assert.InRange(far.Z0o, z0 * 0.99, z0 * 1.01);
        Assert.InRange(far.EpsEffEven, e0 * 0.98, e0 * 1.02);
        Assert.InRange(far.EpsEffOdd, e0 * 0.98, e0 * 1.02);
    }

    [Fact]
    public void Coupling_DecreasesWithGap()
    {
        var s = Substrate.RogersRo4003();
        double prev = double.MaxValue;
        foreach (double gap in new[] { 0.1e-3, 0.2e-3, 0.4e-3, 0.8e-3, 1.6e-3, 3.2e-3 })
        {
            double k = CoupledMicrostrip.Static(s, 1.1e-3, gap).CouplingCoefficient;
            Assert.True(k < prev);
            prev = k;
        }
    }

    [Fact]
    public void GapForCoupling_RoundTrips()
    {
        var s = Substrate.RogersRo4003();
        double gap = CoupledMicrostrip.GapForCoupling(s, 1.1e-3, 0.05);
        Assert.Equal(0.05, CoupledMicrostrip.Static(s, 1.1e-3, gap).CouplingCoefficient, 6);
    }
}

public class SubstratePresetTests
{
    [Fact]
    public void Ro4350B_HasDatasheetValues()
    {
        var s = Substrate.RogersRo4350B();
        Assert.Equal(3.48, s.Er);
        Assert.Equal(0.0037, s.TanD);
        Assert.Equal(0.508e-3, s.H, 9);
        // A 50 ohm line on 20 mil RO4350B is about 1.1 mm wide.
        double w = Microstrip.WidthForZ0(s, 50.0);
        Assert.InRange(w * 1e3, 1.05, 1.25);
    }
}

public class ThickSubstrateTests
{
    [Theory]
    [InlineData(0.813e-3)]
    [InlineData(1.524e-3)]
    public void ThickerRogersBoards_GiveWiderFiftyOhmLines(double h)
    {
        double w20 = Microstrip.WidthForZ0(Substrate.RogersRo4003(0.508e-3), 50.0);
        double w = Microstrip.WidthForZ0(Substrate.RogersRo4003(h), 50.0);
        Assert.InRange(w / h, 2.0, 2.4);          // 50 ohm on er 3.55 is w/h of about 2.2 (1.1 mm on 20 mil)
        Assert.True(w > w20);
    }

    [Fact]
    public void Hairpin_SynthesizesOn60MilRo4350B()
    {
        var s = Substrate.RogersRo4350B(1.524e-3);
        var d = HairpinSynthesizer.Synthesize(s, Prototype.Chebyshev(4, 0.1), 2e9, 60e6);
        Assert.Equal(4, d.Resonators.Length);
    }
}

public class Rt5880Tests
{
    public static readonly double[] Thicknesses = { 0.127e-3, 0.254e-3, 0.381e-3, 0.508e-3, 0.787e-3, 1.575e-3, 3.175e-3 };

    [Fact]
    public void Preset_HasDatasheetValues()
    {
        var s = Substrate.RogersRt5880();
        Assert.Equal(2.20, s.Er);
        Assert.Equal(0.0009, s.TanD);
    }

    [Theory]
    [MemberData(nameof(All))]
    public void FiftyOhmLine_ScalesWithThickness(double h)
    {
        var s = Substrate.RogersRt5880(h);
        double w = Microstrip.WidthForZ0(s, 50.0);
        Assert.InRange(w / h, 2.8, 3.4);       // w/h of a 50 ohm line at er 2.2 is about 3.1
        Assert.InRange(Microstrip.Static(s, w).Z0, 49.99, 50.01);
    }

    [Theory]
    [InlineData(0.787e-3)]
    [InlineData(1.575e-3)]
    public void CoupledLineFilter_SynthesizesOn5880(double h)
    {
        var s = Substrate.RogersRt5880(h);
        var d = CoupledLineSynthesizer.Synthesize(s, Prototype.Chebyshev(4, 0.1), 5e9, 500e6);
        Assert.Equal(5, d.Sections.Length);
        foreach (var sec in d.Sections) Assert.True(sec.Width > 0 && sec.Gap > 0 && sec.Length > 0);
    }

    public static IEnumerable<object[]> All() => Thicknesses.Select(h => new object[] { h });
}

public class RogersMaterialTests
{
    [Fact]
    public void LibraryIsConsistentWithLegacyFactories()
    {
        Assert.Equal(3.66, RogersMaterials.Find("RO4350B")!.Er);
        Assert.Equal(Substrate.RogersRt5880().TanD, RogersMaterials.Find("rt/duroid 5880")!.TanD);
        Assert.Equal(Substrate.RogersRo4003().Er, RogersMaterials.Find("RO4003C")!.Er);
    }

    [Fact]
    public void EveryMaterialGivesAFiftyOhmLine()
    {
        foreach (var m in RogersMaterials.All)
        {
            var s = m.Make(m.ThicknessMil[0]);
            double w = Microstrip.WidthForZ0(s, 50.0);
            Assert.InRange(Microstrip.Static(s, w).Z0, 49.0, 51.0);
        }
    }
}
