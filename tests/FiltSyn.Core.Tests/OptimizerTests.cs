using FiltSyn.Core;
using Xunit;

namespace FiltSyn.Core.Tests;

public class OptimizerTests
{
    private static readonly Substrate Sub = Substrate.RogersRo4003();
    private static readonly double[] G = Prototype.Chebyshev(4, 0.1);

    private static CoupledLineDesign Design()
        => CoupledLineSynthesizer.Synthesize(Sub, G, 3.6e9, 250e6);

    [Fact]
    public void Optimizer_DoesNotIncreaseErrorAndKeepsSymmetryAndLimits()
    {
        var d = Design();
        var r = CoupledLineOptimizer.Optimize(d, G, 400);
        Assert.True(r.FinalError <= r.InitialError + 1e-12);
        Assert.True(r.Evaluations > 0);
        var s = r.Design.Sections;
        Assert.Equal(d.Sections.Length, s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            var a = s[i]; var b = s[s.Length - 1 - i];
            Assert.Equal(a.Length, b.Length, 12);
            Assert.Equal(a.Gap, b.Gap, 12);
            Assert.Equal(a.Width, b.Width, 12);
            Assert.True(a.Gap >= 0.02e-3 - 1e-12);
            Assert.True(a.Width >= 0.05e-3 - 1e-12);
        }
    }

    [Fact]
    public void CircuitRfNetlist_ContainsTechnologyPortsAndOneMclinPerSection()
    {
        var d = Design();
        string cnl = CircuitRfNetlist.Write(d, "../tech/x.ctech", 3e9, 4.2e9, 61);
        Assert.Contains("technology \"../tech/x.ctech\"", cnl);
        Assert.Contains("Port:P1", cnl);
        Assert.Contains("Port:P2", cnl);
        Assert.Equal(d.Sections.Length, cnl.Split('\n').Count(l => l.StartsWith("MCLIN:")));
        Assert.Contains("analysis SP1 type=sparam", cnl);
    }
}
