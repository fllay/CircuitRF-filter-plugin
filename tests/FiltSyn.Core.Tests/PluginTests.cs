using System.Text.Json.Nodes;
using FiltSyn.Core;
using FiltSyn.Plugin;
using Xunit;

namespace FiltSyn.Core.Tests;

public class PluginTests
{
    private static JsonObject Params(string filter, string medium, string source = "Manual") => new()
    {
        ["filter"] = filter, ["response"] = "Butterworth", ["order"] = 4,
        ["f0"] = new JsonObject { ["value"] = 3.5e9 }, ["bw"] = new JsonObject { ["value"] = filter == "Hairpin" ? 60e6 : 200e6 },
        ["ripple"] = new JsonObject { ["value"] = 0.1 }, ["medium"] = medium, ["substrateSource"] = source,
        ["er"] = 2.2, ["h"] = new JsonObject { ["value"] = 1.5748e-3 }, ["tand"] = 0.0009,
        ["copper"] = new JsonObject { ["value"] = 35e-6 }, ["cellName"] = "T", ["emSpan"] = 1.5, ["emPoints"] = 41,
        ["technology"] = new JsonObject { ["id"] = "stripline_RT5880_2x31mil_1oz" },
    };

    [Fact]
    public void Rectilinear_UShape_KeepsAreaAndIsRectangles()
    {
        // U: outer 0..3 x 0..4, notch 1..2 x 1..4.
        var u = new (double, double)[] { (0, 0), (3, 0), (3, 4), (2, 4), (2, 1), (1, 1), (1, 4), (0, 4) };
        var rs = Rectilinear.ToRects(u);
        double area = rs.Sum(r => (r.X1 - r.X0) * (r.Y1 - r.Y0));
        Assert.Equal(3 * 4 - 1 * 3, area, 9);
        Assert.Equal(3, rs.Count); // two arms and the base
    }

    [Fact]
    public void TechInfo_ReadsAStackupLikeTheShippedTechnologies()
    {
        var t = TechInfo.Read(JsonNode.Parse("""
        { "id": "stripline_RT5880_2x31mil_1oz", "dbuPerMicron": 1000,
          "layers": [ { "Key": { "Layer": 1, "Datatype": 0 }, "Name": "Top Copper" }, { "Key": { "Layer": 11, "Datatype": 0 }, "Name": "Inner Copper" } ],
          "stackup": { "Layers": [
            { "Kind": "Conductor", "Name": "Top Copper (1 oz)", "ThicknessDbu": 35000, "SigmaSm": 58000000, "DrawingLayers": [ { "Layer": 1, "Datatype": 0 } ] },
            { "Kind": "Dielectric", "Name": "upper", "ThicknessDbu": 787400, "Epsr": 2.2, "TanD": 0.0009 },
            { "Kind": "Conductor", "Name": "Inner Copper (1 oz)", "ThicknessDbu": 35000, "DrawingLayers": [ { "Layer": 11, "Datatype": 0 } ] },
            { "Kind": "Dielectric", "Name": "lower", "ThicknessDbu": 787400, "Epsr": 2.2, "TanD": 0.0009 } ] } }
        """));
        Assert.Equal("stripline_RT5880_2x31mil_1oz", t.Id);
        Assert.Equal(2, t.Dielectrics.Count);
        Assert.Equal(787.4e-6, t.Dielectrics[0].Thickness, 9);
        Assert.Equal(2.2, t.Dielectrics[0].Er);
        Assert.Equal(35e-6, t.CopperThickness!.Value, 12);
        Assert.Equal(11, t.LayerNumbers["Inner Copper"]);
    }

    [Theory]
    [InlineData("Coupled-line", "Stripline")]
    [InlineData("Coupled-line", "Microstrip")]
    [InlineData("Hairpin", "Stripline")]
    public void Engine_SynthesisesAndTunesOntoCentre(string filter, string medium)
    {
        var r = new DesignEngine().Run(Params(filter, medium));
        Assert.Equal(medium == "Stripline", r.Substrate.IsStripline);
        Assert.InRange(r.Verification.Circuit.CenterHz, 3.5e9 - 0.02 * r.Bandwidth, 3.5e9 + 0.02 * r.Bandwidth);
        Assert.NotEmpty(r.Rows);
        Assert.Equal(2, r.Layout.Ports.Count);
    }

