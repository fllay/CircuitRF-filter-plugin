using System.Text.Json.Nodes;

namespace FiltSyn.Plugin;

/// <summary>
/// What the plugin learns from a <c>technology</c> parameter. The host documents the value as "the technology id, its
/// dbuPerMicron, its layers and stackup" without fixing the field names, so every lookup here is tolerant: keys are
/// matched case-insensitively against a few spellings, and anything missing falls back to the shipped technologies'
/// conventions (Top Copper 1, Bottom Copper 2, Inner Copper 11, Drill 7; 1000 DBU per micron).
/// </summary>
internal sealed class TechInfo
{
    public string? Id { get; private set; }
    public double DbuPerMicron { get; private set; } = 1000;
    public List<(double Thickness, double Er, double TanD)> Dielectrics { get; } = new();
    public double? CopperThickness { get; private set; }
    public double? Sigma { get; private set; }
    public Dictionary<string, int> LayerNumbers { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Top Copper"] = 1, ["Bottom Copper"] = 2, ["Inner Copper"] = 11, ["Drill"] = 7,
    };

    public static TechInfo Read(JsonNode? node)
    {
        var t = new TechInfo();
        if (node is JsonValue v && v.TryGetValue<string>(out var id)) { t.Id = id; return t; }
        if (node is not JsonObject o) return t;
        t.Id = Str(o, "id", "technology", "name", "path");
        if (Num(o, "dbuPerMicron", "dbu") is { } d && d > 0) t.DbuPerMicron = d;
        t.Walk(o, t.DbuPerMicron);
        return t;
    }

    private void Walk(JsonNode? n, double dbu)
    {
        switch (n)
        {
            case JsonObject o:
                ReadLayer(o);
                ReadStackEntry(o, dbu);
                foreach (var kv in o) Walk(kv.Value, dbu);
                break;
            case JsonArray a:
                foreach (var x in a) Walk(x, dbu);
                break;
        }
    }

    private void ReadLayer(JsonObject o)
    {
        // A drawing-layer entry: a name and a layer number (possibly nested as key.layer).
        string? name = Str(o, "name");
        if (name is null) return;
        double? num = Num(o, "layer", "number");
        if (num is null && Get(o, "key") is JsonObject k) num = Num(k, "layer");
        if (num is { } nn && o["thicknessDbu"] is null && Get(o, "kind") is null)
            LayerNumbers[name] = (int)nn;
        // Stack entries list the drawing layers they sit on.
        if (Get(o, "drawingLayers") is JsonArray dl && dl.Count > 0 && dl[0] is JsonObject d0 && Num(d0, "layer") is { } ln)
            LayerNumbers[name.Split('(')[0].Trim()] = (int)ln;
    }

    private void ReadStackEntry(JsonObject o, double dbu)
    {
        string? kind = Str(o, "kind", "type", "role");
        double? th = Thickness(o, dbu);
        if (kind is null || th is null) return;
        if (kind.Contains("diel", StringComparison.OrdinalIgnoreCase))
        {
            double er = Num(o, "epsr", "er", "epsilonR", "eps") ?? 1.0;
            double tand = Num(o, "tand", "tanD", "tanDelta", "lossTangent") ?? 0.0;
            Dielectrics.Add((th.Value, er, tand));
        }
        else if (kind.Contains("cond", StringComparison.OrdinalIgnoreCase) && th.Value > 0)
        {
            CopperThickness ??= th.Value;
            Sigma ??= Num(o, "sigmaSm", "sigma", "conductivity");
        }
    }

    private static double? Thickness(JsonObject o, double dbu)
    {
        if (Num(o, "thicknessDbu") is { } d) return d / dbu * 1e-6;
        if (Num(o, "thicknessUm") is { } u) return u * 1e-6;
        if (Num(o, "thicknessM", "thickness_m") is { } m) return m;
        if (Num(o, "thickness") is { } t) return t < 0.1 ? t : t / dbu * 1e-6; // metres, else DBU
        return null;
    }

    private static JsonNode? Get(JsonObject o, string name)
    {
        foreach (var kv in o)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    private static string? Str(JsonObject o, params string[] names)
    {
        foreach (var n in names)
            if (Get(o, n) is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return null;
    }

    private static double? Num(JsonObject o, params string[] names)
    {
        foreach (var n in names)
        {
            var x = Get(o, n);
            if (x is JsonValue v && v.TryGetValue<double>(out var d)) return d;
            if (x is JsonObject q && Get(q, "value") is JsonValue qv && qv.TryGetValue<double>(out var dv)) return dv;
        }
        return null;
    }
}
