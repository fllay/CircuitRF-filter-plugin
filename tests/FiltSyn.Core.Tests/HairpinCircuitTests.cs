using System.Numerics;
using FiltSyn.Core;
using Xunit;

namespace FiltSyn.Core.Tests;

/// <summary>One calibration run (a few seconds) shared by the tests below.</summary>
public sealed class HairpinFixture
{
    public Substrate Sub { get; } = Substrate.RogersRo4003();
    public double F0 { get; } = 2e9;
    public double W { get; }
    public double A { get; }
    public double Dl { get; }
    public HairpinCalibration Cal { get; }
    public double[] G { get; } = Prototype.Chebyshev(4, 0.1);

    public HairpinFixture()
    {
        W = Microstrip.WidthForZ0(Sub, 50.0);
        A = 2.0 * W;
        Dl = HairpinSynthesizer.OpenEndExtension(Sub, W);
        Cal = HairpinCalibration.Run(Sub, F0, W, A, Dl);
    }

    public HairpinDesign Design(double bw = 25e6)
        => HairpinSynthesizer.SynthesizeCalibrated(Sub, G, F0, bw, null, Cal);
}

public class NelderMeadTests
{
    [Fact]
    public void Quadratic_Converges()
    {
        var r = NelderMead.Minimize(x => Math.Pow(x[0] - 3, 2) + 2 * Math.Pow(x[1] + 1, 2), new[] { 0.0, 0.0 }, 0.5);
        Assert.Equal(3.0, r.X[0], 4);
        Assert.Equal(-1.0, r.X[1], 4);
        Assert.True(r.Value < 1e-8);
    }

    [Fact]
    public void Rosenbrock_Converges()
    {
        var r = NelderMead.Minimize(
            x => 100 * Math.Pow(x[1] - x[0] * x[0], 2) + Math.Pow(1 - x[0], 2),
            new[] { -1.2, 1.0 }, 0.3, 1e-14, 5000);
        Assert.Equal(1.0, r.X[0], 3);
        Assert.Equal(1.0, r.X[1], 3);
    }
}

public class ResponseMetricsTests
{
    [Fact]
    public void Metrics_OnSyntheticParabola()
    {
        // S21 = -(f - 2)^2 * 100 dB (f in GHz): peak 0 dB at 2 GHz, -3.0103 dB at |f - 2| = sqrt(0.030103).
        var f = Enumerable.Range(0, 2001).Select(i => 1.5e9 + i * 1e6).ToArray();
        var s21 = f.Select(x => -100.0 * Math.Pow((x - 2e9) / 1e9, 2)).ToArray();
        var s11 = f.Select(_ => -20.0).ToArray();
        var m = HairpinCircuitModel.Metrics(f, s21, s11);
        double half = Math.Sqrt(0.030103) * 1e9;
        Assert.Equal(0.0, m.PeakS21dB, 6);
        double geoCenter = Math.Sqrt((2e9 - half) * (2e9 + half));   // centre is the geometric mean of the -3 dB points
        Assert.InRange(m.CenterHz, geoCenter - 1e4, geoCenter + 1e4);
        Assert.InRange(m.Bandwidth3dBHz, 2 * half - 1e4, 2 * half + 1e4);
        Assert.Equal(20.0, m.WorstInBandReturnLossDb, 6);
    }
}

public class HairpinCircuitTests : IClassFixture<HairpinFixture>
{
    private readonly HairpinFixture _fx;
    public HairpinCircuitTests(HairpinFixture fx) => _fx = fx;

    private static double Pwr((Complex S11, Complex S21) s) => s.S11.Magnitude * s.S11.Magnitude + s.S21.Magnitude * s.S21.Magnitude;

    [Fact]
    public void LosslessCircuit_ConservesPower_ArmTap()
    {
        var d = _fx.Design();
        Assert.False(d.Input.OnBase);
        var m = new HairpinCircuitModel(d);
        foreach (double f in new[] { 1.9e9, 1.99e9, 2.0e9, 2.01e9, 2.2e9, 3.5e9 })
            Assert.Equal(1.0, Pwr(m.Evaluate(f)), 9);
    }