    [Fact]
    public void Engine_FromTechnology_UsesItsStackup()
    {
        var p = Params("Coupled-line", "Stripline", "From technology");
        p["technology"] = JsonNode.Parse("""
        { "id": "t", "dbuPerMicron": 1000, "stackup": [
          { "kind": "dielectric", "thicknessDbu": 787400, "epsr": 2.2, "tand": 0.0009 },
          { "kind": "dielectric", "thicknessDbu": 787400, "epsr": 2.2, "tand": 0.0009 } ] }
        """);
        var r = new DesignEngine().Run(p);
        Assert.Equal(1.5748e-3, r.Substrate.H, 9);
        Assert.True(r.Substrate.IsStripline);
    }

    [Fact]
    public void Engine_FromTechnology_ExplainsWhenNothingCanBeRead()
    {
        var p = Params("Coupled-line", "Stripline", "From technology");
        p["technology"] = new JsonObject { ["id"] = "x" };
        var ex = Assert.Throws<InvalidOperationException>(() => new DesignEngine().Run(p));
        Assert.Contains("Manual", ex.Message);
    }

    [Fact]
    public void CellWriter_WritesFourDocumentsWithPortsAndGrounds()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var p = Params("Coupled-line", "Stripline");
            var r = new DesignEngine().Run(p);
            var files = CellWriter.Write(dir, "T", r, p, new List<string>());
            Assert.Equal(new[] { "T/layout/T.clay", "T/3d/T.c3d", "T/em/T.cem" }, files);
            var clay = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "T/layout/T.clay")))!;
            var shapes = clay["Shapes"]!.AsArray();
            Assert.Equal(2, shapes.Count(s => s!["IsPort"]?.GetValue<bool>() == true));
            Assert.Equal(2, shapes.Count(s => s!["$type"]!.GetValue<string>() == "Rect")); // two ground planes
            Assert.True(shapes.Any(s => s!["$type"]!.GetValue<string>() == "Via"));
            Assert.Equal("../../tech/stripline_RT5880_2x31mil_1oz.ctech", clay["TechRef"]!.GetValue<string>());
            var c3d = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "T/3d/T.c3d")))!;
            // The lumped port spans one core: half of b.
            Assert.InRange(c3d["Ports"]![0]!["Rect"]!["Size"]![1]!.GetValue<long>(), 787300L, 787500L);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WorkspaceLinker_FindsTechFileByDisplayNameAndRepointsCem()
    {
        var ws = Path.Combine(Path.GetTempPath(), "fs_ws_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "tech"));
        Directory.CreateDirectory(Path.Combine(ws, "F1", "3d"));
        Directory.CreateDirectory(Path.Combine(ws, "F1", "em"));
        File.WriteAllText(Path.Combine(ws, "tech", "pcb-2layer.ctech"), "{ \"Name\": \"PCB 2-Layer (20mil)\" }");
        File.WriteAllText(Path.Combine(ws, "F1", "3d", "F1.c3d"), "{}");
        File.WriteAllText(Path.Combine(ws, "F1", "em", "F1.cem"), "{\n  \"LayoutRef\": \"../3d/F1.c3d\",\n  \"X\": 1\n}");
        try
        {
            Assert.Equal("pcb-2layer.ctech", FiltSyn.Plugin.WorkspaceLinker.FindTechFile(ws, "PCB 2-Layer (20mil)"));
            Assert.Equal("pcb-2layer.ctech", FiltSyn.Plugin.WorkspaceLinker.FindTechFile(ws, "pcb-2layer"));
            Assert.True(FiltSyn.Plugin.WorkspaceLinker.FixOnce(ws, DateTime.UtcNow.AddMinutes(-1)));
            Assert.Contains("\"LayoutRef\": \"F1/3d/F1.c3d\"", File.ReadAllText(Path.Combine(ws, "F1", "em", "F1.cem")));
            Assert.False(FiltSyn.Plugin.WorkspaceLinker.FixOnce(ws, DateTime.UtcNow.AddMinutes(-1)));
        }
        finally { Directory.Delete(ws, true); }
    }
}
