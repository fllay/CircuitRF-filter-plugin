namespace FiltSyn.Core;

/// <summary>Even/odd-mode properties of a symmetric pair of coupled microstrip lines.</summary>
public readonly record struct CoupledProperties(
    double Z0e,
    double Z0o,
    double EpsEffEven,
    double EpsEffOdd)
{
    /// <summary>True for a homogeneous medium (stripline), where both modes travel at the same speed.</summary>
    public bool Homogeneous { get; init; }

    /// <summary>Characteristic impedance of the pair, sqrt(Z0e Z0o).</summary>
    public double Z0 => Math.Sqrt(Z0e * Z0o);

    /// <summary>
    /// Approximate coupling coefficient between two identical coupled half-wave resonators
    /// from the even/odd mode velocities, k = (fo^2 - fe^2)/(fo^2 + fe^2) = (eps_e - eps_o)/(eps_e + eps_o).
    /// In microstrip the even mode carries more field in the dielectric, so eps_e > eps_o and k > 0.
    /// Initial estimate only for fully coupled edge-coupled arms; hairpin self-coupling, offset resonators
    /// and bends need EM calibration.
    /// </summary>
    public double CouplingCoefficient => Homogeneous
        ? (Z0e - Z0o) / (Z0e + Z0o)   // equal velocities: only the impedance split remains (distributed coupling coefficient)
        : (EpsEffEven - EpsEffOdd) / (EpsEffEven + EpsEffOdd);
}

/// <summary>
/// Static even/odd-mode model of symmetric coupled microstrip (Kirschning-Jansen 1984 closed forms,
/// built on Hammerstad-Jensen). Strip thickness is accounted for through effective widths.
/// </summary>
public static class CoupledMicrostrip
{
    public static CoupledProperties Static(Substrate s, double w, double gap)
    {
        if (s.IsStripline) return Stripline.Coupled(s, w, gap);
        double u = w / s.H;
        double g = gap / s.H;
        double er = s.Er;

        // Single-line reference values (thickness-corrected).
        var (z0, e0) = Microstrip.Static(s, w);

        // Even-mode effective permittivity.
        double v = u * (20.0 + g * g) / (10.0 + g * g) + g * Math.Exp(-g);
        double ae = 1.0 + Math.Log((Math.Pow(v, 4) + Math.Pow(v / 52.0, 2)) / (Math.Pow(v, 4) + 0.432)) / 49.0
                    + Math.Log(1.0 + Math.Pow(v / 18.1, 3)) / 18.7;
        double be = 0.564 * Math.Pow((er - 0.9) / (er + 3.0), 0.053);
        double ee = (er + 1.0) / 2.0 + (er - 1.0) / 2.0 * Math.Pow(1.0 + 10.0 / v, -ae * be);

        // Odd-mode effective permittivity.
        double ao = 0.7287 * (e0 - (er + 1.0) / 2.0) * (1.0 - Math.Exp(-0.179 * u));
        double bo = 0.747 * er / (0.15 + er);
        double co = bo - (bo - 0.207) * Math.Exp(-0.414 * u);
        double dO = 0.593 + 0.694 * Math.Exp(-0.562 * u);
        double eo = ((er + 1.0) / 2.0 + ao - e0) * Math.Exp(-co * Math.Pow(g, dO)) + e0;

        // Impedance corrections.
        double q1 = 0.8695 * Math.Pow(u, 0.194);
        double q2 = 1.0 + 0.7519 * g + 0.189 * Math.Pow(g, 2.31);
        double q3 = 0.1975 + Math.Pow(16.6 + Math.Pow(8.4 / g, 6), -0.387)
                    + Math.Log(Math.Pow(g, 10) / (1.0 + Math.Pow(g / 3.4, 10))) / 241.0;
        double q4 = 2.0 * q1 / q2 / (Math.Exp(-g) * Math.Pow(u, q3) + (2.0 - Math.Exp(-g)) * Math.Pow(u, -q3));

        double q5 = 1.794 + 1.14 * Math.Log(1.0 + 0.638 / (g + 0.517 * Math.Pow(g, 2.43)));
        double q6 = 0.2305 + Math.Log(Math.Pow(g, 10) / (1.0 + Math.Pow(g / 5.8, 10))) / 281.3
                    + Math.Log(1.0 + 0.598 * Math.Pow(g, 1.154)) / 5.1;
        double q7 = (10.0 + 190.0 * g * g) / (1.0 + 82.3 * g * g * g);
        double q8 = Math.Exp(-6.5 - 0.95 * Math.Log(g) - Math.Pow(g / 0.15, 5));
        double q9 = Math.Log(q7) * (q8 + 1.0 / 16.5);
        double q10 = (q2 * q4 - q5 * Math.Exp(Math.Log(u) * q6 * Math.Pow(u, -q9))) / q2;

        double z0e = z0 * Math.Sqrt(e0 / ee) / (1.0 - z0 * Math.Sqrt(e0) * q4 / Microstrip.Eta0);
        double z0o = z0 * Math.Sqrt(e0 / eo) / (1.0 - z0 * Math.Sqrt(e0) * q10 / Microstrip.Eta0);

        return new CoupledProperties(z0e, z0o, ee, eo);
    }

    /// <summary>
    /// Gap (m) between two lines of width w that gives the requested coupling coefficient
    /// (see <see cref="CoupledProperties.CouplingCoefficient"/>), by bisection.
    /// </summary>
    public static double GapForCoupling(Substrate s, double w, double k)
    {
        double lo = 0.02 * s.H, hi = 20.0 * s.H;
        double kLo = Static(s, w, lo).CouplingCoefficient;
        double kHi = Static(s, w, hi).CouplingCoefficient;
        if (k > kLo || k < kHi)
            throw new ArgumentOutOfRangeException(nameof(k), $"Coupling must lie between {kHi:F5} and {kLo:F5} for this width.");
        for (int i = 0; i < 100; i++)
        {
            double mid = Math.Sqrt(lo * hi); // geometric bisection: gap spans decades
            if (Static(s, w, mid).CouplingCoefficient > k) lo = mid; else hi = mid;
        }
        return Math.Sqrt(lo * hi);
    }
}
