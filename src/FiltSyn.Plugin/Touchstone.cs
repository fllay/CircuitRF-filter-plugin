using System.Globalization;
using System.Numerics;

namespace FiltSyn.Plugin;

/// <summary>Reads a 2-port Touchstone file (RI, MA or DB data; Hz, kHz, MHz or GHz) into S11 and S21 in dB.</summary>
internal static class Touchstone
{
    public static (double[] F, double[] S11dB, double[] S21dB)? Read(string path)
    {
        if (!File.Exists(path)) return null;
        double fScale = 1e9; string fmt = "MA";
        var f = new List<double>(); var s11 = new List<double>(); var s21 = new List<double>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Split('!')[0].Trim();
            if (line.Length == 0) continue;
            if (line[0] == '#')
            {
                var tok = line.Substring(1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var t in tok)
                {
                    switch (t.ToUpperInvariant())
                    {
                        case "HZ": fScale = 1; break;
                        case "KHZ": fScale = 1e3; break;
                        case "MHZ": fScale = 1e6; break;
                        case "GHZ": fScale = 1e9; break;
                        case "RI": case "MA": case "DB": fmt = t.ToUpperInvariant(); break;
                    }
                }
                continue;
            }
            var v = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            if (v.Length < 9) continue; // f, then S11 S21 S12 S22 as pairs
            Complex C(double a, double b) => fmt switch
            {
                "RI" => new Complex(a, b),
                "DB" => Complex.FromPolarCoordinates(Math.Pow(10, a / 20), b * Math.PI / 180),
                _ => Complex.FromPolarCoordinates(a, b * Math.PI / 180),
            };
            f.Add(v[0] * fScale);
            s11.Add(20 * Math.Log10(Math.Max(C(v[1], v[2]).Magnitude, 1e-12)));
            s21.Add(20 * Math.Log10(Math.Max(C(v[3], v[4]).Magnitude, 1e-12)));
        }
        return f.Count == 0 ? null : (f.ToArray(), s11.ToArray(), s21.ToArray());
    }
}
