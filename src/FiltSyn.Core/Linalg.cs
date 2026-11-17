using System.Numerics;

namespace FiltSyn.Core;

/// <summary>Small dense linear-algebra helpers (matrices here are at most about 16 x 16).</summary>
internal static class Linalg
{
    public static double[,] Identity(int n)
    {
        var m = new double[n, n];
        for (int i = 0; i < n; i++) m[i, i] = 1.0;
        return m;
    }

    public static double[,] Transpose(double[,] a)
    {
        int r = a.GetLength(0), c = a.GetLength(1);
        var t = new double[c, r];
        for (int i = 0; i < r; i++) for (int j = 0; j < c; j++) t[j, i] = a[i, j];
        return t;
    }

    public static double[,] Mul(double[,] a, double[,] b)
    {
        int n = a.GetLength(0), k = a.GetLength(1), m = b.GetLength(1);
        var r = new double[n, m];
        for (int i = 0; i < n; i++)
            for (int p = 0; p < k; p++)
            {
                double v = a[i, p];
                if (v == 0) continue;
                for (int j = 0; j < m; j++) r[i, j] += v * b[p, j];
            }
        return r;
    }

    /// <summary>Gauss-Jordan inverse with partial pivoting.</summary>
    public static double[,] Invert(double[,] a)
    {
        int n = a.GetLength(0);
        var m = (double[,])a.Clone();
        var inv = Identity(n);
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
            if (Math.Abs(m[piv, col]) < 1e-300) throw new InvalidOperationException("Singular matrix.");
            if (piv != col)
                for (int c = 0; c < n; c++)
                {
                    (m[col, c], m[piv, c]) = (m[piv, c], m[col, c]);
                    (inv[col, c], inv[piv, c]) = (inv[piv, c], inv[col, c]);
                }
            double d = m[col, col];
            for (int c = 0; c < n; c++) { m[col, c] /= d; inv[col, c] /= d; }
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = m[r, col];
                if (f == 0) continue;
                for (int c = 0; c < n; c++) { m[r, c] -= f * m[col, c]; inv[r, c] -= f * inv[col, c]; }
            }
        }
        return inv;
    }

    /// <summary>Lower-triangular Cholesky factor: a = L L^T.</summary>
    public static double[,] Cholesky(double[,] a)
    {
        int n = a.GetLength(0);
        var l = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= i; j++)
            {
                double s = a[i, j];
                for (int k = 0; k < j; k++) s -= l[i, k] * l[j, k];
                if (i == j)
                {
                    if (s <= 0) throw new InvalidOperationException("Matrix is not positive definite.");
                    l[i, i] = Math.Sqrt(s);
                }
                else l[i, j] = s / l[j, j];
            }
        return l;
    }

    public static double[,] InvertLower(double[,] l)
    {
        int n = l.GetLength(0);
        var inv = new double[n, n];
        for (int c = 0; c < n; c++)
            for (int i = c; i < n; i++)
            {
                double s = i == c ? 1.0 : 0.0;
                for (int k = c; k < i; k++) s -= l[i, k] * inv[k, c];
                inv[i, c] = s / l[i, i];
            }
        return inv;
    }

    /// <summary>Cyclic Jacobi eigen-decomposition of a real symmetric matrix. Columns of Vectors are eigenvectors.</summary>
    public static (double[] Values, double[,] Vectors) SymmetricEigen(double[,] a)
    {
        int n = a.GetLength(0);
        var m = (double[,])a.Clone();
        var v = Identity(n);
        double norm = 0;
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) norm += m[i, j] * m[i, j];
        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0;
            for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) off += m[i, j] * m[i, j];
            if (off <= 1e-28 * norm) break;
            for (int p = 0; p < n - 1; p++)
                for (int q = p + 1; q < n; q++)
                {
                    double apq = m[p, q];
                    if (Math.Abs(apq) < 1e-300) continue;
                    double theta = (m[q, q] - m[p, p]) / (2.0 * apq);
                    double t = Math.Sign(theta == 0 ? 1.0 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                    double c = 1.0 / Math.Sqrt(t * t + 1.0), s = t * c;
                    for (int k = 0; k < n; k++)
                    {
                        double akp = m[k, p], akq = m[k, q];
                        m[k, p] = c * akp - s * akq;
                        m[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double apk = m[p, k], aqk = m[q, k];
                        m[p, k] = c * apk - s * aqk;
                        m[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        var vals = new double[n];
        for (int i = 0; i < n; i++) vals[i] = m[i, i];
        return (vals, v);
    }

    /// <summary>Solve A X = B for complex matrices (Gaussian elimination, partial pivoting).</summary>
    public static Complex[,] Solve(Complex[,] a, Complex[,] b)
    {
        int n = a.GetLength(0), m = b.GetLength(1);
        var A = (Complex[,])a.Clone();
        var X = (Complex[,])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            double best = A[col, col].Magnitude;
            for (int r = col + 1; r < n; r++)
                if (A[r, col].Magnitude > best) { best = A[r, col].Magnitude; piv = r; }
            if (best < 1e-300) throw new InvalidOperationException("Singular network matrix.");
            if (piv != col)
            {
                for (int c = 0; c < n; c++) (A[col, c], A[piv, c]) = (A[piv, c], A[col, c]);
                for (int c = 0; c < m; c++) (X[col, c], X[piv, c]) = (X[piv, c], X[col, c]);
            }
            for (int r = col + 1; r < n; r++)
            {
                var f = A[r, col] / A[col, col];
                if (f == Complex.Zero) continue;
                for (int c = col; c < n; c++) A[r, c] -= f * A[col, c];
                for (int c = 0; c < m; c++) X[r, c] -= f * X[col, c];
            }
        }
        for (int c = 0; c < m; c++)
            for (int r = n - 1; r >= 0; r--)
            {
                var s = X[r, c];
                for (int k = r + 1; k < n; k++) s -= A[r, k] * X[k, c];
                X[r, c] = s / A[r, r];
            }
        return X;
    }
}
