using System.Numerics;

namespace FiltSyn.Core;

/// <summary>
/// Four-pole lowpass prototype with one pair of finite transmission zeros at +/- OmegaA, realised by cross-coupling
/// resonators 1 and 4 (Hong and Lancaster, Microstrip Filters for RF/Microwave Applications, 2001, section 10.1).
/// Element values g1, g2 and the inverters J1 (cross, 1-4) and J2 (main, 2-3) of the symmetric ladder of Fig. 10.2.
/// </summary>
public sealed record QuasiEllipticPrototype(double G1, double G2, double J1, double J2, double OmegaA, double ReturnLossDb)
{
    /// <summary>Hong eq. 10.6 (n = 4, return loss 20 dB, 1.8 &lt;= OmegaA &lt;= 2.4).</summary>
    public static QuasiEllipticPrototype FromCurveFit(double a)
    {
        double P(double c0, double c1, double c2, double c3, double c4) => c0 + a * (c1 + a * (c2 + a * (c3 + a * c4)));
        return new QuasiEllipticPrototype(
            P(1.22147, -0.35543, 0.18337, -0.0447, 0.00425),
            P(7.22106, -9.48678, 5.89032, -1.65776, 0.17723),
            P(-4.30192, 6.26745, -3.67345, 0.9936, -0.10317),
            P(8.17573, -11.36315, 6.96223, -1.94244, 0.20636), a, 20.0);
    }

    /// <summary>
    /// Synthesises the prototype for any zero position and return loss: J1 follows exactly from the zero position
    /// (Hong eq. 10.13 for n = 4: OmegaA^2 g2^2 = J2^2 - J2 / J1) and g1, g2, J2 are solved for an equiripple return
    /// loss over |Omega| &lt;= 1, starting from the curve fit of eq. 10.6.
    /// </summary>
    public static QuasiEllipticPrototype Synthesize(double omegaA, double returnLossDb = 20.0)
    {
        if (omegaA < 1.3 || omegaA > 6.0) throw new ArgumentOutOfRangeException(nameof(omegaA), "For four resonators the transmission zeros must lie at 1.3 to 6 times the half bandwidth from the centre (Omega_a 1.3 to 6).");
        if (returnLossDb < 5 || returnLossDb > 40) throw new ArgumentOutOfRangeException(nameof(returnLossDb));
        // Continuation: from the curve fit's range (and 20 dB) step towards the requested zero and return loss.
        double a0 = Math.Clamp(omegaA, 1.8, 2.4);
        var p0 = FromCurveFit(a0);
        int steps = (int)Math.Ceiling(Math.Max(Math.Abs(omegaA - a0) / 0.08, Math.Abs(returnLossDb - 20.0) / 2.0));
        for (int k = 1; k < steps; k++)
        {
            double t = k / (double)steps;
            p0 = Solve(a0 + (omegaA - a0) * t, 20.0 + (returnLossDb - 20.0) * t, p0);
        }
        return Solve(omegaA, returnLossDb, p0);
    }

    private static QuasiEllipticPrototype Solve(double omegaA, double returnLossDb, QuasiEllipticPrototype start)
    {
        double target = -returnLossDb;
        double Err(double[] x)
        {
            double g1 = x[0], g2 = x[1], j2 = x[2];
            if (g1 <= 0.1 || g2 <= 0.1 || j2 <= 0.05) return 1e6;
            double denom = omegaA * omegaA * g2 * g2 - j2 * j2;
            if (denom <= 1e-9) return 1e6;
            var p = new QuasiEllipticPrototype(g1, g2, -j2 / denom, j2, omegaA, returnLossDb);
            // Return-loss extremes in the passband: the band edge, the centre and the inner maximum.
            double s0 = p.S11dB(0.0), s1 = p.S11dB(1.0);
            // The interior local maximum between the two reflection zeros (not the rising edge near Omega = 1).
            const int N = 400;
            var sv = new double[N + 1];
            for (int i = 0; i <= N; i++) sv[i] = p.S11dB(i / (double)N);
            double inner = double.NegativeInfinity;
            for (int i = 2; i < N - 1; i++)
                if (sv[i] >= sv[i - 1] && sv[i] >= sv[i + 1]) inner = Math.Max(inner, sv[i]);
            if (double.IsNegativeInfinity(inner)) return 1e3;
            double e = 0;
            foreach (double v in new[] { s0, s1, inner }) e += (v - target) * (v - target);
            return e;
        }
        var best = NelderMead.Minimize(Err, new[] { start.G1, start.G2, start.J2 }, 0.02, 1e-14, 4000);
        double gg1 = best.X[0], gg2 = best.X[1], jj2 = best.X[2];
        double jj1 = -jj2 / (omegaA * omegaA * gg2 * gg2 - jj2 * jj2);
        if (best.Value > 1e-3)
            throw new InvalidOperationException($"Could not synthesise a quasi-elliptic prototype with zeros at +/-{omegaA:0.###} and {returnLossDb:0.#} dB return loss.");
        return new QuasiEllipticPrototype(gg1, gg2, jj1, jj2, omegaA, returnLossDb);
    }

