using FiltSyn.Core;
using Xunit;

namespace FiltSyn.Core.Tests;

public class TapAndQeTests
{
    [Fact]
    public void TapAtOpenEnd_GivesMinimumQe()
    {
        // Equal impedances, tap at the voltage maximum: Qe = pi/2.
        Assert.Equal(Math.PI / 2.0, HairpinSynthesizer.QeFromTapAngle(Math.PI / 2.0, 50, 50), 12);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(11.09)]
    [InlineData(60.0)]
    public void TapAngle_RoundTripsQe(double qe)
    {
        double phi = HairpinSynthesizer.TapAngleFromQe(qe, 50, 50);
        Assert.InRange(phi, 0, Math.PI / 2.0);
        Assert.Equal(qe, HairpinSynthesizer.QeFromTapAngle(phi, 50, 50), 9);
    }

    [Fact]
    public void QeBelowMinimum_IsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => HairpinSynthesizer.TapAngleFromQe(1.0, 50, 50));

    [Fact]
    public void HigherResonatorImpedance_LowersQe()
        => Assert.True(HairpinSynthesizer.QeFromTapAngle(0.4, 80, 50) < HairpinSynthesizer.QeFromTapAngle(0.4, 50, 50));
}

public class CouplingCurveTests
{
    private static readonly (double, double)[] Points =
    {
        (0.2e-3, 0.090), (0.4e-3, 0.070), (0.8e-3, 0.045), (1.6e-3, 0.020)
    };

    [Fact]
    public void Tabulated_IsExactAtKnotsAndRoundTrips()
    {
        var c = new TabulatedCouplingCurve(Points);
        foreach (var (gap, k) in Points)
        {
            Assert.Equal(k, c.K(gap), 9);
            Assert.Equal(gap, c.GapFor(k), 9);
        }
        double gMid = c.GapFor(0.05);
        Assert.InRange(gMid, 0.4e-3, 1.6e-3);
        Assert.Equal(0.05, c.K(gMid), 9);
    }

    [Fact]
    public void Tabulated_RejectsExtrapolationAndBadData()
    {
        var c = new TabulatedCouplingCurve(Points);
        Assert.Throws<ArgumentOutOfRangeException>(() => c.GapFor(0.2));
        Assert.Throws<ArgumentOutOfRangeException>(() => c.K(5e-3));
        Assert.Throws<ArgumentException>(() => new TabulatedCouplingCurve(new[] { (1e-3, 0.02), (2e-3, 0.03) }));
    }

    [Fact]
    public void Analytic_ScaleShiftsGap()
    {
        var s = Substrate.RogersRo4003();
        var plain = new AnalyticCouplingCurve(s, 1.1e-3, 1.0);
        var weak = new AnalyticCouplingCurve(s, 1.1e-3, 0.7);   // weaker coupling per gap -> need smaller gap
        Assert.True(weak.GapFor(0.05) < plain.GapFor(0.05));
        Assert.Equal(0.05, weak.K(weak.GapFor(0.05)), 6);
    }
}

public class HairpinSynthesisTests
{
    private static HairpinDesign Example(HairpinOptions? o = null, int n = 4, double bw = 200e6)
        => HairpinSynthesizer.Synthesize(Substrate.RogersRo4003(), Prototype.Chebyshev(n, 0.1), 2e9, bw, o);

    [Fact]
    public void ResonatorPath_IsHalfWavelengthMinusOpenEnds()
    {
        var d = Example();
        double expected = d.GuidedWavelength / 2.0 - 2.0 * d.OpenEndExtension;
        Assert.Equal(expected, d.PathLength, 9);

        var r = d.Resonators[0];
        double rebuilt = 2.0 * r.ArmLength + (d.ArmGap + d.ResonatorWidth);
        Assert.Equal(d.PathLength, rebuilt, 9);
    }

    [Fact]
    public void ResonatorWidth_Gives50Ohm()
    {
        var d = Example();
        Assert.Equal(50.0, Microstrip.Static(d.Substrate, d.ResonatorWidth).Z0, 3);
    }

    [Fact]
    public void Gaps_AreSymmetric_AndMatchTargetCoupling()
    {
        var d = Example();
        Assert.Equal(d.Gaps[0], d.Gaps[2], 12);
        Assert.True(d.Gaps[1] > d.Gaps[0]);   // weaker middle coupling -> wider gap

        var curve = new AnalyticCouplingCurve(d.Substrate, d.ResonatorWidth);
        for (int i = 0; i < d.Gaps.Length; i++)
            Assert.Equal(d.TargetK[i], curve.K(d.Gaps[i]), 6);
    }

