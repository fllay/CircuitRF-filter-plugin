using System.Numerics;

namespace FiltSyn.Core;

/// <summary>
/// Synchronously tuned coupled-resonator bandpass filter described by external Q's and
/// inter-resonator couplings (Hong and Lancaster formulation). Gives an ideal, instant S-parameter
/// response used to validate the prototype and later the physical (hairpin) design.
/// </summary>
public sealed class CouplingMatrix
{
    public int Order { get; }
    public double F0Hz { get; }
    public double Fbw { get; }

    /// <summary>Scaled external Q's: qe = Qe * FBW (input and output).</summary>
    public double QeIn { get; }
    public double QeOut { get; }

    /// <summary>Normalized couplings m[i] between resonator i+1 and i+2: m = k / FBW.</summary>
    public double[] M { get; }

    /// <summary>Physical coupling coefficients k[i] = FBW / sqrt(g_i g_{i+1}).</summary>
    public double[] K { get; }

    /// <summary>Physical external quality factor Qe = g0 g1 / FBW (input) and gN gN+1 / FBW (output).</summary>
    public double QeInPhysical => QeIn / Fbw;
    public double QeOutPhysical => QeOut / Fbw;

    public CouplingMatrix(double[] g, double f0Hz, double fbw)
    {
        Order = g.Length - 2;
        F0Hz = f0Hz;
        Fbw = fbw;
        QeIn = g[0] * g[1];
        QeOut = g[Order] * g[Order + 1];
        M = new double[Order - 1];
        K = new double[Order - 1];
        for (int i = 0; i < Order - 1; i++)
        {
            double inv = 1.0 / Math.Sqrt(g[i + 1] * g[i + 2]);
            M[i] = inv;
            K[i] = fbw * inv;
        }
    }

    public static CouplingMatrix FromPrototype(double[] g, double f0Hz, double bandwidthHz)
        => new(g, f0Hz, bandwidthHz / f0Hz);

    /// <summary>Normalized lowpass frequency variable for a physical frequency.</summary>
    public double Omega(double fHz) => (fHz / F0Hz - F0Hz / fHz) / Fbw;

    /// <summary>S11 and S21 at one frequency.</summary>
    public (Complex S11, Complex S21) Evaluate(double fHz)
    {
        int n = Order;
        double omega = Omega(fHz);

        // A = q + j*Omega*U - j*m
        var a = new Complex[n, n];
        a[0, 0] += 1.0 / QeIn;
        a[n - 1, n - 1] += 1.0 / QeOut;
        for (int i = 0; i < n; i++) a[i, i] += new Complex(0, omega);
        for (int i = 0; i < n - 1; i++)
        {
            a[i, i + 1] += new Complex(0, -M[i]);
            a[i + 1, i] += new Complex(0, -M[i]);
        }

        // Solve A x = e1; x[0] = [A^-1]_11, x[n-1] = [A^-1]_N1
        var rhs = new Complex[n];
        rhs[0] = 1.0;
        var x = Solve(a, rhs);

        var s11 = 1.0 - (2.0 / QeIn) * x[0];
        var s21 = 2.0 / Math.Sqrt(QeIn * QeOut) * x[n - 1];
        return (s11, s21);
    }

    public (double[] S11dB, double[] S21dB) Sweep(double[] freqsHz)
    {
        var s11 = new double[freqsHz.Length];
        var s21 = new double[freqsHz.Length];
        for (int i = 0; i < freqsHz.Length; i++)
        {
            var (a, b) = Evaluate(freqsHz[i]);
            s11[i] = ToDb(a);
            s21[i] = ToDb(b);
        }
        return (s11, s21);
    }

    private static double ToDb(Complex c) => 20.0 * Math.Log10(Math.Max(c.Magnitude, 1e-12));

    // Gaussian elimination with partial pivoting (complex, small N).
    private static Complex[] Solve(Complex[,] a, Complex[] b)
    {
        int n = b.Length;
        var m = (Complex[,])a.Clone();
        var r = (Complex[])b.Clone();

        for (int col = 0; col < n; col++)
        {
            int piv = col;
            double best = m[col, col].Magnitude;
            for (int row = col + 1; row < n; row++)
                if (m[row, col].Magnitude > best) { best = m[row, col].Magnitude; piv = row; }
            if (best < 1e-300) throw new InvalidOperationException("Singular coupling matrix.");
            if (piv != col)
            {
                for (int c = 0; c < n; c++) (m[col, c], m[piv, c]) = (m[piv, c], m[col, c]);
                (r[col], r[piv]) = (r[piv], r[col]);
            }
            for (int row = col + 1; row < n; row++)
            {
                var f = m[row, col] / m[col, col];
                if (f == Complex.Zero) continue;
                for (int c = col; c < n; c++) m[row, c] -= f * m[col, c];
                r[row] -= f * r[col];
            }
        }

        var x = new Complex[n];
        for (int row = n - 1; row >= 0; row--)
        {
            var s = r[row];
            for (int c = row + 1; c < n; c++) s -= m[row, c] * x[c];
            x[row] = s / m[row, row];
        }
        return x;
    }
}
