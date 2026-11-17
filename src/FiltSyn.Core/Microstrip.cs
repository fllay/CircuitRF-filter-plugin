namespace FiltSyn.Core;

/// <summary>Microstrip substrate and conductor description. All lengths in metres.</summary>
public sealed record Substrate(
    double Er,
    double H,
    double T = 35e-6,
    double TanD = 0.0,
    double Sigma = 5.8e7,
    double RoughnessRms = 0.0,
    bool IsStripline = false)
{
    /// <summary>The same material as a stripline: H becomes the ground-plane spacing b, the strip sits at the centre.</summary>
    public Substrate AsStripline() => this with { IsStripline = true };
    /// <summary>
    /// Stripline built from two identical cores of this substrate's thickness bonded around the strip (the usual
    /// Rogers stack-up, e.g. 2 x 31 mil): plane spacing b = 2 H. The copper thickness adds to neither core here.
    /// </summary>
    public Substrate AsStriplineFromCores() => this with { H = 2.0 * H, IsStripline = true };
    public Substrate AsMicrostrip() => this with { IsStripline = false };

    public static Substrate Fr4(double h = 1.6e-3) => new(4.4, h, 35e-6, 0.02);
    public static Substrate RogersRo4003(double h = 0.508e-3) => new(3.55, h, 17.5e-6, 0.0027);
    public static Substrate RogersRo4350B(double h = 0.508e-3) => new(3.48, h, 17.5e-6, 0.0037);
    /// <summary>RT/duroid 5880: design Er 2.20, tan d 0.0009 at 10 GHz. Standard thicknesses 5, 10, 15, 20, 31, 62 and 125 mil.</summary>
    public static Substrate RogersRt5880(double h = 0.508e-3) => new(2.20, h, 17.5e-6, 0.0009);
    public static Substrate Alumina(double h = 0.635e-3) => new(9.8, h, 5e-6, 0.0001, 4.1e7);
}

/// <summary>Result of analysing a single microstrip line at one frequency.</summary>
public readonly record struct LineProperties(
    double Z0,
    double EpsEffStatic,
    double EpsEff,
    double AlphaCondNpPerM,
    double AlphaDielNpPerM)
{
    public double AlphaTotalNpPerM => AlphaCondNpPerM + AlphaDielNpPerM;
}

/// <summary>
/// Single microstrip line: Hammerstad-Jensen static Z0 and effective permittivity with strip-thickness
/// correction, Kirschning-Jansen dispersion of epsilon_eff, and closed-form (Hammerstad-Jensen) conductor and
/// dielectric loss. Loss is typically good to 10-20 percent; use EM for final insertion-loss numbers.
/// </summary>
public static class Microstrip
{
    public const double Eta0 = 376.730313668;
    public const double C0 = 299792458.0;
    private const double Mu0 = 4e-7 * Math.PI;

    /// <summary>Static characteristic impedance and effective permittivity (no dispersion).</summary>
    public static (double Z0, double EpsEff) Static(Substrate s, double w)
    {
        if (s.IsStripline) return Stripline.Static(s, w);
        double u = w / s.H;
        double t = s.T / s.H;

        double du1 = 0, dur = 0;
        if (t > 0)
        {
            double coth = 1.0 / Math.Tanh(Math.Sqrt(6.517 * u));
            du1 = t / Math.PI * Math.Log(1.0 + 4.0 * Math.E / (t * coth * coth));
            dur = 0.5 * (1.0 + 1.0 / Math.Cosh(Math.Sqrt(s.Er - 1.0))) * du1;
        }
        double u1 = u + du1;
        double ur = u + dur;

        double z01r = Z01(ur);
        double epsR = EpsEffHj(ur, s.Er);
        double z0 = z01r / Math.Sqrt(epsR);
        double eps = epsR * Math.Pow(Z01(u1) / z01r, 2);
        return (z0, eps);
    }