    [Fact]
    public void Layout_HasNoOverlaps_AndExpectedExtent()
    {
        var d = Example();
        var rs = d.Resonators;
        for (int i = 0; i < rs.Length - 1; i++)
        {
            // Neighbouring hairpins are separated by exactly the design gap.
            Assert.Equal(d.Gaps[i], rs[i + 1].LeftArm.X0 - rs[i].RightArm.X1, 12);
            Assert.False(rs[i].LeftArm.Overlaps(rs[i + 1].LeftArm));
            Assert.False(rs[i].RightArm.Overlaps(rs[i + 1].LeftArm));
        }
        // Arms of one hairpin are separated by the arm gap.
        Assert.Equal(d.ArmGap, rs[0].RightArm.X0 - rs[0].LeftArm.X1, 12);

        double outer = d.ArmGap + 2 * d.ResonatorWidth;
        Assert.Equal(rs.Length * outer + d.Gaps.Sum(), d.TotalWidth, 9);
        Assert.Equal(rs[0].LeftArm.Y1, d.Height, 12);
    }

    [Fact]
    public void Taps_AreOnOuterArms_AtMirroredHeights()
    {
        var d = Example();
        Assert.False(d.Input.OnBase);
        Assert.Equal(d.Input.Height, d.Output.Height, 12);
        // Feed touches the outer edge of the first / last arm.
        Assert.Equal(d.Resonators[0].LeftArm.X0, d.Input.Feed.X1, 12);
        Assert.Equal(d.Resonators[^1].RightArm.X1, d.Output.Feed.X0, 12);
        // Tap lies on the arm, below the open tip.
        Assert.InRange(d.Input.Height, d.ResonatorWidth, d.Resonators[0].LeftArm.Y1);
        // Feed width gives the feed impedance.
        Assert.Equal(50.0, Microstrip.Static(d.Substrate, d.FeedWidth).Z0, 3);
    }

    [Fact]
    public void TapDistance_ReproducesTargetQe()
    {
        var d = Example();
        double beta = Math.PI / d.PathLength;
        double phi = d.Input.DistanceFromCentre * beta;
        Assert.Equal(d.QeTarget, HairpinSynthesizer.QeFromTapAngle(phi, 50, 50), 9);
    }

    [Fact]
    public void SecondResonance_IsJustBelowTwiceF0()
    {
        var d = Example();
        Assert.InRange(d.SecondResonanceHz, 1.9 * d.F0Hz, 2.0 * d.F0Hz);
    }

    [Fact]
    public void WiderBandwidth_NeedsTighterGaps()
    {
        var narrow = Example(bw: 100e6);
        var wide = Example(bw: 200e6);
        Assert.True(wide.Gaps[0] < narrow.Gaps[0]);
        // Wider band -> lower Qe -> larger tap angle -> tap farther from the fold.
        Assert.True(wide.Input.DistanceFromCentre > narrow.Input.DistanceFromCentre);
        Assert.True(wide.QeTarget < narrow.QeTarget);
    }

    [Fact]
    public void Uncalibrated_IsFlagged_AndTabulatedIsNot()
    {
        var d = Example();
        Assert.False(d.Calibrated);
        Assert.Contains(d.Warnings, w => w.Contains("calibrated"));

        var table = new TabulatedCouplingCurve(new[]
        {
            (0.1e-3, 0.12), (0.3e-3, 0.08), (0.6e-3, 0.05), (1.2e-3, 0.025), (2.4e-3, 0.01)
        });
        var c = Example(new HairpinOptions { Coupling = table });
        Assert.True(c.Calibrated);
        Assert.DoesNotContain(c.Warnings, w => w.Contains("not been calibrated"));
    }

    [Fact]
    public void MinFeature_Warning()
    {
        var d = Example(new HairpinOptions { MinFeature = 1e-3 });
        Assert.Contains(d.Warnings, w => w.Contains("minimum feature"));
    }

    [Fact]
    public void BendShortening_LengthensArms()
    {
        var plain = Example();
        var comp = Example(new HairpinOptions { BendShorteningPerCorner = 0.3 });
        Assert.True(comp.Resonators[0].ArmLength > plain.Resonators[0].ArmLength);
    }

    [Fact]
    public void Unrealisable_Coupling_IsExplained()
    {
        // 60 percent bandwidth demands far more coupling than edge-coupled lines can give.
        var ex = Assert.Throws<InvalidOperationException>(() => Example(bw: 1.2e9));
        Assert.Contains("cannot be realised", ex.Message);
    }

    [Fact]
    public void WideBandwidth_AddsWarning()
    {
        // Edge-coupled lines cannot reach k this large, so supply a (hypothetical) table that can.
        var table = new TabulatedCouplingCurve(new[] { (0.05e-3, 0.40), (0.5e-3, 0.20), (5e-3, 0.02) });
        var d = HairpinSynthesizer.Synthesize(Substrate.RogersRo4003(), Prototype.Chebyshev(3, 0.1), 2e9, 500e6,
            new HairpinOptions { Coupling = table });
        Assert.Contains(d.Warnings, w => w.Contains("wide"));
    }
}
