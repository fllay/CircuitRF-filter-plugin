using System.Globalization;
using System.Text;

namespace FiltSyn.Core;

/// <summary>
/// Writes a parallel-coupled-line filter as a circuitRF netlist (.cnl) built from circuitRF's own MCLIN and MLIN
/// elements, so circuitRF's solver and line models can evaluate it. Strip j (j = 0..N+1) is the second half of
/// section j-1 and the first half of section j; strip 0 and strip N+1 are the feeds.
/// </summary>
public static class CircuitRfNetlist
{
    private static string F(double metres) => (metres * 1e3).ToString("0.######", CultureInfo.InvariantCulture) + " mm";

    /// <param name="technologyPath">Path as written on the netlist's technology line, relative to the .cnl.</param>
    /// <param name="openEnds">Add a short open stub of the end extension at every open end, as the plugin's own model does.</param>
    /// <param name="feedLength">Length of the 50 ohm feed lines (the EM cell has 5 mm ones).</param>
    public static string Write(CoupledLineDesign d, string technologyPath, double startHz, double stopHz, int points,
        bool openEnds = true, double feedLength = 5e-3, string analysisName = "SP1")
    {
        var sb = new StringBuilder();
        int m = d.Sections.Length;
        sb.AppendLine($"; Parallel-coupled-line bandpass filter, order {m - 1}, f0 = {d.F0Hz / 1e6:0.###} MHz, BW = {d.BandwidthHz / 1e6:0.###} MHz");
        sb.AppendLine("; Written by filtsyn. Strip j is the second half of section j-1 and the first half of section j.");
        sb.AppendLine($"technology \"{technologyPath}\"");
        sb.AppendLine();
        sb.AppendLine("Port:P1 in 0 Num=1 Z=50 Ohm");
        sb.AppendLine("Port:P2 out 0 Num=2 Z=50 Ohm");
        sb.AppendLine($"MLIN:F1 in a0L W={F(d.FeedWidth)} L={F(feedLength)}");
        sb.AppendLine($"MLIN:F2 b{m - 1}R out W={F(d.FeedWidth)} L={F(feedLength)}");
        for (int j = 0; j < m; j++)
        {
            var s = d.Sections[j];
            string aL = $"a{j}L", aR = $"a{j}R";
            string bL = $"b{j}L", bR = $"b{j}R";
            // Resonator joints: the right end of strip j's lower half meets the left end of its upper half.
            if (j >= 1) aL = $"b{j - 1}R";
            sb.AppendLine($"MCLIN:K{j} {aL} {aR} {bL} {bR} W={F(s.Width)} S={F(s.Gap)} L={F(s.Length)}");
            if (openEnds)
            {
                double dl = HairpinSynthesizer.OpenEndExtension(d.Substrate, s.Width);
                sb.AppendLine($"MLIN:EA{j} {aR} {aR}o W={F(s.Width)} L={F(dl)}");
                sb.AppendLine($"MLIN:EB{j} {bL} {bL}o W={F(s.Width)} L={F(dl)}");
            }
        }
        sb.AppendLine();
        sb.AppendLine($"analysis {analysisName} type=sparam start={startHz / 1e9:0.######} GHz stop={stopHz / 1e9:0.######} GHz npts={points}");
        return sb.ToString();
    }
}