    [Fact]
    public void LosslessCircuit_ConservesPower_BaseTap_And_SingleResonator()
    {
        // Tap close to the fold centre puts the feed on the base; Order = 1 puts both taps on one fold.
        double path = 2 * _fx.Cal.TunedArmLength + _fx.A + _fx.W;
        var two = new HairpinGeometry(_fx.Sub, 2, _fx.W, _fx.A, _fx.Cal.TunedArmLength, new[] { 2.5e-3 }, 0.4e-3, _fx.Dl, 50);
        var one = new HairpinGeometry(_fx.Sub, 1, _fx.W, _fx.A, _fx.Cal.TunedArmLength, Array.Empty<double>(), 0.4e-3, _fx.Dl, 50);
        foreach (var g in new[] { two, one })
        {
            var m = new HairpinCircuitModel(g);
            foreach (double f in new[] { 1.95e9, 2.0e9, 2.05e9 })
                Assert.Equal(1.0, Pwr(m.Evaluate(f)), 9);
            Assert.Equal(1.0, m.InputReflection(2.0e9).Magnitude, 9);   // lossless one-port
        }
    }

    [Fact]
    public void Loss_ReducesTransmission_ConsistentWithUnloadedQ()
    {
        var d = _fx.Design();
        var lossless = HairpinVerification.Run(d, _fx.G, lossy: false);
        var lossy = HairpinVerification.Run(d, _fx.G, lossy: true);
        Assert.True(lossy.Circuit.PeakS21dB < lossless.Circuit.PeakS21dB - 1.0);

        // Order-of-magnitude check: IL ~ 4.343 * sum(g) / (FBW * Qu).
        double fbw = d.BandwidthHz / d.F0Hz;
        double qu = Microstrip.UnloadedQ(d.Substrate, d.ResonatorWidth, d.F0Hz);
        double est = 4.343 * _fx.G.Skip(1).Take(4).Sum() / (fbw * qu);
        double il = -lossy.Circuit.PeakS21dB;
        Assert.InRange(il / est, 0.6, 1.3);
    }

    [Fact]
    public void Calibration_TunesSingleHairpinToF0()
    {
        var g = new HairpinGeometry(_fx.Sub, 1, _fx.W, _fx.A, _fx.Cal.TunedArmLength, Array.Empty<double>(),
            0.35 * (2 * _fx.Cal.TunedArmLength + _fx.A + _fx.W), _fx.Dl, 50);
        var roots = new HairpinCircuitModel(g).NaturalFrequencies(1.5e9, 2.5e9, 4000);
        Assert.InRange(roots[0], _fx.F0 - 1e3, _fx.F0 + 1e3);   // within 1 kHz
    }

    [Fact]
    public void TunedArm_IsSlightlyLongerThanSingleLineEstimate()
    {
        // Arm-to-arm self-coupling raises the resonance, so the arms must be a little longer than the
        // single-line half-wavelength estimate.
        var closed = HairpinSynthesizer.Synthesize(_fx.Sub, _fx.G, _fx.F0, 25e6, new HairpinOptions
        {
            Coupling = _fx.Cal.CouplingCurve   // any curve covering the needed k; only the arm length matters here
        });
        double ratio = _fx.Cal.TunedArmLength / closed.ArmLength;
        Assert.InRange(ratio, 1.0, 1.05);
    }

    [Theory]
    [InlineData(1.2e-3)]
    [InlineData(2.5e-3)]
    [InlineData(4e-3)]
    [InlineData(8e-3)]
    [InlineData(15e-3)]
    public void CircuitQe_AgreesWithClosedFormTapFormula(double tap)
    {
        // Two independent routes: stored-energy derivation (closed form) vs group delay of the circuit model.
        // Compare in electrical coordinates: each fold corner is shorter than its centreline by the corner correction.
        double c = HairpinGeometry.DefaultCornerShortening(_fx.Sub, _fx.W);
        double half = (_fx.A + _fx.W) / 2.0;
        double path = 2 * _fx.Cal.TunedArmLength + _fx.A + _fx.W - 2 * c;
        double te = tap <= half - c ? tap : tap <= half ? half - c : tap - c;
        double formula = HairpinSynthesizer.QeFromTapAngle(te * Math.PI / path, 50, 50);
        double circuit = _fx.Cal.Qe(tap);
        Assert.InRange(circuit / formula, 0.95, 1.05);
    }

