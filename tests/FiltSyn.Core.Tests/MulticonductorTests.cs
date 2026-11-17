using System.Numerics;
using FiltSyn.Core;
using Xunit;

namespace FiltSyn.Core.Tests;

public class LinalgTests
{
    private static double[,] SamplePd() => new double[,] { { 4, 1, 0.5 }, { 1, 3, 0.2 }, { 0.5, 0.2, 2 } };

    [Fact]
    public void Cholesky_Reconstructs()
    {
        var a = SamplePd();
        var l = Linalg.Cholesky(a);
        var r = Linalg.Mul(l, Linalg.Transpose(l));
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Assert.Equal(a[i, j], r[i, j], 12);
    }

    [Fact]
    public void LowerInverse_And_GeneralInverse_AgreeWithIdentity()
    {
        var a = SamplePd();
        var l = Linalg.Cholesky(a);
        var id1 = Linalg.Mul(l, Linalg.InvertLower(l));
        var id2 = Linalg.Mul(a, Linalg.Invert(a));
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double e = i == j ? 1 : 0;
                Assert.Equal(e, id1[i, j], 12);
                Assert.Equal(e, id2[i, j], 12);
            }
    }

    [Fact]
    public void SymmetricEigen_ReconstructsAndIsOrthogonal()
    {
        var a = SamplePd();
        var (vals, v) = Linalg.SymmetricEigen(a);
        var d = new double[3, 3];
        for (int i = 0; i < 3; i++) d[i, i] = vals[i];
        var rec = Linalg.Mul(Linalg.Mul(v, d), Linalg.Transpose(v));
        var vtv = Linalg.Mul(Linalg.Transpose(v), v);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                Assert.Equal(a[i, j], rec[i, j], 10);
                Assert.Equal(i == j ? 1.0 : 0.0, vtv[i, j], 10);
            }
    }

    [Fact]
    public void ComplexSolve_SolvesSystem()
    {
        var a = new Complex[,] { { new(2, 1), new(0, -1) }, { new(1, 0), new(3, 2) } };
        var x = new Complex[,] { { new(1, 2) }, { new(-1, 0.5) } };
        var b = new Complex[2, 1];
        for (int i = 0; i < 2; i++) b[i, 0] = a[i, 0] * x[0, 0] + a[i, 1] * x[1, 0];
        var sol = Linalg.Solve(a, b);
        Assert.True((sol[0, 0] - x[0, 0]).Magnitude < 1e-12);
        Assert.True((sol[1, 0] - x[1, 0]).Magnitude < 1e-12);
    }
}

