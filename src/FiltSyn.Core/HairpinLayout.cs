using System.Globalization;
using System.Text;

namespace FiltSyn.Core;

/// <summary>Closed polygon on a named layer. Coordinates in metres.</summary>
public sealed record LayoutPolygon(string Layer, (double X, double Y)[] Points);

/// <summary>Drawable geometry of a filter: copper outlines, board outline and port marks.</summary>
public sealed class FilterLayout
{
    public const string CopperLayer = "COPPER";
    public const string BoardLayer = "BOARD";

    public IReadOnlyList<LayoutPolygon> Polygons { get; }
    /// <summary>Port reference points (x, y) at the outer end of each feed, input first.</summary>
    public IReadOnlyList<(double X, double Y)> Ports { get; }
    public Rect Bounds { get; }
    public Rect Board { get; }

    internal FilterLayout(List<LayoutPolygon> polys, List<(double, double)> ports, Rect bounds, Rect board)
    {
        Polygons = polys; Ports = ports; Bounds = bounds; Board = board;
    }

    internal static (double, double)[] RectPoints(Rect r)
        => new[] { (r.X0, r.Y0), (r.X1, r.Y0), (r.X1, r.Y1), (r.X0, r.Y1) };
}

/// <summary>Layout of a hairpin design.</summary>
public static class HairpinLayout
{
    public const string CopperLayer = FilterLayout.CopperLayer;
    public const string BoardLayer = FilterLayout.BoardLayer;

    /// <summary>
    /// Builds the layout. Each hairpin is one U-shaped polygon; each feed is a rectangle that overlaps its resonator
    /// by half a strip width. Overlapping polygons on one layer are unioned by EM and CAM tools.
    /// </summary>
    /// <param name="boardMargin">Substrate margin around the copper (m).</param>
    public static FilterLayout From(HairpinDesign d, double boardMargin = 5e-3)
    {
        var polys = new List<LayoutPolygon>();
        double w = d.ResonatorWidth;
        foreach (var r in d.Resonators)
        {
            var polyBefore = polys.Count;
            double x0 = r.LeftArm.X0, x1 = r.RightArm.X1, top = r.LeftArm.Y1;
            double xi0 = r.LeftArm.X1, xi1 = r.RightArm.X0;
            if (r.Tails.Length == 2)
            {
                // Open-loop ring: one C-shaped polygon; each tail turns inward from the top of its arm.
                double xl = r.Tails[0].X1, xr = r.Tails[1].X0, under = top - w;
                polys.Add(new LayoutPolygon(CopperLayer, new[]
                {
                    (x0, 0.0), (x1, 0.0), (x1, top), (xr, top), (xr, under), (xi1, under), (xi1, w),
                    (xi0, w), (xi0, under), (xl, under), (xl, top), (x0, top),
                }));
            }
            else
                polys.Add(new LayoutPolygon(CopperLayer, new[]
                {
                    (x0, 0.0), (x1, 0.0), (x1, top), (xi1, top), (xi1, w), (xi0, w), (xi0, top), (x0, top),
                }));
            if (r.Flipped)   // turned upside down: mirror the polygon in y about the middle of the resonator
            {
                var up = polys[polyBefore];
                polys[polyBefore] = new LayoutPolygon(up.Layer, up.Points.Select(pt => (pt.X, top - pt.Y)).Reverse().ToArray());
            }
        }

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var rc in d.AllRectangles())
        {
            minX = Math.Min(minX, rc.X0); minY = Math.Min(minY, rc.Y0);
            maxX = Math.Max(maxX, rc.X1); maxY = Math.Max(maxY, rc.Y1);
        }
        var bounds = new Rect(minX, minY, maxX, maxY);
        var board = new Rect(minX - boardMargin, minY - boardMargin, maxX + boardMargin, maxY + boardMargin);

        // Feeds run out to the board edge so the ports sit on it. Each overlaps its resonator by half a strip width.
        double e = w / 2.0;
        var ports = new List<(double, double)>();
        foreach (var tap in new[] { d.Input, d.Output })
        {
            var f = tap.Feed;
            if (tap.OnBase && tap.FromTop)
            {
                f = new Rect(f.X0, f.Y0 - e, f.X1, board.Y1);
                ports.Add(((f.X0 + f.X1) / 2, board.Y1));
            }
            else if (tap.OnBase)
            {
                f = new Rect(f.X0, board.Y0, f.X1, f.Y1 + e);
                ports.Add(((f.X0 + f.X1) / 2, board.Y0));
            }
            else if (f.X1 <= d.Resonators[0].X + 1e-12)
            {
                f = new Rect(board.X0, f.Y0, f.X1 + e, f.Y1);
                ports.Add((board.X0, (f.Y0 + f.Y1) / 2));
            }
            else
            {
                f = new Rect(f.X0 - e, f.Y0, board.X1, f.Y1);
                ports.Add((board.X1, (f.Y0 + f.Y1) / 2));
            }
            polys.Add(new LayoutPolygon(CopperLayer, RectPoints(f)));
        }

        polys.Add(new LayoutPolygon(BoardLayer, RectPoints(board)));
        return new FilterLayout(polys, ports, bounds, board);
    }

    private static (double, double)[] RectPoints(Rect r) => FilterLayout.RectPoints(r);
}

/// <summary>
/// Minimal ASCII DXF writer (AutoCAD R12 / AC1009, closed POLYLINEs) that every CAD, CAM and EM tool reads.
/// Output units are millimetres.
/// </summary>
public static class DxfWriter
{
    public static string Write(FilterLayout layout)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void P(int code, string value) { sb.Append(code.ToString(inv)).Append('\n').Append(value).Append('\n'); }
        string N(double metres) => (metres * 1e3).ToString("0.######", inv);

        P(0, "SECTION"); P(2, "HEADER");
        P(9, "$ACADVER"); P(1, "AC1009");
        P(9, "$INSUNITS"); P(70, "4");          // millimetres
        P(9, "$MEASUREMENT"); P(70, "1");
        P(0, "ENDSEC");

        var layers = layout.Polygons.Select(p => p.Layer).Distinct().ToList();
        P(0, "SECTION"); P(2, "TABLES");
        P(0, "TABLE"); P(2, "LAYER"); P(70, layers.Count.ToString(inv));
        foreach (var l in layers)
        {
            P(0, "LAYER"); P(2, l); P(70, "0");
            P(62, l == FilterLayout.CopperLayer ? "1" : "8");   // red copper, grey board
            P(6, "CONTINUOUS");
        }
        P(0, "ENDTAB"); P(0, "ENDSEC");

        P(0, "SECTION"); P(2, "ENTITIES");
        foreach (var poly in layout.Polygons)
        {
            P(0, "POLYLINE"); P(8, poly.Layer); P(66, "1"); P(70, "1");
            foreach (var (x, y) in poly.Points)
            {
                P(0, "VERTEX"); P(8, poly.Layer);
                P(10, N(x)); P(20, N(y)); P(30, "0");
            }
            P(0, "SEQEND"); P(8, poly.Layer);
        }
        P(0, "ENDSEC");
        P(0, "EOF");
        return sb.ToString();
    }

    public static void Write(FilterLayout layout, string path) => File.WriteAllText(path, Write(layout));
}