    /// <summary>Even/odd-mode admittances of the symmetric ladder (Hong eq. 10.11a) at lowpass frequency Omega.</summary>
    public (Complex S11, Complex S21) Evaluate(double omega)
    {
        var j = Complex.ImaginaryOne;
        var ye = j * (omega * G1 - J1) + 1.0 / (j * (omega * G2 - J2));
        var yo = j * (omega * G1 + J1) + 1.0 / (j * (omega * G2 + J2));
        var d = (1 + ye) * (1 + yo);
        return ((1 - ye * yo) / d, (yo - ye) / d);
    }

    public double S11dB(double omega) => 20 * Math.Log10(Math.Max(Evaluate(omega).S11.Magnitude, 1e-12));

    /// <summary>The zero position this prototype actually has (eq. 10.13).</summary>
    public double ZeroOmega => Math.Sqrt(J2 * J2 - J2 / J1) / G2;

    /// <summary>Bandpass coupling matrix (Hong eq. 10.9): M12 = M34, M23 (main), M14 (cross, opposite sign) and Qe.</summary>
    public GeneralCouplingMatrix ToBandpass(double f0Hz, double bandwidthHz)
    {
        double fbw = bandwidthHz / f0Hz;
        var m = new double[4, 4];
        m[0, 1] = m[1, 0] = m[2, 3] = m[3, 2] = fbw / Math.Sqrt(G1 * G2);
        m[1, 2] = m[2, 1] = fbw * J2 / G2;
        m[0, 3] = m[3, 0] = fbw * J1 / G1;
        return new GeneralCouplingMatrix(m, G1 / fbw, G1 / fbw, f0Hz, fbw);
    }
}

/// <summary>
/// Coupled-resonator filter with an arbitrary (symmetric) coupling matrix, input at resonator 1 and output at
/// resonator n (Hong and Lancaster section 8.1): [A] = [q] + p[U] - j[m], S21 = 2 [A^-1]n1 / sqrt(qe1 qen).
/// </summary>
public sealed class GeneralCouplingMatrix
{
    public double[,] M { get; }
    public double QeIn { get; }
    public double QeOut { get; }
    public double F0Hz { get; }
    public double Fbw { get; }
    public int Order => M.GetLength(0);

    public GeneralCouplingMatrix(double[,] m, double qeIn, double qeOut, double f0Hz, double fbw)
    {
        M = m; QeIn = qeIn; QeOut = qeOut; F0Hz = f0Hz; Fbw = fbw;
    }

    public (Complex S11, Complex S21) Evaluate(double fHz)
    {
        int n = Order;
        double omega = (fHz / F0Hz - F0Hz / fHz) / Fbw;
        var a = new Complex[n, n];
        for (int i = 0; i < n; i++)
        {
            a[i, i] += new Complex(0, omega);
            for (int k = 0; k < n; k++) a[i, k] += new Complex(0, -M[i, k] / Fbw);
        }
        double q1 = QeIn * Fbw, qn = QeOut * Fbw;
        a[0, 0] += 1.0 / q1;
        a[n - 1, n - 1] += 1.0 / qn;
        var rhs = new Complex[n, 1];
        rhs[0, 0] = 1.0;
        var x = Linalg.Solve(a, rhs);
        return (1.0 - 2.0 / q1 * x[0, 0], 2.0 / Math.Sqrt(q1 * qn) * x[n - 1, 0]);
    }

    public (double[] S11dB, double[] S21dB) Sweep(double[] freqsHz)
    {
        var s11 = new double[freqsHz.Length];
        var s21 = new double[freqsHz.Length];
        for (int i = 0; i < freqsHz.Length; i++)
        {
            var (a, b) = Evaluate(freqsHz[i]);
            s11[i] = 20 * Math.Log10(Math.Max(a.Magnitude, 1e-12));
            s21[i] = 20 * Math.Log10(Math.Max(b.Magnitude, 1e-12));
        }
        return (s11, s21);
    }
}