    /// <summary>Kirschning-Jansen effective permittivity versus frequency.</summary>
    public static double EpsEffAt(Substrate s, double w, double fHz)
    {
        if (s.IsStripline) return s.Er;
        var (_, e0) = Static(s, w);
        double u = w / s.H;
        double fh = fHz * 1e-9 * s.H * 1e2; // f in GHz times h in cm (Kirschning-Jansen convention)
        double er = s.Er;

        double p1 = 0.27488 + (0.6315 + 0.525 / Math.Pow(1.0 + 0.157 * fh, 20.0)) * u
                    - 0.065683 * Math.Exp(-8.7513 * u);
        double p2 = 0.33622 * (1.0 - Math.Exp(-0.03442 * er));
        double p3 = 0.0363 * Math.Exp(-4.6 * u) * (1.0 - Math.Exp(-Math.Pow(fh / 3.87, 4.97)));
        double p4 = 1.0 + 2.751 * (1.0 - Math.Exp(-Math.Pow(er / 15.916, 8.0)));
        double p = p1 * p2 * Math.Pow((0.1844 + p3 * p4) * 10.0 * fh, 1.5763);
        return er - (er - e0) / (1.0 + p);
    }

    /// <summary>Full analysis at frequency f (Hz).</summary>
    public static LineProperties Analyze(Substrate s, double w, double fHz)
    {
        if (s.IsStripline) return Stripline.Analyze(s, w, fHz);
        var (z0, eStatic) = Static(s, w);
        double eEff = EpsEffAt(s, w, fHz);

        double k0 = 2.0 * Math.PI * fHz / C0;
        double alphaD = s.TanD > 0
            ? k0 * s.Er * (eEff - 1.0) * s.TanD / (2.0 * Math.Sqrt(eEff) * (s.Er - 1.0))
            : 0.0;

        double rs = Math.Sqrt(Math.PI * fHz * Mu0 / s.Sigma);
        double delta = Math.Sqrt(1.0 / (Math.PI * fHz * Mu0 * s.Sigma));
        double rough = 1.0 + 2.0 / Math.PI * Math.Atan(1.4 * Math.Pow(s.RoughnessRms / delta, 2));
        // Hammerstad-Jensen conductor loss: uniform-current estimate times current-distribution factor Ki.
        double ki = Math.Exp(-1.2 * Math.Pow(z0 / Eta0, 0.7));
        double alphaC = rs * rough * ki / (z0 * w);

        return new LineProperties(z0, eStatic, eEff, alphaC, alphaD);
    }

    /// <summary>Strip width (m) giving characteristic impedance z0 (static), by bisection.</summary>
    public static double WidthForZ0(Substrate s, double z0)
    {
        double lo = 0.01 * s.H, hi = 30.0 * s.H;
        if (z0 < Static(s, hi).Z0 || z0 > Static(s, lo).Z0)
            throw new ArgumentOutOfRangeException(nameof(z0), "Impedance outside the realizable range for this substrate.");
        for (int i = 0; i < 100; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (Static(s, mid).Z0 > z0) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>Guided wavelength (m) of a width-w line at f.</summary>
    public static double GuidedWavelength(Substrate s, double w, double fHz)
        => C0 / (fHz * Math.Sqrt(EpsEffAt(s, w, fHz)));

    /// <summary>Unloaded Q of a resonator made from this line: Q = beta / (2 alpha).</summary>
    public static double UnloadedQ(Substrate s, double w, double fHz)
    {
        var p = Analyze(s, w, fHz);
        double beta = 2.0 * Math.PI * fHz * Math.Sqrt(p.EpsEff) / C0;
        return beta / (2.0 * p.AlphaTotalNpPerM);
    }

    // ---- Hammerstad-Jensen building blocks (shared with the coupled-line model) ----

    internal static double Z01(double u)
    {
        double f = 6.0 + (2.0 * Math.PI - 6.0) * Math.Exp(-Math.Pow(30.666 / u, 0.7528));
        return Eta0 / (2.0 * Math.PI) * Math.Log(f / u + Math.Sqrt(1.0 + 4.0 / (u * u)));
    }

    internal static double EpsEffHj(double u, double er)
    {
        double a = 1.0 + Math.Log((Math.Pow(u, 4) + Math.Pow(u / 52.0, 2)) / (Math.Pow(u, 4) + 0.432)) / 49.0
                   + Math.Log(1.0 + Math.Pow(u / 18.1, 3)) / 18.7;
        double b = 0.564 * Math.Pow((er - 0.9) / (er + 3.0), 0.053);
        return (er + 1.0) / 2.0 + (er - 1.0) / 2.0 * Math.Pow(1.0 + 10.0 / u, -a * b);
    }
}
