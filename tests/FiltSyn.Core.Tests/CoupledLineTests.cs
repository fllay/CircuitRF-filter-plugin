using FiltSyn.Core;
using Xunit;

namespace FiltSyn.Core.Tests;

public class CoupledLineTests
{
    private static readonly Substrate Sub = Substrate.RogersRo4003();
    private static readonly double[] G = Prototype.Chebyshev(4, 0.1);

    [Fact]
    public void EvenOddImpedances_FollowJInverterFormulas()
    {
        double fbw = 0.1;
        var z = CoupledLineSynthesizer.EvenOddImpedances(G, fbw, 50.0);
        Assert.Equal(5, z.Length);
        double j0 = Math.Sqrt(Math.PI * fbw / (2 * G[0] * G[1]));
        Assert.Equal(50 * (1 + j0 + j0 * j0), z[0].Z0e, 9);
        Assert.Equal(50 * (1 - j0 + j0 * j0), z[0].Z0o, 9);
        double j1 = Math.PI * fbw / 2 / Math.Sqrt(G[1] * G[2]);
        Assert.Equal(50 * (1 + j1 + j1 * j1), z[1].Z0e, 9);
        // Symmetric prototype -> symmetric sections.
        Assert.Equal(z[0].Z0e, z[4].Z0e, 9);
        Assert.Equal(z[1].Z0o, z[3].Z0o, 9);
        // Z0e + Z0o is above 2 Z0 and Z0e > Z0o.
        foreach (var (ze, zo) in z) Assert.True(ze > 50 && zo < 50);
    }

    [Fact]
    public void WidthGapSolver_ReproducesTargets()
    {
        foreach (var (ze, zo) in new[] { (75.9, 38.3), (57.4, 44.3), (52.7, 47.6) })
        {
            var (w, gap) = CoupledLineSynthesizer.SolveWidthGap(Sub, ze, zo, 0.1, 1);
            var p = CoupledMicrostrip.Static(Sub, w, gap);
            Assert.Equal(ze, p.Z0e, 2);
            Assert.Equal(zo, p.Z0o, 2);
        }
    }

    [Fact]
    public void Synthesis_GivesQuarterWaveSectionsAndMonotonicGaps()
    {
        var d = CoupledLineSynthesizer.Synthesize(Sub, G, 2e9, 200e6);
        Assert.Equal(4, d.Order);
        Assert.Equal(5, d.Sections.Length);
        // Quarter wave at 2 GHz on RO4003 is about 22-23 mm.
        foreach (var s in d.Sections) Assert.InRange(s.Length * 1e3, 21.0, 24.5);
        // Outer sections couple most tightly, the middle one least.
        Assert.True(d.Sections[0].Gap < d.Sections[1].Gap);
        Assert.True(d.Sections[1].Gap < d.Sections[2].Gap);
        Assert.Equal(d.Sections[0].Gap, d.Sections[4].Gap, 9);
        Assert.Contains(d.Warnings, w => w.Contains("0.080") || w.Contains("minimum feature"));
    }

