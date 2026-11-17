using System.Text.Json.Nodes;
using FiltSyn.Core;

namespace FiltSyn.Plugin;

/// <summary>Builds the views of a reply: the response plot, the layout artwork and the dimension table.</summary>
internal static class Views
{
    public static JsonObject Response(FilterResult r, (double[] S11dB, double[] S21dB)? crf = null)
    {
        var v = r.Verification;
        JsonObject Cube(double[] dB)
        {
            var re = new JsonArray(); var im = new JsonArray();
            foreach (double x in dB) { re.Add(Math.Pow(10, x / 20)); im.Add(0.0); }
            return new JsonObject { ["axes"] = new JsonArray("freq"), ["kind"] = "complex", ["re"] = re, ["im"] = im };
        }
        var freq = new JsonArray();
        foreach (double f in v.FrequenciesHz) freq.Add(f);
        var cubes = new JsonObject
        {
            ["S21"] = Cube(v.CircuitS21dB), ["S11"] = Cube(v.CircuitS11dB),
            ["S21_ideal"] = Cube(v.IdealS21dB), ["S11_ideal"] = Cube(v.IdealS11dB),
        };
        var traces = new JsonArray(
            new JsonObject { ["cube"] = "S21", ["y"] = "db20", ["label"] = "S21" },
            new JsonObject { ["cube"] = "S11", ["y"] = "db20", ["label"] = "S11" },
            new JsonObject { ["cube"] = "S21_ideal", ["y"] = "db20", ["label"] = "S21 ideal" },
            new JsonObject { ["cube"] = "S11_ideal", ["y"] = "db20", ["label"] = "S11 ideal" });
        if (crf is { } c)
        {
            cubes["S21_crf"] = Cube(c.S21dB); cubes["S11_crf"] = Cube(c.S11dB);
            traces.Add(new JsonObject { ["cube"] = "S21_crf", ["y"] = "db20", ["label"] = "S21 circuitRF" });
            traces.Add(new JsonObject { ["cube"] = "S11_crf", ["y"] = "db20", ["label"] = "S11 circuitRF" });
        }
        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["axes"] = new JsonObject { ["freq"] = new JsonObject { ["unit"] = "Hz", ["values"] = freq } },
                ["cubes"] = cubes,
            },
            ["plot"] = new JsonObject
            {
                ["title"] = "Circuit model vs ideal prototype",
                ["y"] = new JsonObject { ["label"] = "dB", ["min"] = -80, ["max"] = 0 },
                ["freqUnit"] = "GHz",
                ["traces"] = traces,
            },
        };
    }

    public static JsonObject Table(FilterResult r)
    {
        var rows = new JsonArray();
        foreach (var row in r.Rows)
        {
            var a = new JsonArray();
            foreach (var c in row) a.Add(c switch { null => null, double d => JsonValue.Create(d), string s => JsonValue.Create(s), _ => JsonValue.Create(c.ToString()) });
            rows.Add(a);
        }
        return new JsonObject
        {
            ["columns"] = new JsonArray(r.Columns.Select(x => (JsonNode?)x).ToArray()),
            ["units"] = new JsonArray(r.Units.Select(x => (JsonNode?)x).ToArray()),
            ["rows"] = rows,
        };
    }

    /// <summary>The copper as rectangles and the ports, coordinates int64 DBU in the frame's binary payload.</summary>
    public static (JsonObject View, long[] Bin) Artwork(FilterResult r, string stripLayer)
    {
        double dbu = r.Tech.DbuPerMicron;
        long D(double m) => (long)Math.Round(m * 1e6 * dbu);
        var bin = new List<long>();
        var shapes = new JsonArray();
        foreach (var poly in r.Layout.Polygons.Where(p => p.Layer == FilterLayout.CopperLayer))
            foreach (var (x0, y0, x1, y1) in Rectilinear.ToRects(poly.Points))
            {
                int at = bin.Count;
                bin.Add(D(x0)); bin.Add(D(y0)); bin.Add(D(x1)); bin.Add(D(y1));
                shapes.Add(new JsonObject
                {
                    ["kind"] = "rect", ["layer"] = stripLayer,
                    ["xy"] = new JsonObject { ["at"] = at, ["count"] = 4 },
                });
            }
        var ports = new JsonArray();
        for (int i = 0; i < r.Layout.Ports.Count; i++)
        {
            var (px, py) = r.Layout.Ports[i];
            ports.Add(new JsonObject
            {
                ["name"] = (i + 1).ToString(), ["x"] = D(px), ["y"] = D(py), ["layer"] = stripLayer,
                ["width"] = D(r.FeedWidth), ["outwardDeg"] = PortGeometry.OutwardDeg(r.Layout, i),
            });
        }
        var view = new JsonObject { ["technology"] = "self", ["shapes"] = shapes, ["ports"] = ports };
        return (view, bin.ToArray());
    }
}

internal static class PortGeometry
{
    private const double Tol = 1e-9;

    /// <summary>Direction the port points away from the filter, degrees (0 = +x, 90 = +y).</summary>
    public static int OutwardDeg(FilterLayout l, int i)
    {
        var (x, y) = l.Ports[i];
        var b = l.Board;
        if (Math.Abs(x - b.X0) < Tol) return 180;
        if (Math.Abs(x - b.X1) < Tol) return 0;
        if (Math.Abs(y - b.Y1) < Tol) return 90;
        return 270;
    }

    /// <summary>circuitRF's PortDirection label: the way a signal enters (R0 = +x), the reverse of outward.</summary>
    public static string LabelDirection(FilterLayout l, int i) => $"R{(OutwardDeg(l, i) + 180) % 360}";
}

/// <summary>Splits an axis-aligned polygon into rectangles by horizontal slabs.</summary>
internal static class Rectilinear
{
    public static List<(double X0, double Y0, double X1, double Y1)> ToRects((double X, double Y)[] pts)
    {
        const double eps = 1e-10;
        var ys = pts.Select(p => p.Y).OrderBy(y => y).ToList();
        var uy = new List<double>();
        foreach (var y in ys) if (uy.Count == 0 || y - uy[^1] > eps) uy.Add(y);

        var edges = new List<(double X, double Ya, double Yb)>();
        for (int i = 0; i < pts.Length; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Length];
            if (Math.Abs(a.X - b.X) < eps && Math.Abs(a.Y - b.Y) > eps)
                edges.Add((a.X, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)));
        }

        var rects = new List<(double X0, double Y0, double X1, double Y1)>();
        for (int s = 0; s + 1 < uy.Count; s++)
        {
            double mid = 0.5 * (uy[s] + uy[s + 1]);
            var xs = edges.Where(e => e.Ya < mid && e.Yb > mid).Select(e => e.X).OrderBy(x => x).ToList();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                // Extend the previous slab's rectangle when it has the same x interval.
                int idx = rects.FindIndex(r => Math.Abs(r.X0 - xs[k]) < eps && Math.Abs(r.X1 - xs[k + 1]) < eps && Math.Abs(r.Y1 - uy[s]) < eps);
                if (idx >= 0) rects[idx] = (rects[idx].X0, rects[idx].Y0, rects[idx].X1, uy[s + 1]);
                else rects.Add((xs[k], uy[s], xs[k + 1], uy[s + 1]));
            }
        }
        return rects;
    }
}
