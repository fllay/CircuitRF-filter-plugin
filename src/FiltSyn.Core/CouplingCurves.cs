namespace FiltSyn.Core;

/// <summary>Maps resonator spacing to coupling coefficient and back.</summary>
public interface ICouplingCurve
{
    /// <summary>Coupling coefficient for an edge-to-edge gap (m).</summary>
    double K(double gap);

    /// <summary>Gap (m) that gives coupling coefficient k.</summary>
    double GapFor(double k);
}

/// <summary>
/// Closed-form estimate from the coupled-microstrip even/odd modes, times a calibration scale.
/// The scale (default 1) is the knob for pulling the estimate toward EM-extracted values;
/// use <see cref="TabulatedCouplingCurve"/> once real k(s) data exists.
/// </summary>
public sealed class AnalyticCouplingCurve : ICouplingCurve
{
    private readonly Substrate _s;
    private readonly double _w;
    public double Scale { get; }

    public AnalyticCouplingCurve(Substrate s, double w, double scale = 1.0)
    {
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        _s = s; _w = w; Scale = scale;
    }

    public double K(double gap) => Scale * CoupledMicrostrip.Static(_s, _w, gap).CouplingCoefficient;

    public double GapFor(double k) => CoupledMicrostrip.GapForCoupling(_s, _w, k / Scale);
}

/// <summary>
/// Coupling curve from measured or EM-simulated (gap, k) pairs. Interpolates ln k against ln gap;
/// requests outside the tabulated range are rejected rather than extrapolated.
/// </summary>
public sealed class TabulatedCouplingCurve : ICouplingCurve
{
    private readonly double[] _lnGap;
    private readonly double[] _lnK;

    public TabulatedCouplingCurve(IEnumerable<(double Gap, double K)> points)
    {
        var pts = points.OrderBy(p => p.Gap).ToArray();
        if (pts.Length < 2) throw new ArgumentException("Need at least two points.", nameof(points));
        if (pts.Any(p => p.Gap <= 0 || p.K <= 0)) throw new ArgumentException("Gap and k must be positive.", nameof(points));
        for (int i = 1; i < pts.Length; i++)
            if (!(pts[i].K < pts[i - 1].K) || pts[i].Gap == pts[i - 1].Gap)
                throw new ArgumentException("k must strictly decrease as gap increases.", nameof(points));
        _lnGap = pts.Select(p => Math.Log(p.Gap)).ToArray();
        _lnK = pts.Select(p => Math.Log(p.K)).ToArray();
    }

    public double K(double gap)
    {
        double x = Math.Log(gap);
        if (x < _lnGap[0] - 1e-12 || x > _lnGap[^1] + 1e-12)
            throw new ArgumentOutOfRangeException(nameof(gap), "Gap outside the tabulated range.");
        int i = Array.FindLastIndex(_lnGap, g => g <= x + 1e-12);
        if (i >= _lnGap.Length - 1) i = _lnGap.Length - 2;
        double t = (x - _lnGap[i]) / (_lnGap[i + 1] - _lnGap[i]);
        return Math.Exp(_lnK[i] + t * (_lnK[i + 1] - _lnK[i]));
    }

    public double GapFor(double k)
    {
        double y = Math.Log(k);
        if (y > _lnK[0] + 1e-12 || y < _lnK[^1] - 1e-12)
            throw new ArgumentOutOfRangeException(nameof(k),
                $"k must lie between {Math.Exp(_lnK[^1]):G4} and {Math.Exp(_lnK[0]):G4} for this table.");
        int i = 0;
        while (i < _lnK.Length - 2 && y < _lnK[i + 1] - 1e-12) i++;
        double t = (y - _lnK[i]) / (_lnK[i + 1] - _lnK[i]);
        return Math.Exp(_lnGap[i] + t * (_lnGap[i + 1] - _lnGap[i]));
    }
}
