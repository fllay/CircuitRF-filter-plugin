namespace FiltSyn.Core;

/// <summary>Derivative-free simplex minimiser (Nelder-Mead).</summary>
public static class NelderMead
{
    public readonly record struct Result(double[] X, double Value, int Evaluations, bool Converged);

    public static Result Minimize(Func<double[], double> f, double[] x0, double step = 0.1,
        double tolerance = 1e-12, int maxEvaluations = 2000)
    {
        int n = x0.Length;
        var simplex = new double[n + 1][];
        var fx = new double[n + 1];
        int evals = 0;
        double Eval(double[] x) { evals++; return f(x); }

        simplex[0] = (double[])x0.Clone();
        fx[0] = Eval(simplex[0]);
        for (int i = 0; i < n; i++)
        {
            var p = (double[])x0.Clone();
            p[i] += step;
            simplex[i + 1] = p;
            fx[i + 1] = Eval(p);
        }

        bool converged = false;
        while (evals < maxEvaluations)
        {
            var order = Enumerable.Range(0, n + 1).OrderBy(i => fx[i]).ToArray();
            simplex = order.Select(i => simplex[i]).ToArray();
            fx = order.Select(i => fx[i]).ToArray();

            double spread = Math.Abs(fx[n] - fx[0]);
            double size = 0;
            for (int i = 1; i <= n; i++)
                for (int j = 0; j < n; j++) size = Math.Max(size, Math.Abs(simplex[i][j] - simplex[0][j]));
            if (spread <= tolerance * (Math.Abs(fx[0]) + 1e-30) && size < 1e-7) { converged = true; break; }

            var centroid = new double[n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) centroid[j] += simplex[i][j] / n;

            double[] At(double t) { var p = new double[n]; for (int j = 0; j < n; j++) p[j] = centroid[j] + t * (simplex[n][j] - centroid[j]); return p; }

            var xr = At(-1.0); double fr = Eval(xr);
            if (fr < fx[0])
            {
                var xe = At(-2.0); double fe = Eval(xe);
                if (fe < fr) { simplex[n] = xe; fx[n] = fe; } else { simplex[n] = xr; fx[n] = fr; }
            }
            else if (fr < fx[n - 1]) { simplex[n] = xr; fx[n] = fr; }
            else
            {
                var xc = fr < fx[n] ? At(-0.5) : At(0.5);
                double fc = Eval(xc);
                if (fc < Math.Min(fr, fx[n])) { simplex[n] = xc; fx[n] = fc; }
                else
                {
                    for (int i = 1; i <= n; i++)
                    {
                        for (int j = 0; j < n; j++) simplex[i][j] = simplex[0][j] + 0.5 * (simplex[i][j] - simplex[0][j]);
                        fx[i] = Eval(simplex[i]);
                    }
                }
            }
        }

        int best = 0;
        for (int i = 1; i <= n; i++) if (fx[i] < fx[best]) best = i;
        return new Result(simplex[best], fx[best], evals, converged);
    }
}
