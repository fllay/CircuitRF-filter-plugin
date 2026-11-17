namespace FiltSyn.Core;

/// <summary>
/// Symmetric stripline (strip centred between two ground planes spaced <see cref="Substrate.H"/> apart, homogeneous
/// dielectric). Zero-thickness Z0 and even/odd modes are Cohn's exact elliptic-integral results; strip thickness is
/// folded in through Wheeler's effective-width increment. The medium is homogeneous, so epsilon_eff = epsilon_r and
/// there is no dispersion.
/// </summary>
public static class Stripline
{
    /// <summary>Wheeler effective-width increment for a strip of thickness t between planes b apart (m).</summary>
    public static double WidthIncrement(Substrate s, double w)
    {
        double b = s.H, t = s.T;
        if (t <= 0) return 0.0;
        double x = t / b;
        if (x >= 0.5) x = 0.5; // outside the formula's range; clamp
        double m = 2.0 / (1.0 + 2.0 * x / (3.0 * (1.0 - x)));
        double inner = Math.Pow(x / (2.0 - x), 2) + Math.Pow(0.0796 * x / (w / b + 1.1 * x), m);
        return t * x / (Math.PI * (1.0 - x)) * (1.0 - 0.5 * Math.Log(inner));
    }

    /// <summary>Static characteristic impedance; epsilon_eff equals epsilon_r.</summary>
    public static (double Z0, double EpsEff) Static(Substrate s, double w)
    {
        double a = Math.PI * (w + WidthIncrement(s, w)) / (2.0 * s.H);
        return (Eta0Over4 / Math.Sqrt(s.Er) * KRatio(Math.Tanh(a), 1.0 / Math.Cosh(a)), s.Er);
    }

    private const double Eta0Over4 = Microstrip.Eta0 / 4.0; // 30 pi

    /// <summary>Even/odd-mode data of two identical coupled striplines with edge-to-edge gap.</summary>
    public static CoupledProperties Coupled(Substrate s, double w, double gap)
    {
        double dw = WidthIncrement(s, w);
        double we = w + dw, ge = Math.Max(gap - dw, 1e-9 * s.H);
        double a = Math.PI * we / (2.0 * s.H);
        double sg = Math.PI * (we + ge) / (2.0 * s.H);
        double ta = Math.Tanh(a), ts = Math.Tanh(sg);
        double ke = ta * ts;
        double ko = ta / ts;
        double pre = Eta0Over4 / Math.Sqrt(s.Er);
        double z0e = pre * KRatio(ke, Math.Sqrt((1.0 - ke) * (1.0 + ke)));
        double z0o = pre * KRatio(ko, Math.Sqrt((1.0 - ko) * (1.0 + ko)));
        return new CoupledProperties(z0e, z0o, s.Er, s.Er) { Homogeneous = true };
    }

    /// <summary>Attenuation of a single stripline at f (Np/m): Cohn/Pozar conductor-loss forms plus dielectric loss.</summary>
    public static LineProperties Analyze(Substrate s, double w, double fHz)
    {
        var (z0, er) = Static(s, w);
        double b = s.H, t = Math.Max(s.T, 1e-9);
        double rs = Math.Sqrt(Math.PI * fHz * 4e-7 * Math.PI / s.Sigma);
        double delta = Math.Sqrt(1.0 / (Math.PI * fHz * 4e-7 * Math.PI * s.Sigma));
        double rough = 1.0 + 2.0 / Math.PI * Math.Atan(1.4 * Math.Pow(s.RoughnessRms / delta, 2));
        double alphaC;
        if (Math.Sqrt(er) * z0 < 120.0)
        {
            double A = 1.0 + 2.0 * w / (b - t) + (b + t) / (Math.PI * (b - t)) * Math.Log((2.0 * b - t) / t);
            alphaC = 2.7e-3 * rs * rough * er * z0 / (30.0 * Math.PI * (b - t)) * A;
        }
        else
        {
            double B = 1.0 + b / (0.5 * w + 0.7 * t) * (0.5 + 0.414 * t / w + Math.Log(4.0 * Math.PI * w / t) / (2.0 * Math.PI));
            alphaC = 0.16 * rs * rough / (z0 * b) * B;
        }
        double alphaD = Math.PI * fHz * Math.Sqrt(er) * s.TanD / Microstrip.C0;
        return new LineProperties(z0, er, er, alphaC, alphaD);
    }

    /// <summary>
    /// Open-end length extension (m): the edge fringing of a strip between planes (about 0.22 b of extra width per
    /// edge, from Wheeler's wide-strip result) divided by the line's capacitance per unit length.
    /// </summary>
    public static double OpenEndExtension(Substrate s, double w)
    {
        double b = s.H;
        double we = w + WidthIncrement(s, w);
        return 0.2205 * b * we / (we + 0.441 * b);
    }

    /// <summary>K'(k)/K(k) via the arithmetic-geometric mean, given k and the complementary modulus k'.</summary>
    internal static double KRatio(double k, double kPrime) => Agm(1.0, kPrime) / Agm(1.0, k);

    private static double Agm(double a, double g)
    {
        for (int i = 0; i < 40; i++)
        {
            double an = 0.5 * (a + g), gn = Math.Sqrt(a * g);
            if (Math.Abs(an - gn) < 1e-15 * an) return an;
            a = an; g = gn;
        }
        return 0.5 * (a + g);
    }
}
