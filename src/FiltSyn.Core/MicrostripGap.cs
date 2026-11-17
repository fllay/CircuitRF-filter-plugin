namespace FiltSyn.Core;

/// <summary>
/// Symmetric microstrip gap (two collinear open-ended lines of equal width facing each other across a gap s) as a pi
/// network: a series capacitance Cg across the gap and a shunt capacitance Cp from each end to ground. The form is the
/// Garg-Bahl power-law fit of the even- and odd-mode end capacitances (Cp = Ce/2, Cg = Co/2 - Ce/4), stated for
/// 0.5 &lt;= W/h &lt;= 2, 0.1 &lt;= s/W &lt;= 1 and 2.5 &lt;= Er &lt;= 15 and used outside that range as an extrapolation. The even-mode
/// constant was fitted to circuitRF's own MGAP element (five points, W/h 1.2 to 2.2, s/W 0.09 to 0.73, within 5 percent).
/// </summary>
public static class MicrostripGap
{
    /// <summary>Even-mode scale fitted to circuitRF's MGAP; see the class remarks.</summary>
    private const double EvenScale = 11.9;

    /// <summary>Shunt capacitance Cp of each end (F) and series capacitance Cg across the gap (F).</summary>
    public static (double Cp, double Cg) Capacitances(Substrate s, double w, double gap)
    {
        if (s.IsStripline) throw new InvalidOperationException("The gap model covers microstrip only.");
        if (gap <= 0) throw new ArgumentOutOfRangeException(nameof(gap), "The gap must be positive.");
        double wh = w / s.H, sw = gap / w;

        double mo = wh * (0.619 * Math.Log10(wh) - 0.3853);
        double ko = 4.26 - 1.453 * Math.Log10(wh);
        double co = Math.Pow(sw, mo) * Math.Exp(ko) * Math.Pow(s.Er / 9.6, 0.8) * w * 1e-12;

        double me, ke;
        if (sw <= 0.3) { me = 0.8675; ke = 2.043 * Math.Pow(wh, 0.12); }
        else { me = 1.565 / Math.Pow(wh, 0.16) - 1.0; ke = 1.97 - 0.03 / wh; }
        double ce = EvenScale * Math.Pow(sw, me) * Math.Exp(ke) * Math.Pow(s.Er / 9.6, 0.9) * w * 1e-12;

        double cp = 0.5 * ce;
        double cg = 0.5 * co - 0.25 * ce;
        return (cp, Math.Max(cg, 0.02 * co));   // a fit outside its range can drive Cg negative; keep it physical
    }
}
