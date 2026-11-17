using FiltSyn.Core;
using Xunit;
using Xunit.Abstractions;

namespace FiltSyn.Core.Tests;

public class StriplineFilterTests
{
    private readonly ITestOutputHelper _o;
    public StriplineFilterTests(ITestOutputHelper o) => _o = o;

    // RT/duroid 5880 stripline, 60 mil plane spacing.
    private static readonly Substrate Sub = Substrate.RogersRt5880(1.524e-3).AsStripline();
    private static readonly double[] G = Prototype.Chebyshev(4, 0.1);

    [Fact]
    public void CoupledLine_Stripline_TunesOntoPrototype()
    {
        var d = CoupledLineTuner.CenterResponse(CoupledLineSynthesizer.Synthesize(Sub, G, 3e9, 300e6));
        var v = CoupledLineVerification.Run(d, G);
        _o.WriteLine($"centre {v.Circuit.CenterHz / 1e9:F4} GHz  bw {v.Circuit.Bandwidth3dBHz / 1e6:F1} MHz vs {v.Ideal.Bandwidth3dBHz / 1e6:F1}  rms {v.RmsS21Error:F4}");
        foreach (var s in d.Sections) _o.WriteLine($"w {s.Width * 1e3:F3} gap {s.Gap * 1e3:F3} len {s.Length * 1e3:F3}");
        Assert.InRange(v.Circuit.CenterHz, 3e9 - 3e6, 3e9 + 3e6);
        Assert.InRange(v.Circuit.Bandwidth3dBHz / v.Ideal.Bandwidth3dBHz, 0.93, 1.07);
        Assert.True(v.RmsS21Error < 0.05, $"rms {v.RmsS21Error}");
    }

    private static (HairpinCalibration Cal, double W) HairpinCal()
    {
        double w = Microstrip.WidthForZ0(Sub, 50.0);
        double dl = HairpinSynthesizer.OpenEndExtension(Sub, w);
        return (HairpinCalibration.Run(Sub, 3e9, w, 2 * w, dl), w);
    }

    [Fact]
    public void Hairpin_Stripline_SynthesisMatchesPrototype()
    {
        var (cal, _) = HairpinCal();
        var d = HairpinTuner.CenterResponse(HairpinSynthesizer.SynthesizeCalibrated(Sub, G, 3e9, 30e6, null, cal));
        var v = HairpinVerification.Run(d, G);
        _o.WriteLine($"centre {v.Circuit.CenterHz / 1e9:F4} bw {v.Circuit.Bandwidth3dBHz / 1e6:F2} vs {v.Ideal.Bandwidth3dBHz / 1e6:F2} rms {v.RmsS21Error:F4}");
        Assert.InRange(v.Circuit.CenterHz, 3e9 - 1.5e6, 3e9 + 1.5e6);
        Assert.InRange(v.Circuit.Bandwidth3dBHz / v.Ideal.Bandwidth3dBHz, 0.85, 1.15);
        Assert.True(v.RmsS21Error < 0.08, $"rms {v.RmsS21Error}");
    }

    [Fact]
    public void Hairpin_Stripline_CouplingReachIsLimitedAndReported()
    {
        var (cal, _) = HairpinCal();
        var ok = HairpinSynthesizer.SynthesizeCalibrated(Sub, G, 3e9, 60e6, null, cal);
        Assert.True(ok.Gaps[0] < ok.Gaps[1]);
        var ex = Assert.ThrowsAny<Exception>(() => HairpinSynthesizer.SynthesizeCalibrated(Sub, G, 3e9, 300e6, null, cal));
        Assert.Contains("exceeds", ex.Message);
    }
}