    [Fact]
    public void Solver_RejectsImpossibleImpedances()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CoupledLineSynthesizer.SolveWidthGap(Sub, 400.0, 8.0, 0.5, 3));
        Assert.Contains("Section 3", ex.Message);
    }

    [Fact]
    public void Synthesis_WarnsAboutVeryNarrowBandwidth()
    {
        var d = CoupledLineSynthesizer.Synthesize(Sub, G, 2e9, 10e6);
        Assert.Contains(d.Warnings, w => w.Contains("narrow for a coupled-line"));
    }

    [Fact]
    public void CircuitModel_IsPassiveAndReciprocalForLosslessSweep()
    {
        var d = CoupledLineSynthesizer.Synthesize(Sub, G, 2e9, 200e6);
        var m = new CoupledLineCircuitModel(d);
        foreach (double f in new[] { 1.7e9, 1.95e9, 2.0e9, 2.1e9, 2.4e9, 3.9e9 })
        {
            var (s11, s21) = m.Evaluate(f);
            Assert.InRange(s11.Magnitude * s11.Magnitude + s21.Magnitude * s21.Magnitude, 0.999, 1.001);
        }
    }

    [Fact]
    public void CircuitModel_LossReducesTransmission()
    {
        var d = CoupledLineSynthesizer.Synthesize(Sub, G, 2e9, 200e6);
        double lossless = new CoupledLineCircuitModel(d).Evaluate(2e9).S21.Magnitude;
        double lossy = new CoupledLineCircuitModel(d, true).Evaluate(2e9).S21.Magnitude;
        Assert.True(lossy < lossless);
        Assert.InRange(20 * Math.Log10(lossy), -3.0, -0.05);
    }

    [Fact]
    public void Tuned_ResponseMatchesIdealPrototype()
    {
        var d = CoupledLineTuner.CenterResponse(CoupledLineSynthesizer.Synthesize(Sub, G, 2e9, 200e6));
        var v = CoupledLineVerification.Run(d, G);
        Assert.InRange(v.Circuit.CenterHz, 2e9 - 0.01 * 200e6, 2e9 + 0.01 * 200e6);
        Assert.InRange(v.Circuit.Bandwidth3dBHz / v.Ideal.Bandwidth3dBHz, 0.95, 1.05);
        Assert.True(v.RmsS21Error < 0.03, $"rms {v.RmsS21Error}");
    }

    [Fact]
    public void CenterResponse_MovesBandOntoF0()
    {
        var raw = CoupledLineSynthesizer.Synthesize(Sub, G, 2e9, 200e6);
        var tuned = CoupledLineTuner.CenterResponse(raw);
        double Centre(CoupledLineDesign x)
        {
            var f = Enumerable.Range(0, 801).Select(i => 1.5e9 + 1e9 * i / 800).ToArray();
            var (s11, s21) = new CoupledLineCircuitModel(x).Sweep(f);
            return HairpinCircuitModel.Metrics(f, s21, s11).CenterHz;
        }
        Assert.True(Math.Abs(Centre(tuned) - 2e9) < Math.Abs(Centre(raw) - 2e9));
        Assert.InRange(Math.Abs(Centre(tuned) - 2e9), 0, 2e6);
    }

    [Fact]
    public void Layout_IsAStaircaseWithPortsOnTheBoardEdge()
    {
        var d = CoupledLineTuner.CenterResponse(CoupledLineSynthesizer.Synthesize(Sub, G, 2e9, 200e6));
        var l = CoupledLineLayout.From(d);
        // N+2 strips + 2 feeds + board.
        Assert.Equal(d.Order + 2 + 2, l.Polygons.Count(p => p.Layer == FilterLayout.CopperLayer));
        Assert.Single(l.Polygons, p => p.Layer == FilterLayout.BoardLayer);
        Assert.Equal(l.Board.X0, l.Ports[0].X, 12);
        Assert.Equal(l.Board.X1, l.Ports[1].X, 12);
        // Strip centres step upward by the section pitches.
        var strips = l.Polygons.Where(p => p.Layer == FilterLayout.CopperLayer).Take(d.Order + 2).ToArray();
        for (int k = 0; k < strips.Length - 1; k++)
        {
            double c0 = (strips[k].Points.Max(p => p.Y) + strips[k].Points.Min(p => p.Y)) / 2;
            double c1 = (strips[k + 1].Points.Max(p => p.Y) + strips[k + 1].Points.Min(p => p.Y)) / 2;
            Assert.Equal(d.Sections[k].Pitch, c1 - c0, 9);
        }
        // Adjacent strips are separated by the section gap.
        double lowTop = strips[0].Points.Max(p => p.Y);
        double xEnd = strips[0].Points.Max(p => p.X);
        double highBottom = strips[1].Points.Where(p => p.X < xEnd - 1e-9).Min(p => p.Y);
        Assert.Equal(d.Sections[0].Gap, highBottom - lowTop, 9);
        foreach (var p in l.Polygons)
            foreach (var (px, py) in p.Points)
            {
                Assert.InRange(px, l.Board.X0 - 1e-12, l.Board.X1 + 1e-12);
                Assert.InRange(py, l.Board.Y0 - 1e-12, l.Board.Y1 + 1e-12);
            }
    }

    [Fact]
    public void Dxf_WritesTheCoupledLayout()
    {
        var d = CoupledLineSynthesizer.Synthesize(Sub, G, 2e9, 200e6);
        var l = CoupledLineLayout.From(d);
        string dxf = DxfWriter.Write(l);
        var lines = dxf.Split('\n');
        Assert.Equal(l.Polygons.Count, lines.Count(s => s == "SEQEND"));
        Assert.Equal(l.Polygons.Sum(p => p.Points.Length), lines.Count(s => s == "VERTEX"));
    }
}
