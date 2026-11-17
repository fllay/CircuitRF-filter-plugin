using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FiltSyn.Core;

namespace FiltSyn.App;

public partial class MainWindow : Window
{
    private FilterLayout? _layout;
    private HairpinCalibration? _cal;
    private (Substrate S, double F0)? _calKey;
    private bool _busy;

    // Preset list (every stocked Rogers thickness, then FR-4 and alumina); the last entry (null) is Custom.
    private static readonly (string Label, Func<Substrate>? Make)[] Presets = BuildPresets();

    private static (string Label, Func<Substrate>? Make)[] BuildPresets()
    {
        var list = new List<(string, Func<Substrate>?)>();
        foreach (var m in RogersMaterials.All)
            foreach (int mil in m.ThicknessMil)
            {
                var mat = m; int t = mil;
                list.Add(($"Rogers {m.Name}, {mil} mil", () => mat.Make(t)));
            }
        list.Add(("FR-4, 1.6 mm", () => Substrate.Fr4()));
        list.Add(("Alumina, 25 mil", () => Substrate.Alumina()));
        list.Add(("Custom", null));
        return list.ToArray();
    }

    public MainWindow()
    {
        InitializeComponent();
        SubstrateBox.ItemsSource = Presets.Select(p => p.Label).ToList();
        SubstrateBox.SelectedIndex = 0;
        SubstrateBox.SelectionChanged += (_, _) => ApplyPreset();
        MediumBox.SelectionChanged += async (_, _) =>
        {
            HLabel.Text = MediumBox.SelectedIndex == 1 ? "b (mm, plane spacing)" : "h (mm)";
            StackNote.IsVisible = MediumBox.SelectedIndex == 1;
            ApplyPreset();
            if (!IsInitialized || !IsLoaded) return;
            await SynthesizeAsync();
        };
        FilterBox.SelectionChanged += async (_, _) =>
        {
            if (!IsInitialized || !IsLoaded) return;
            // Typical bandwidths differ a lot: hairpin is a narrow-band topology, coupled-line a moderate one.
            BwBox.Text = FilterBox.SelectedIndex == 1 ? "200" : "30";
            await SynthesizeAsync();
        };
        ApplyPreset();
        SynthButton.Click += async (_, _) => await SynthesizeAsync();
        ExportButton.Click += async (_, _) => await ExportDxfAsync();
        Opened += async (_, _) => await SynthesizeAsync();
    }

