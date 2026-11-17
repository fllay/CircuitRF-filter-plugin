using FiltSyn.Core;
using Xunit;

namespace FiltSyn.Core.Tests;

public class LayoutTests : IClassFixture<HairpinFixture>
{
    private readonly HairpinFixture _fx;
    public LayoutTests(HairpinFixture fx) => _fx = fx;

    [Fact]
    public void Layout_HasOnePolygonPerResonatorPlusFeedsAndBoard()
    {
        var d = _fx.Design();
        var l = HairpinLayout.From(d);
        Assert.Equal(d.Resonators.Length, l.Polygons.Count(p => p.Layer == HairpinLayout.CopperLayer) - 2);
        Assert.Single(l.Polygons, p => p.Layer == HairpinLayout.BoardLayer);
        Assert.Equal(2, l.Ports.Count);
    }

    [Fact]
    public void Layout_PolygonsStayInsideBoard()
    {
        var l = HairpinLayout.From(_fx.Design());
        foreach (var p in l.Polygons)
            foreach (var (x, y) in p.Points)
            {
                Assert.InRange(x, l.Board.X0 - 1e-12, l.Board.X1 + 1e-12);
                Assert.InRange(y, l.Board.Y0 - 1e-12, l.Board.Y1 + 1e-12);
            }
    }

    [Fact]
    public void HairpinPolygon_MatchesDesignDimensions()
    {
        var d = _fx.Design();
        var l = HairpinLayout.From(d);
        var u = l.Polygons.First().Points;
        Assert.Equal(8, u.Length);
        Assert.Equal(d.Resonators[0].OuterWidth, u.Max(p => p.X) - u.Min(p => p.X), 9);
        Assert.Equal(d.Resonators[0].LeftArm.Y1, u.Max(p => p.Y), 9);
    }

    [Fact]
    public void Dxf_IsWellFormedAndInMillimetres()
    {
        var d = _fx.Design();
        var l = HairpinLayout.From(d);
        string dxf = DxfWriter.Write(l);
        var lines = dxf.Split('\n');
        Assert.Equal("EOF", lines[^2]);
        Assert.Equal(l.Polygons.Count, lines.Count(s => s == "SEQEND"));
        Assert.Equal(l.Polygons.Sum(p => p.Points.Length), lines.Count(s => s == "VERTEX"));
        // Group-code/value pairing: every even line is an integer code.
        for (int i = 0; i + 1 < lines.Length; i += 2) Assert.True(int.TryParse(lines[i], out _), lines[i]);
        // The widest coordinate in mm equals the board width.
        double maxX = 0;
        for (int i = 0; i + 1 < lines.Length; i += 2)
            if (lines[i] == "10") maxX = Math.Max(maxX, double.Parse(lines[i + 1], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(l.Board.X1 * 1e3, maxX, 4);
    }
}

public class DxfDumpTests : IClassFixture<HairpinFixture>
{
    private readonly HairpinFixture _fx;
    public DxfDumpTests(HairpinFixture fx) => _fx = fx;

    [Fact]
    public void Dump_WhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("FILTSYN_DXF_DUMP");
        if (string.IsNullOrEmpty(path)) return;
        DxfWriter.Write(HairpinLayout.From(_fx.Design()), path);
    }
}

public class PortEdgeTests : IClassFixture<HairpinFixture>
{
    private readonly HairpinFixture _fx;
    public PortEdgeTests(HairpinFixture fx) => _fx = fx;

    [Fact]
    public void Ports_SitOnBoardEdgeAndFeedsReachThem()
    {
        var l = HairpinLayout.From(_fx.Design());
        var b = l.Board;
        foreach (var (x, y) in l.Ports)
        {
            bool onEdge = Math.Abs(x - b.X0) < 1e-12 || Math.Abs(x - b.X1) < 1e-12 ||
                          Math.Abs(y - b.Y0) < 1e-12 || Math.Abs(y - b.Y1) < 1e-12;
            Assert.True(onEdge);
        }
        // The two feed polygons (last two copper entries) touch the board edge at the port.
        var feeds = l.Polygons.Where(p => p.Layer == HairpinLayout.CopperLayer).TakeLast(2).ToArray();
        for (int i = 0; i < 2; i++)
        {
            var (px, py) = l.Ports[i];
            Assert.Contains(feeds[i].Points, q => Math.Abs(q.X - px) < 1e-9 || Math.Abs(q.Y - py) < 1e-9);
            Assert.True(feeds[i].Points.Any(q => Math.Abs(q.X - b.X0) < 1e-12 || Math.Abs(q.X - b.X1) < 1e-12 ||
                                                 Math.Abs(q.Y - b.Y0) < 1e-12));
        }
    }
}
