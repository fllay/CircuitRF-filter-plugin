using System.Numerics;

namespace FiltSyn.Core;

/// <summary>
/// N parallel coupled conductors over a ground plane, described by per-unit-length inductance and
/// capacitance matrices. Gives the 2N-port admittance blocks of a length of line at any frequency:
/// Y11 = P diag(-j Yk cot(theta_k)) P^T and Y12 = P diag(j Yk / sin(theta_k)) P^T, where the modal
/// decomposition is done once (Cholesky + Jacobi) and only the diagonal changes with frequency.
/// </summary>
public sealed class MulticonductorLine
{
    private readonly double[,] _p;          // R^-T Q
    private readonly double[] _sqrtLambda;  // 1 / modal phase velocity

    public int Count { get; }

    /// <summary>Phase velocity of each mode (m/s), static.</summary>
    public IReadOnlyList<double> ModeVelocities => _sqrtLambda.Select(s => 1.0 / s).ToArray();

    private MulticonductorLine(int n, double[,] p, double[] sqrtLambda)
    {
        Count = n; _p = p; _sqrtLambda = sqrtLambda;
    }

    /// <summary>Build from per-unit-length inductance [H/m] and capacitance [F/m] matrices.</summary>
    public static MulticonductorLine FromLC(double[,] l, double[,] c)
    {
        int n = l.GetLength(0);
        var r = Linalg.Cholesky(l);                         // L = R R^T
        var rt = Linalg.Transpose(r);
        var s = Linalg.Mul(Linalg.Mul(rt, c), r);           // symmetric, similar to L C
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double avg = 0.5 * (s[i, j] + s[j, i]);
                s[i, j] = s[j, i] = avg;
            }
        var (vals, q) = Linalg.SymmetricEigen(s);
        var rinv = Linalg.InvertLower(r);
        var p = Linalg.Mul(Linalg.Transpose(rinv), q);
        var sl = new double[n];
        for (int k = 0; k < n; k++)
        {
            if (vals[k] <= 0) throw new InvalidOperationException("Non-physical L/C matrices (non-positive mode).");
            sl[k] = Math.Sqrt(vals[k]);
        }
        return new MulticonductorLine(n, p, sl);
    }

    /// <summary>
    /// Equal-width microstrip conductors separated by gaps[i] (between conductor i and i+1). The L and C
    /// matrices are assembled from the isolated-line and nearest-neighbour even/odd-mode data, with the
    /// ground capacitance reduced by the fringing each neighbour removes (first-order superposition).
    /// </summary>
    public static MulticonductorLine FromMicrostrip(Substrate s, double w, double[] gaps)
    {
        int n = gaps.Length + 1;
        double c = Microstrip.C0;
        var (z0, e0) = Microstrip.Static(s, w);
        double c0 = Math.Sqrt(e0) / (c * z0);
        double ca0 = 1.0 / (c * z0 * Math.Sqrt(e0));

        var cg = Enumerable.Repeat(c0, n).ToArray();
        var cag = Enumerable.Repeat(ca0, n).ToArray();
        var cm = new double[n - 1];
        var cam = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            var pr = CoupledMicrostrip.Static(s, w, gaps[i]);
            double ce = Math.Sqrt(pr.EpsEffEven) / (c * pr.Z0e);
            double co = Math.Sqrt(pr.EpsEffOdd) / (c * pr.Z0o);
            double cae = 1.0 / (c * pr.Z0e * Math.Sqrt(pr.EpsEffEven));
            double cao = 1.0 / (c * pr.Z0o * Math.Sqrt(pr.EpsEffOdd));
            cm[i] = (co - ce) / 2.0;
            cam[i] = (cao - cae) / 2.0;
            cg[i] -= c0 - ce; cg[i + 1] -= c0 - ce;
            cag[i] -= ca0 - cae; cag[i + 1] -= ca0 - cae;
        }

        var C = new double[n, n];
        var Ca = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            if (cg[i] <= 0 || cag[i] <= 0)
                throw new InvalidOperationException("Neighbour spacing too tight for the nearest-neighbour model.");
            C[i, i] += cg[i]; Ca[i, i] += cag[i];
            if (i < n - 1)
            {
                C[i, i] += cm[i]; C[i + 1, i + 1] += cm[i]; C[i, i + 1] = C[i + 1, i] = -cm[i];
                Ca[i, i] += cam[i]; Ca[i + 1, i + 1] += cam[i]; Ca[i, i + 1] = Ca[i + 1, i] = -cam[i];
            }
        }
        // L = mu0 eps0 Ca^-1 = Ca^-1 / c^2
        var L = Linalg.Invert(Ca);
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) L[i, j] /= c * c;
        for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) L[i, j] = L[j, i] = 0.5 * (L[i, j] + L[j, i]);
        return FromLC(L, C);
    }

    /// <summary>
    /// Admittance blocks of a section of this line. alpha is a uniform attenuation (Np/m); rho scales the
    /// dielectric loading to follow dispersion (eps_eff(f) / eps_eff(0)).
    /// </summary>
    public (Complex[,] Y11, Complex[,] Y12) Y(double length, double fHz, double alpha = 0.0, double rho = 1.0)
    {
        int n = Count;
        double omega = 2.0 * Math.PI * fHz;
        double sr = Math.Sqrt(rho);
        var a = new Complex[n];
        var b = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            double yk = sr * _sqrtLambda[k];
            var theta = new Complex(omega * yk * length, -alpha * length);
            var sin = Complex.Sin(theta);
            a[k] = yk * (-Complex.ImaginaryOne) * Complex.Cos(theta) / sin;
            b[k] = yk * Complex.ImaginaryOne / sin;
        }
        var y11 = new Complex[n, n];
        var y12 = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = i; j < n; j++)
            {
                Complex s11 = Complex.Zero, s12 = Complex.Zero;
                for (int k = 0; k < n; k++)
                {
                    double pp = _p[i, k] * _p[j, k];
                    s11 += pp * a[k];
                    s12 += pp * b[k];
                }
                y11[i, j] = y11[j, i] = s11;
                y12[i, j] = y12[j, i] = s12;
            }
        return (y11, y12);
    }
}