public class MulticonductorLineTests
{
    private static Complex[,] ToS(Complex[,] y, double z0)
    {
        int n = y.GetLength(0);
        var plus = new Complex[n, n];
        var minus = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                var id = i == j ? Complex.One : Complex.Zero;
                plus[i, j] = id + z0 * y[i, j];
                minus[i, j] = id - z0 * y[i, j];
            }
        return Linalg.Solve(plus, minus);
    }

    [Fact]
    public void HomogeneousCoupler_MatchesTextbookAtQuarterWave()
    {
        // Contra-directional coupled-line coupler: at theta = 90 deg, with Zc = sqrt(Z0e Z0o), the coupled-port
        // amplitude is C = (Z0e - Z0o)/(Z0e + Z0o), through = sqrt(1 - C^2), isolation and match are perfect.
        const double v = 2e8, z0e = 70, z0o = 40, f = 1e9;
        double ce = 1 / (v * z0e), co = 1 / (v * z0o);
        double cg = ce, cm = (co - ce) / 2;
        var c = new double[,] { { cg + cm, -cm }, { -cm, cg + cm } };
        var l = Linalg.Invert(c);
        for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) l[i, j] /= v * v;

        var line = MulticonductorLine.FromLC(l, c);
        var (y11, y12) = line.Y(v / (4 * f), f);

        // Port order: a_near, b_near, a_far, b_far.
        var y = new Complex[4, 4];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                y[i, j] = y11[i, j]; y[i + 2, j + 2] = y11[i, j];
                y[i, j + 2] = y12[i, j]; y[i + 2, j] = y12[i, j];
            }
        var s = ToS(y, Math.Sqrt(z0e * z0o));
        double cc = (z0e - z0o) / (z0e + z0o);

        Assert.True(s[0, 0].Magnitude < 1e-9);                         // input match
        Assert.Equal(cc, s[1, 0].Magnitude, 9);                        // coupled (same end)
        Assert.Equal(Math.Sqrt(1 - cc * cc), s[2, 0].Magnitude, 9);    // through
        Assert.True(s[3, 0].Magnitude < 1e-9);                         // isolated
    }

    [Fact]
    public void InhomogeneousPair_MatchesEvenOddFormulas()
    {
        var sub = Substrate.RogersRo4003();
        double w = 1.1e-3, gap = 0.3e-3, len = 10e-3, f = 2e9;
        var pr = CoupledMicrostrip.Static(sub, w, gap);

        var line = MulticonductorLine.FromMicrostrip(sub, w, new[] { gap });
        var (y11, y12) = line.Y(len, f);

        Complex Y11(double z0, double eps)
        {
            double th = 2 * Math.PI * f * Math.Sqrt(eps) * len / Microstrip.C0;
            return -Complex.ImaginaryOne / z0 * Complex.Cos(th) / Complex.Sin(th);
        }
        Complex Y12(double z0, double eps)
        {
            double th = 2 * Math.PI * f * Math.Sqrt(eps) * len / Microstrip.C0;
            return Complex.ImaginaryOne / z0 / Complex.Sin(th);
        }
        var y11e = Y11(pr.Z0e, pr.EpsEffEven); var y11o = Y11(pr.Z0o, pr.EpsEffOdd);
        var y12e = Y12(pr.Z0e, pr.EpsEffEven); var y12o = Y12(pr.Z0o, pr.EpsEffOdd);

        void Close(Complex expected, Complex actual)
            => Assert.True((expected - actual).Magnitude < 1e-9 * Math.Max(1.0, expected.Magnitude), $"{expected} vs {actual}");

        Close((y11e + y11o) / 2, y11[0, 0]);
        Close((y11e + y11o) / 2, y11[1, 1]);
        Close((y11e - y11o) / 2, y11[0, 1]);
        Close((y12e + y12o) / 2, y12[0, 0]);
        Close((y12e - y12o) / 2, y12[0, 1]);
    }

    [Fact]
    public void SingleConductor_IsATransmissionLine()
    {
        var sub = Substrate.Fr4();
        double w = 3.0e-3, len = 12e-3, f = 1.5e9;
        var (z0, e0) = Microstrip.Static(sub, w);
        var (y11, y12) = MulticonductorLine.FromMicrostrip(sub, w, Array.Empty<double>()).Y(len, f);
        double th = 2 * Math.PI * f * Math.Sqrt(e0) * len / Microstrip.C0;
        var expected11 = -Complex.ImaginaryOne / z0 * Complex.Cos(th) / Complex.Sin(th);
        var expected12 = Complex.ImaginaryOne / z0 / Complex.Sin(th);
        Assert.True((y11[0, 0] - expected11).Magnitude < 1e-9 * expected11.Magnitude);
        Assert.True((y12[0, 0] - expected12).Magnitude < 1e-9 * expected12.Magnitude);
    }

    [Fact]
    public void ThreeConductors_AreReciprocalAndDecoupleWhenFarApart()
    {
        var sub = Substrate.RogersRo4003();
        var near = MulticonductorLine.FromMicrostrip(sub, 1.1e-3, new[] { 0.3e-3, 0.6e-3 });
        var (a, b) = near.Y(8e-3, 2e9);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                Assert.True((a[i, j] - a[j, i]).Magnitude < 1e-12 * (1 + a[i, j].Magnitude));
                Assert.True((b[i, j] - b[j, i]).Magnitude < 1e-12 * (1 + b[i, j].Magnitude));
            }

        var far = MulticonductorLine.FromMicrostrip(sub, 1.1e-3, new[] { 40e-3, 40e-3 });
        var (fa, _) = far.Y(8e-3, 2e9);
        Assert.True(fa[0, 1].Magnitude < 1e-3 * fa[0, 0].Magnitude);
        Assert.True(fa[0, 2].Magnitude < 1e-3 * fa[0, 0].Magnitude);
    }

    [Fact]
    public void Loss_AddsRealPartToSeriesAdmittance()
    {
        var sub = Substrate.RogersRo4003();
        var line = MulticonductorLine.FromMicrostrip(sub, 1.1e-3, new[] { 0.3e-3 });
        var (lossless, _) = line.Y(20e-3, 2e9, 0.0);
        var (lossy, _) = line.Y(20e-3, 2e9, 2.0);
        Assert.True(Math.Abs(lossless[0, 0].Real) < 1e-12);
        Assert.True(lossy[0, 0].Real > 0);
    }
}