    private void ApplyPreset()
    {
        int i = SubstrateBox.SelectedIndex;
        if (i < 0 || i >= Presets.Length || Presets[i].Make is not { } make) return;
        var s = make();
        // Stripline presets are stack-ups of two identical cores around the strip: b = 2 x core thickness.
        if (MediumBox.SelectedIndex == 1) s = s.AsStriplineFromCores();
        ErBox.Text = s.Er.ToString("0.###", CultureInfo.InvariantCulture);
        HBox.Text = (s.H * 1e3).ToString("0.###", CultureInfo.InvariantCulture);
        TanDBox.Text = s.TanD.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static IEnumerable<string> MediumNotes(Substrate sub, bool hairpin)
    {
        if (!sub.IsStripline) yield break;
        yield return "Stripline: epsilon_eff = Er (no dispersion); open-end extension is an approximation. Substrate b is the full ground-plane spacing with the strip centred; the layout assumes an identical dielectric above and below.";
        if (hairpin)
            yield return "Stripline hairpins couple only through the folds (homogeneous medium), so the reachable bandwidth is lower and the gaps tighter than in microstrip.";
    }

    private static bool TryParse(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private Substrate? ReadSubstrate()
    {
        if (!TryParse(ErBox.Text, out double er) || er < 1 ||
            !TryParse(HBox.Text, out double hMm) || hMm <= 0 ||
            !TryParse(TanDBox.Text, out double tand) || tand < 0) return null;
        int idx = SubstrateBox.SelectedIndex;
        var preset = idx >= 0 && idx < Presets.Length && Presets[idx].Make is { } make
            ? make()
            : new Substrate(er, hMm * 1e-3);
        var sub = preset with { Er = er, H = hMm * 1e-3, TanD = tand };
        return MediumBox.SelectedIndex == 1 ? sub.AsStripline() : sub.AsMicrostrip();
    }

    private async Task SynthesizeAsync()
    {
        if (_busy) return;
        StatusText.Text = "";
        _layout = null;
        LayoutPreview.Layout = null;
        ExportButton.IsEnabled = false;

        var sub = ReadSubstrate();
        if (!TryParse(F0Box.Text, out double f0Mhz) || f0Mhz <= 0 ||
            !TryParse(BwBox.Text, out double bwMhz) || bwMhz <= 0 || bwMhz >= f0Mhz ||
            !TryParse(RippleBox.Text, out double ripple) || ripple <= 0 || sub is null)
        {
            StatusText.Text = "Enter positive numbers (bandwidth must be smaller than center frequency; Er >= 1).";
            return;
        }

        int n = (int)(OrderBox.Value ?? 4);
        bool cheb = TypeBox.SelectedIndex == 0;
        double f0 = f0Mhz * 1e6, bw = bwMhz * 1e6;

        double[] g = cheb ? Prototype.Chebyshev(n, ripple) : Prototype.Butterworth(n);
        var cm = CouplingMatrix.FromPrototype(g, f0, bw);
        DrawIdeal(cm, f0, bw);
        OutputBox.Text = DescribePrototype(g, cm, bw / f0);

        if (n < 2 && FilterBox.SelectedIndex == 0)
        {
            StatusText.Text = "Hairpin layout needs order 2 or more.";
            return;
        }

        if (FilterBox.SelectedIndex == 1)
        {
            await SynthesizeCoupledAsync(sub, g, cm, f0, bw);
            return;
        }

        _busy = true;
        SynthButton.IsEnabled = false;
        StatusText.Foreground = Avalonia.Media.Brushes.Gray;
        StatusText.Text = _calKey is { } k && k.S == sub && k.F0 == f0
            ? "Synthesizing hairpin layout..."
            : "Calibrating coupling and Qe curves against the circuit model (a few seconds)...";

        var cached = _calKey is { } kk && kk.S == sub && kk.F0 == f0 ? _cal : null;
        try
        {
            var result = await Task.Run(() =>
            {
                var cal = cached;
                if (cal is null)
                {
                    double w = Microstrip.WidthForZ0(sub, 50.0);
                    cal = HairpinCalibration.Run(sub, f0, w, 2.0 * w, HairpinSynthesizer.OpenEndExtension(sub, w));
                }
                var d = HairpinSynthesizer.SynthesizeCalibrated(sub, g, f0, bw, null, cal);
                d = HairpinTuner.CenterResponse(d);
                var v = HairpinVerification.Run(d, g);
                return (cal, d, v);
            });
            _cal = result.cal;
            _calKey = (sub, f0);
            _layout = HairpinLayout.From(result.d);
            LayoutPreview.Layout = _layout;
            ExportButton.IsEnabled = true;
            DrawVerification(result.v, cm, f0, bw);
            OutputBox.Text = DescribePrototype(g, cm, bw / f0) + Environment.NewLine + DescribeDesign(result.d, result.v);
            StatusText.Foreground = Avalonia.Media.Brushes.Gray;
            StatusText.Text = string.Join(Environment.NewLine, result.d.Warnings.Concat(MediumNotes(sub, hairpin: true)));
        }
        catch (InvalidOperationException ex)
        {
            StatusText.Foreground = Avalonia.Media.Brushes.OrangeRed;
            StatusText.Text = ex.Message;
        }
        catch (ArgumentException ex)
        {
            StatusText.Foreground = Avalonia.Media.Brushes.OrangeRed;
            StatusText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            SynthButton.IsEnabled = true;
        }
    }

    private async Task SynthesizeCoupledAsync(Substrate sub, double[] g, CouplingMatrix cm, double f0, double bw)
    {
        _busy = true;
        SynthButton.IsEnabled = false;
        StatusText.Foreground = Avalonia.Media.Brushes.Gray;
        StatusText.Text = "Synthesizing coupled-line layout...";
        try
        {
            var result = await Task.Run(() =>
            {
                var d = CoupledLineSynthesizer.Synthesize(sub, g, f0, bw);
                d = CoupledLineTuner.CenterResponse(d);
                var v = CoupledLineVerification.Run(d, g);
                return (d, v);
            });
            _layout = CoupledLineLayout.From(result.d);
            LayoutPreview.Layout = _layout;
            ExportButton.IsEnabled = true;
            DrawVerification(result.v, cm, f0, bw);
            OutputBox.Text = DescribePrototype(g, cm, bw / f0) + Environment.NewLine + DescribeCoupled(result.d, result.v);
            StatusText.Text = string.Join(Environment.NewLine, result.d.Warnings.Concat(MediumNotes(sub, hairpin: false)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            StatusText.Foreground = Avalonia.Media.Brushes.OrangeRed;
            StatusText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            SynthButton.IsEnabled = true;
        }
    }

    private static string DescribeCoupled(CoupledLineDesign d, HairpinVerification v)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sections (mm)  width  gap  length  Z0e/Z0o");
        for (int i = 0; i < d.Sections.Length; i++)
        {
            var s = d.Sections[i];
            sb.AppendLine($"{i + 1}: {s.Width * 1e3:F3} {s.Gap * 1e3:F3} {s.Length * 1e3:F3}  {s.Z0e:F1}/{s.Z0o:F1}");
        }
        sb.AppendLine($"feed width = {d.FeedWidth * 1e3:F3}");
        sb.AppendLine();
        sb.AppendLine("Circuit vs ideal");
        sb.AppendLine($"3 dB BW  = {v.Circuit.Bandwidth3dBHz / 1e6:F2} MHz (ideal {v.Ideal.Bandwidth3dBHz / 1e6:F2})");
        sb.AppendLine($"centre   = {v.Circuit.CenterHz / 1e6:F2} MHz");
        sb.AppendLine($"RMS |S21| error = {v.RmsS21Error:F4}");
        return sb.ToString();
    }

    private void DrawIdeal(CouplingMatrix cm, double f0, double bw)
    {
        int points = 801;
        double fLo = Math.Max(f0 - 3 * bw, f0 * 0.2), fHi = f0 + 3 * bw;
        var freqs = new double[points];
        var fMhz = new double[points];
        for (int i = 0; i < points; i++)
        {
            freqs[i] = fLo + (fHi - fLo) * i / (points - 1);
            fMhz[i] = freqs[i] / 1e6;
        }
        var (s11, s21) = cm.Sweep(freqs);

        Plot.Plot.Clear();
        var l21 = Plot.Plot.Add.Scatter(fMhz, s21);
        l21.LegendText = "S21 ideal";
        l21.MarkerSize = 0;
        var l11 = Plot.Plot.Add.Scatter(fMhz, s11);
        l11.LegendText = "S11 ideal";
        l11.MarkerSize = 0;
        Plot.Plot.Axes.SetLimits(fMhz[0], fMhz[^1], -80, 2);
        Plot.Plot.XLabel("Frequency (MHz)");
        Plot.Plot.YLabel("Magnitude (dB)");
        Plot.Plot.ShowLegend();
        Plot.Refresh();
    }

    private void DrawVerification(HairpinVerification v, CouplingMatrix cm, double f0, double bw)
    {
        DrawIdeal(cm, f0, bw);
        var fMhz = v.FrequenciesHz.Select(f => f / 1e6).ToArray();
        var c21 = Plot.Plot.Add.Scatter(fMhz, v.CircuitS21dB);
        c21.LegendText = "S21 layout (circuit model)";
        c21.MarkerSize = 0;
        c21.LinePattern = ScottPlot.LinePattern.Dashed;
        var c11 = Plot.Plot.Add.Scatter(fMhz, v.CircuitS11dB);
        c11.LegendText = "S11 layout (circuit model)";
        c11.MarkerSize = 0;
        c11.LinePattern = ScottPlot.LinePattern.Dashed;
        Plot.Plot.ShowLegend();
        Plot.Refresh();
    }

    private static string DescribePrototype(double[] g, CouplingMatrix cm, double fbw)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FBW = {fbw:F4}");
        sb.AppendLine();
        for (int i = 0; i < g.Length; i++) sb.AppendLine($"g{i} = {g[i]:F4}");
        sb.AppendLine();
        sb.AppendLine($"Qe(in)  = {cm.QeInPhysical:F3}");
        sb.AppendLine($"Qe(out) = {cm.QeOutPhysical:F3}");
        for (int i = 0; i < cm.K.Length; i++) sb.AppendLine($"k{i + 1},{i + 2} = {cm.K[i]:F5}");
        return sb.ToString();
    }

    private static string DescribeDesign(HairpinDesign d, HairpinVerification v)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Layout (mm)");
        sb.AppendLine($"resonator width = {d.ResonatorWidth * 1e3:F3}");
        sb.AppendLine($"feed width      = {d.FeedWidth * 1e3:F3}");
        sb.AppendLine($"arm gap         = {d.ArmGap * 1e3:F3}");
        sb.AppendLine($"arm length      = {d.ArmLength * 1e3:F3}");
        sb.AppendLine($"tap distance    = {d.TapDistance * 1e3:F3}");
        for (int i = 0; i < d.Gaps.Length; i++) sb.AppendLine($"gap {i + 1},{i + 2}       = {d.Gaps[i] * 1e3:F3}");
        sb.AppendLine($"size            = {d.TotalWidth * 1e3:F1} x {d.Height * 1e3:F1}");
        sb.AppendLine();
        sb.AppendLine("Circuit vs ideal");
        sb.AppendLine($"3 dB BW  = {v.Circuit.Bandwidth3dBHz / 1e6:F2} MHz (ideal {v.Ideal.Bandwidth3dBHz / 1e6:F2})");
        sb.AppendLine($"centre   = {v.Circuit.CenterHz / 1e6:F2} MHz");
        sb.AppendLine($"RMS |S21| error = {v.RmsS21Error:F4}");
        return sb.ToString();
    }

    private async Task ExportDxfAsync()
    {
        if (_layout is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export layout as DXF",
            SuggestedFileName = FilterBox.SelectedIndex == 1 ? "coupled_line_filter" : "hairpin_filter",
            DefaultExtension = "dxf",
            FileTypeChoices = new[] { new FilePickerFileType("DXF drawing") { Patterns = new[] { "*.dxf" } } },
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(DxfWriter.Write(_layout));
            StatusText.Foreground = Avalonia.Media.Brushes.Gray;
            StatusText.Text = $"Saved {file.Name} (millimetres, layers COPPER and BOARD).";
        }
        catch (Exception ex)
        {
            StatusText.Foreground = Avalonia.Media.Brushes.OrangeRed;
            StatusText.Text = "Could not save DXF: " + ex.Message;
        }
    }
}
