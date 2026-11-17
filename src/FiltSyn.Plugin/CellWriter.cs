using System.Text.Json;
using System.Text.Json.Nodes;
using FiltSyn.Core;

namespace FiltSyn.Plugin;

/// <summary>
/// Writes a placeable cell into the staging folder in the form circuitRF's own cells take: layout/NAME.clay,
/// 3d/NAME.c3d with one lumped port per feed and em/NAME.cem for an EMerge driven sweep.
/// Stripline puts the strip on Inner Copper between ground planes on Top and Bottom Copper and fences the board
/// edge with vias; microstrip puts it on Top Copper over a ground plane on Bottom Copper.
/// </summary>
internal static class CellWriter
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static List<string> Write(string staging, string cell, FilterResult r, JsonObject p, List<string> messages, string? workspace = null)
    {
        var tech = r.Tech;
        bool strip = r.Substrate.IsStripline;
        int stripLayer = Layer(tech, strip ? "Inner Copper" : "Top Copper", strip ? 11 : 1);
        int topLayer = Layer(tech, "Top Copper", 1), botLayer = Layer(tech, "Bottom Copper", 2), drill = Layer(tech, "Drill", 7);
        double dbu = tech.DbuPerMicron;
        long D(double m) => (long)Math.Round(m * 1e6 * dbu);
        JsonObject Key(int l) => new() { ["Layer"] = l, ["Datatype"] = 0 };

        string techRef = DesignEngine.Str(p, "techRef", "");
        if (techRef.Length == 0)
        {
            var id = tech.Id;
            var file = WorkspaceLinker.FindTechFile(workspace, id);
            if (file != null) techRef = $"../../tech/{file}";
            else if (string.IsNullOrEmpty(id)) messages.Add("The technology's id was not delivered, so the cell's TechRef is empty: set it in the cell's layout.");
            else techRef = id.EndsWith(".ctech", StringComparison.OrdinalIgnoreCase) ? id : $"../../tech/{id}.ctech";
        }

        string source = DesignEngine.Str(p, "substrateSource", "From technology");
        if (source != "From technology" && !string.IsNullOrEmpty(workspace) && techRef.Length > 0)
            techRef = DerivedTech(workspace, techRef, r.Substrate, source == "Rogers material" ? DesignEngine.Str(p, "material", "RO4350B") : "Custom", messages);

        var shapes = new JsonArray();
        foreach (var poly in r.Layout.Polygons.Where(x => x.Layer == FilterLayout.CopperLayer))
        {
            var xy = new JsonArray();
            foreach (var (x, y) in poly.Points) { xy.Add(D(x)); xy.Add(D(y)); }
            shapes.Add(new JsonObject { ["$type"] = "Poly", ["Layer"] = Key(stripLayer), ["Xy"] = xy });
        }
        var b = r.Layout.Board;
        JsonObject Rect(Rect q, int layer) => new()
        {
            ["$type"] = "Rect", ["Layer"] = Key(layer), ["X1"] = D(q.X0), ["Y1"] = D(q.Y0), ["X2"] = D(q.X1), ["Y2"] = D(q.Y1),
        };
        shapes.Add(Rect(b, botLayer));
        if (strip)
        {
            shapes.Add(Rect(b, topLayer));
            foreach (var (vx, vy) in ViaFence(r.Layout, 0.6e-3, 2.0e-3))
                shapes.Add(new JsonObject
                {
                    ["$type"] = "Via", ["Layer"] = Key(drill), ["X"] = D(vx), ["Y"] = D(vy),
                    ["PadSize"] = 900000, ["DrillSize"] = 500000,
                });
        }
        for (int i = 0; i < r.Layout.Ports.Count; i++)
        {
            var (px, py) = r.Layout.Ports[i];
            shapes.Add(new JsonObject
            {
                ["$type"] = "Label", ["Layer"] = Key(stripLayer), ["X"] = D(px), ["Y"] = D(py), ["Text"] = (i + 1).ToString(),
                ["Height"] = 1000000, ["IsPort"] = true, ["PortDirection"] = PortGeometry.LabelDirection(r.Layout, i),
            });
        }

        var clay = new JsonObject
        {
            ["FormatVersion"] = 1, ["DbuPerMicron"] = (int)dbu, ["DisplayUnit"] = "Mm", ["SnapDbu"] = (int)dbu,
            ["AngleMode"] = "AnyAngle", ["TechRef"] = techRef, ["Shapes"] = shapes, ["Instances"] = new JsonArray(),
        };

        // One lumped port per feed, spanning the dielectric from the lower ground to the strip (one core in stripline).
        long zb = D(r.Substrate.T);
        long hz = D(strip ? r.Substrate.H / 2.0 : r.Substrate.H);
        var ports = new JsonArray();
        for (int i = 0; i < r.Layout.Ports.Count; i++)
        {
            var (px, py) = r.Layout.Ports[i];
            bool vertical = Math.Abs(PortGeometry.OutwardDeg(r.Layout, i) % 180) == 0; // feed runs along x: port plane is YZ
            double lateral = vertical ? py : px, offset = vertical ? px : py;
            ports.Add(new JsonObject
            {
                ["Number"] = i + 1, ["Name"] = $"P{i + 1}", ["Kind"] = "Lumped", ["Plane"] = vertical ? "YZ" : "XZ",
                ["Offset"] = D(offset),
                ["Rect"] = new JsonObject
                {
                    ["Min"] = new JsonArray(D(lateral - r.FeedWidth / 2), zb),
                    ["Size"] = new JsonArray(D(r.FeedWidth), hz),
                },
                ["Z0"] = "50",
                ["FromLayout"] = new JsonObject { ["Instance"] = "U1", ["Port"] = i + 1 },
            });
        }
        var c3d = new JsonObject
        {
            ["FormatVersion"] = 1, ["DbuPerMicron"] = (int)dbu, ["DisplayUnit"] = "Mm", ["SnapDbu"] = (int)dbu, ["TechRef"] = techRef,
            ["Objects"] = new JsonArray(),
            ["Instances"] = new JsonArray(new JsonObject { ["Name"] = "U1", ["CellRef"] = "..", ["View"] = "Layout" }),
            ["Ports"] = ports,
            ["Look"] = new JsonObject
            {
                ["ShowEdges"] = true, ["ShowGrid"] = true, ["ShowOverlays"] = true, ["ShowPorts"] = true,
                ["ShowBoundaries"] = true, ["ShowImages"] = true,
            },
        };

        double span = DesignEngine.Num(p, "emSpan", 1.5);
        int npts = (int)DesignEngine.Num(p, "emPoints", 81);
        // Keep the window at least +/-5 percent of f0 so a passband that lands a little off is still caught and calibration can measure it.
        double half = Math.Max(span * r.SpecBandwidth, 0.05 * r.SpecF0);
        double lo = (r.SpecF0 - half) / 1e9, hi = (r.SpecF0 + half) / 1e9;
        JsonObject Pad(int um, string bc = "Absorbing") => new() { ["PaddingUm"] = um, ["Boundary"] = bc };
        var cem = new JsonObject
        {
            ["FormatVersion"] = 1, ["Name"] = cell, ["LayoutRef"] = $"../3d/{cell}.c3d", // relative to this .cem while staged; the host re-points it on placement
               
            ["Frequency"] = new JsonObject
            {
                ["StartExpr"] = lo.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                ["StopExpr"] = hi.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                ["NumPoints"] = npts, ["Mode"] = "PointCount", ["Kind"] = "Linear", ["StartUnit"] = "GHz", ["StopUnit"] = "GHz",
            },
            ["Port1Z0Real"] = 50, ["Port2Z0Real"] = 50, ["Solver3D"] = "Emerge", ["Problem3D"] = "Driven",
            ["Emerge"] = new JsonObject
            {
                ["Quality"] = "Standard", ["SaveFieldsGHz"] = new JsonArray(r.SpecF0 / 1e9), ["Tolerance"] = 1e-4, ["MaxSamples"] = 150,
            },
            ["AirBox"] = new JsonObject
            {
                ["XMin"] = Pad(10000), ["XMax"] = Pad(10000), ["YMin"] = Pad(10000), ["YMax"] = Pad(10000),
                ["ZMin"] = Pad(5000), ["ZMax"] = Pad(5000),
            },
        };
        var files = new List<string>();
        void Save(string rel, JsonNode n)
        {
            var path = Path.Combine(staging, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, n.ToJsonString(Pretty));
            files.Add(rel.Replace('\\', '/'));
        }
        Save($"{cell}/layout/{cell}.clay", clay);
        Save($"{cell}/3d/{cell}.c3d", c3d);
        if (r.Substrate.IsStripline) Save($"{cell}/em/{cell}.cem", cem);
        else
        {
            // Microstrip: the full-wave planar (method of moments) solver is the default setup. Checked against single
            // resonators, EMerge at Standard quality read 3 to 4 percent low and at Accurate within about 1 percent of it.
            var planar = new JsonObject
            {
                ["FormatVersion"] = 1, ["Name"] = cell, ["LayoutRef"] = $"../layout/{cell}.clay",
                ["Frequency"] = cem["Frequency"]!.DeepClone(),
                ["Port1Z0Real"] = 50, ["Port2Z0Real"] = 50,
                ["DispersionCorrection"] = true, ["AnalysisKind"] = "Planar",
            };
            // circuitRF accepts exactly one .cem per placed cell, so the 3D EMerge setup is not staged alongside.
            Save($"{cell}/em/{cell}.cem", planar);
        }
        return files;
    }

    /// <summary>
    /// The placed cell takes its stack-up from its technology file, so a substrate chosen in the panel (a Rogers material
    /// or manual values) needs a technology that says the same. Copies the selected technology, sets the dielectric
    /// Er, tan d and thickness and the copper thickness, saves it next to it in the workspace's tech folder and returns
    /// its cell-relative reference. Falls back to the original reference (with a message) when that is not possible.
    /// </summary>
    internal static string DerivedTech(string workspace, string techRef, Substrate s, string material, List<string> messages)
    {
        try
        {
            var dir = Path.Combine(workspace, "tech");
            var srcFile = Path.Combine(dir, Path.GetFileName(techRef));
            if (!File.Exists(srcFile)) { messages.Add("The cell uses the selected technology's stack-up: its file was not found to derive one from."); return techRef; }
            var root = JsonNode.Parse(File.ReadAllText(srcFile)) as JsonObject ?? throw new InvalidOperationException("not an object");
            var layers = (root["Stackup"] as JsonObject)?["Layers"] as JsonArray ?? throw new InvalidOperationException("no stack-up");
            var diel = layers.OfType<JsonObject>().Where(l => l["Kind"]?.GetValue<string>() == "Dielectric").ToList();
            if (diel.Count == 0 || diel.Count > 2) { messages.Add("The technology's stack-up has an unexpected dielectric layout, so the cell keeps its own stack-up instead of the chosen substrate."); return techRef; }
            double dbu = root["DbuPerMicron"]?.GetValue<double>() ?? 1000;
            double ToDbu(double m) => Math.Round(m * 1e6 * dbu);
            string label = material.Replace(' ', '-').Replace('/', '-');
            string mil = (s.H / 25.4e-6 * (s.IsStripline && diel.Count == 2 ? 0.5 : 1)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var d in diel)
            {
                d["Epsr"] = s.Er; d["TanD"] = s.TanD; d["Material"] = material;
                d["Name"] = material;
                d["ThicknessDbu"] = ToDbu(s.IsStripline && diel.Count == 2 ? s.H / 2 : s.H);
            }
            foreach (var c in layers.OfType<JsonObject>().Where(l => l["Kind"]?.GetValue<string>() == "Conductor" && l["Material"]?.GetValue<string>() == "Copper"))
                c["ThicknessDbu"] = ToDbu(s.T);
            string name = $"{label}_{mil}mil_{s.T * 1e6:0.#}um";
            root["Name"] = $"{material} ({mil} mil{(s.IsStripline ? " cores" : "")}, Er {s.Er:0.###}, tan d {s.TanD})";
            string file = $"fs_{name}.ctech";
            File.WriteAllText(Path.Combine(dir, file), root.ToJsonString(Pretty));
            messages.Add($"Technology 'tech/{file}' was written with the chosen substrate and the cell placed on it.");
            return $"../../tech/{file}";
        }
        catch (Exception e)
        {
            messages.Add($"Could not write a technology file for the chosen substrate ({e.Message}); the cell keeps the selected technology's stack-up.");
            return techRef;
        }
    }

    private static int Layer(TechInfo t, string name, int fallback)
        => t.LayerNumbers.TryGetValue(name, out var n) ? n : fallback;

    /// <summary>Vias along the board edges that carry no port, inset from the edge.</summary>
    private static IEnumerable<(double X, double Y)> ViaFence(FilterLayout l, double inset, double pitch)
    {
        var b = l.Board;
        bool PortOn(Func<(double X, double Y), bool> f) => l.Ports.Any(f);
        const double tol = 1e-9;
        bool left = PortOn(p => Math.Abs(p.X - b.X0) < tol), right = PortOn(p => Math.Abs(p.X - b.X1) < tol);
        bool bottom = PortOn(p => Math.Abs(p.Y - b.Y0) < tol), top = PortOn(p => Math.Abs(p.Y - b.Y1) < tol);
        double x0 = b.X0 + inset, x1 = b.X1 - inset, y0 = b.Y0 + inset, y1 = b.Y1 - inset;
        IEnumerable<double> Steps(double a, double z)
        {
            int n = Math.Max(1, (int)Math.Floor((z - a) / pitch));
            for (int i = 0; i <= n; i++) yield return a + (z - a) * i / n;
        }
        if (!bottom) foreach (var x in Steps(x0, x1)) yield return (x, y0);
        if (!top) foreach (var x in Steps(x0, x1)) yield return (x, y1);
        if (!left) foreach (var y in Steps(y0, y1)) if (y > y0 + 1e-9 && y < y1 - 1e-9) yield return (x0, y);
        if (!right) foreach (var y in Steps(y0, y1)) if (y > y0 + 1e-9 && y < y1 - 1e-9) yield return (x1, y);
    }
}
