using FiltSyn.Plugin;
using Xunit;

namespace FiltSyn.Core.Tests;

public class TouchstoneTests
{
    [Fact]
    public void Reads_MagnitudeAngle_GHz()
    {
        string p = Path.GetTempFileName();
        File.WriteAllText(p, "# GHz S MA R 50\n3.0 0.1 0 0.5 0 0.5 0 0.1 0\n3.1 0.2 0 0.1 0 0.1 0 0.2 0\n");
        var t = Touchstone.Read(p);
        Assert.NotNull(t);
        Assert.Equal(2, t!.Value.F.Length);
        Assert.Equal(-20.0, t.Value.S11dB[0], 6);
        Assert.Equal(20 * Math.Log10(0.5), t.Value.S21dB[0], 6);
        File.Delete(p);
    }

    [Fact]
    public void MissingFile_ReturnsNull() => Assert.Null(Touchstone.Read("/nonexistent/x.s2p"));
}
