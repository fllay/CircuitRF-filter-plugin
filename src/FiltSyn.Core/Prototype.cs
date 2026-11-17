namespace FiltSyn.Core;

/// <summary>Lowpass prototype element values g0..g(n+1) and order estimation.</summary>
public static class Prototype
{
    /// <summary>Butterworth g-values, returned as g[0..n+1] with g[0] = g[n+1] = 1.</summary>
    public static double[] Butterworth(int n)
    {
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n));
        var g = new double[n + 2];
        g[0] = 1.0;
        for (int k = 1; k <= n; k++)
            g[k] = 2.0 * Math.Sin((2 * k - 1) * Math.PI / (2.0 * n));
        g[n + 1] = 1.0;
        return g;
    }

    /// <summary>Chebyshev g-values for passband ripple Lar (dB), g[0..n+1].</summary>
    public static double[] Chebyshev(int n, double rippleDb)
    {
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n));
        if (rippleDb <= 0) throw new ArgumentOutOfRangeException(nameof(rippleDb));

        double beta = Math.Log(1.0 / Math.Tanh(rippleDb / 17.37));
        double gamma = Math.Sinh(beta / (2.0 * n));

        var a = new double[n + 1];
        var b = new double[n + 1];
        for (int k = 1; k <= n; k++)
        {
            a[k] = Math.Sin((2 * k - 1) * Math.PI / (2.0 * n));
            double s = Math.Sin(k * Math.PI / n);
            b[k] = gamma * gamma + s * s;
        }

        var g = new double[n + 2];
        g[0] = 1.0;
        g[1] = 2.0 * a[1] / gamma;
        for (int k = 2; k <= n; k++)
            g[k] = 4.0 * a[k - 1] * a[k] / (b[k - 1] * g[k - 1]);

        if (n % 2 == 1)
            g[n + 1] = 1.0;
        else
        {
            double c = 1.0 / Math.Tanh(beta / 4.0);
            g[n + 1] = c * c;
        }
        return g;
    }

    /// <summary>
    /// Normalized lowpass frequency of a bandpass stopband edge:
    /// Omega = (1/FBW) * (f/f0 - f0/f).
    /// </summary>
    public static double BandpassToLowpass(double f, double f0, double fbw)
        => (f / f0 - f0 / f) / fbw;

    /// <summary>Smallest Butterworth order giving at least attenDb at |Omega| = omegaS (passband edge 3 dB or ripple).</summary>
    public static int ButterworthOrder(double omegaS, double attenDb, double passbandDb = 3.0103)
    {
        omegaS = Math.Abs(omegaS);
        if (omegaS <= 1.0) throw new ArgumentException("Stopband edge must be outside the passband.");
        double eps2 = Math.Pow(10, passbandDb / 10.0) - 1.0;
        double num = Math.Log10((Math.Pow(10, attenDb / 10.0) - 1.0) / eps2);
        return (int)Math.Ceiling(num / (2.0 * Math.Log10(omegaS)));
    }

    /// <summary>Smallest Chebyshev order giving at least attenDb at |Omega| = omegaS for the given ripple.</summary>
    public static int ChebyshevOrder(double omegaS, double attenDb, double rippleDb)
    {
        omegaS = Math.Abs(omegaS);
        if (omegaS <= 1.0) throw new ArgumentException("Stopband edge must be outside the passband.");
        double eps2 = Math.Pow(10, rippleDb / 10.0) - 1.0;
        double ratio = (Math.Pow(10, attenDb / 10.0) - 1.0) / eps2;
        double n = Math.Acosh(Math.Sqrt(ratio)) / Math.Acosh(omegaS);
        return (int)Math.Ceiling(n);
    }
}