    [Fact]
    public void TapDistanceForQe_RoundTrips()
    {
        double t = _fx.Cal.TapDistanceForQe(60.0);
        Assert.Equal(60.0, _fx.Cal.Qe(t), 3);
    }

    [Fact]
    public void CouplingCurve_IsMonotonic_WithinKnownMaximum()
    {
        Assert.InRange(_fx.Cal.MaxCoupling, 0.005, 0.2);
        double prev = double.MaxValue;
        foreach (double gap in new[] { 0.5e-3, 0.8e-3, 1.2e-3, 2e-3, 3e-3, 5e-3 })
        {
            if (gap < _fx.Cal.GapAtMaxCoupling) continue;
            double k = _fx.Cal.CouplingCurve.K(gap);
            Assert.True(k <= _fx.Cal.MaxCoupling + 1e-12);
            Assert.True(k < prev);
            prev = k;
        }
    }

    [Fact]
    public void ExtractedCoupling_IsFarBelowTheFullyCoupledPairEstimate()
    {
        // Same-orientation hairpins: electric and magnetic parts oppose, so k is far below the straight-pair figure.
        double pair = CoupledMicrostrip.Static(_fx.Sub, _fx.W, 0.4e-3).CouplingCoefficient;
        Assert.True(_fx.Cal.MaxCoupling < 0.5 * pair);
    }

    [Fact]
    public void SynthesizeCalibrated_ReportsUnreachableBandwidth()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _fx.Design(200e6));
        Assert.Contains("exceeds", ex.Message);
        Assert.Contains("fractional bandwidth", ex.Message);
    }

    [Fact]
    public void SynthesizeCalibrated_ProducesFlaggedSymmetricDesign()
    {
        var d = _fx.Design();
        Assert.True(d.Calibrated);
        Assert.Contains("circuit", d.CalibrationSource);
        Assert.Equal(d.Gaps[0], d.Gaps[2], 12);
        Assert.Contains(d.Warnings, w => w.Contains("full-wave EM"));
        Assert.Equal(2 * d.ArmLength + d.BaseLength, d.PathLength, 12);
        for (int i = 0; i < d.Gaps.Length; i++)
            Assert.Equal(d.TargetK[i], _fx.Cal.CouplingCurve.K(d.Gaps[i]), 6);
    }

    [Fact]
    public void WithGeometry_RebuildsLayoutConsistently()
    {
        var d = _fx.Design();
        var e = d.WithGeometry(d.ArmLength + 0.5e-3, d.Gaps.Select(x => x * 1.1).ToArray(), d.TapDistance);
        Assert.Equal(d.ArmLength + 0.5e-3, e.ArmLength, 12);
        Assert.Equal(2 * e.ArmLength + e.BaseLength, e.PathLength, 12);
        Assert.Equal(e.Resonators[0].LeftArm.Y1, e.Height, 12);
        Assert.Equal(e.Gaps[1], e.Resonators[2].LeftArm.X0 - e.Resonators[1].RightArm.X1, 12);
    }

    [Fact]
    public void CenterResponse_PutsPassbandOnF0_AndMatchesIdealShape()
    {
        var d = _fx.Design();
        var before = HairpinVerification.Run(d, _fx.G);
        var tuned = HairpinTuner.CenterResponse(d);
        var after = HairpinVerification.Run(tuned, _fx.G);

        Assert.True(after.RmsS21Error < before.RmsS21Error);
        Assert.InRange(Math.Abs(after.Circuit.CenterHz - _fx.F0), 0, 0.02 * d.BandwidthHz);
        Assert.InRange(after.Circuit.Bandwidth3dBHz / after.Ideal.Bandwidth3dBHz, 0.95, 1.05);
        Assert.True(after.RmsS21Error < 0.03);
        Assert.True(after.RmsS11Error < 0.05);
    }

    [Fact]
    public void Optimize_NeverWorsensTheError()
    {
        var d = HairpinTuner.CenterResponse(_fx.Design());
        var r = HairpinTuner.Optimize(d, _fx.G, maxEvaluations: 150);
        Assert.True(r.FinalError <= r.InitialError + 1e-12);
        foreach (var gap in r.Design.Gaps) Assert.True(gap > 0);
    }
}
