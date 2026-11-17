using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FiltSyn.Core;

namespace FiltSyn.Plugin;

internal static class Program
{
    private const int ApiVersion = 1;
    internal static readonly ConcurrentQueue<(JsonObject Json, long[] Bin)?> Queue = new();
    internal static readonly SemaphoreSlim Signal = new(0);

    public static int Main()
    {
        // stdout carries frames only; anything a library prints goes to the log on stderr.
        var stdout = Console.OpenStandardOutput();
        var stdin = Console.OpenStandardInput();
        Console.SetOut(Console.Error);

        var queue = Queue;
        var signal = Signal;
        var reader = new Thread(() =>
        {
            try { while (Wire.Read(stdin) is { } f) { queue.Enqueue(f); signal.Release(); } }
            catch (Exception e) { Console.Error.WriteLine($"reader: {e.Message}"); }
            finally { queue.Enqueue(null); signal.Release(); }
        }) { IsBackground = true };
        reader.Start();

        var session = new Session(stdout);
        while (true)
        {
            signal.Wait();
            queue.TryDequeue(out var item);
            if (item is null) return 0;
            var msg = item.Value.Json;
            string type = msg["type"]?.GetValue<string>() ?? "";
            if (type == "bye") return 0;
            if (type == "cancel") continue;

            // A newer update supersedes an older one: answer the old ones empty and do only the last.
            while (type == "update" && queue.TryPeek(out var next) && next is { } nx &&
                   nx.Json["type"]?.GetValue<string>() == "update")
            {
                Wire.Write(stdout, new JsonObject { ["type"] = "reply", ["id"] = msg["id"]?.DeepClone() });
                signal.Wait();
                queue.TryDequeue(out var taken);
                msg = taken!.Value.Json;
            }

            try { session.Handle(msg, type); }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                Wire.Write(stdout, Error(msg, e is InvalidOperationException or ArgumentException ? e.Message : $"Internal error: {e.Message}"));
            }
        }
    }

    internal static JsonObject Error(JsonObject request, string text) => new()
    {
        ["type"] = "reply", ["id"] = request["id"]?.DeepClone(),
        ["messages"] = new JsonArray(new JsonObject { ["severity"] = "error", ["text"] = text }),
    };
}

internal sealed class Session
{
    private readonly Stream _out;
    private readonly DesignEngine _engine = new();
    private string _staging = Path.GetTempPath();
    private bool _techDumped;
    private string? _workspace;

    public Session(Stream stdout) => _out = stdout;

    public void Handle(JsonObject msg, string type)
    {
        switch (type)
        {
            case "hello":
                _staging = msg["staging"]?.GetValue<string>() ?? _staging;
                _workspace = msg["workspace"] switch
                {
                    JsonValue v when v.TryGetValue<string>(out var w) => w,
                    JsonObject o => (o["path"] ?? o["root"] ?? o["folder"])?.GetValue<string>(),
                    _ => null,
                };
                Wire.Write(_out, new JsonObject { ["type"] = "hello", ["id"] = msg["id"]?.DeepClone(), ["apiVersion"] = 1 });
                break;
            case "action" when (msg["name"]?.GetValue<string>() ?? "") is "calibrate" or "resetCalibration":
                Calibrate(msg, (msg["name"]?.GetValue<string>() ?? "") == "resetCalibration");
                break;
            case "update":
            case "action" when (msg["name"]?.GetValue<string>() ?? "") != "commit":
            case "event":
                Reply(msg);
                break;
            case "commit":
            case "action":
                Commit(msg);
                break;
            default:
                Console.Error.WriteLine($"ignoring message type '{type}'");
                break;
        }
    }

    /// <summary>
    /// Asks circuitRF to do some work (section 7 of the plugin contract) and waits for its hostReply. Anything else that
    /// arrives meanwhile is put back on the queue for the main loop.
    /// </summary>
    private JsonObject? HostCall(JsonObject request, JsonObject call, int timeoutMs)
    {
        call["type"] = "host";
        call["id"] = request["id"]?.DeepClone();
        Wire.Write(_out, call);
        var stash = new List<(JsonObject Json, long[] Bin)?>();
        JsonObject? answer = null;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        try
        {
            while (true)
            {
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0 || !Program.Signal.Wait(left)) break;
                Program.Queue.TryDequeue(out var item);
                if (item is null) { stash.Add(null); break; }
                var j = item.Value.Json;
                if (j["type"]?.GetValue<string>() == "hostReply" && JsonNode.DeepEquals(j["id"], request["id"])) { answer = j; break; }
                stash.Add(item);
            }
        }
        finally
        {
            foreach (var it in stash) { Program.Queue.Enqueue(it); Program.Signal.Release(); }
        }
        return answer;
    }

    /// <summary>
    /// Evaluates the coupled-line design with circuitRF's own MCLIN and MLIN models (host service "simulate") on the same
    /// frequency grid as the preview. Returns null with a reason when it cannot.
    /// </summary>
    private (double[] S11dB, double[] S21dB)? CircuitRfCheck(JsonObject msg, FilterResult r, out string reason)
    {
        reason = "";
        if (r.Hairpin) { reason = "the circuitRF circuit check covers coupled-line filters only."; return null; }
        if (r.Substrate.IsStripline) { reason = "the circuitRF circuit check covers microstrip only (its coupled-line element is a microstrip model)."; return null; }
        if (r.CoupledDesign is not CoupledLineDesign d) { reason = "no coupled-line design to check."; return null; }
        var techFile = WorkspaceLinker.FindTechFile(_workspace, r.Tech.Id);
        if (techFile is null || string.IsNullOrEmpty(_workspace)) { reason = "the workspace's technology file was not found."; return null; }
        var f = r.Verification.FrequenciesHz;
        var cnlPath = Path.Combine(_staging, "crf_check.cnl");
        File.WriteAllText(cnlPath, CircuitRfNetlist.Write(d, Path.Combine(_workspace!, "tech", techFile), f[0], f[^1], f.Length));
        var reply = HostCall(msg, new JsonObject { ["call"] = "simulate", ["cnl"] = cnlPath, ["analysis"] = "SP1" }, 25000);
        if (reply is null) { reason = "circuitRF did not answer the simulate request in time."; return null; }
        if (reply["error"]?.GetValue<string>() is { Length: > 0 } err) { reason = err; return null; }
        var ts = reply["touchstone"]?.GetValue<string>() ?? reply["result"]?["touchstone"]?.GetValue<string>();
        if (string.IsNullOrEmpty(ts)) { reason = "circuitRF returned no Touchstone file."; return null; }
        if (!Path.IsPathRooted(ts)) ts = Path.Combine(_staging, ts);
        var parsed = Touchstone.Read(ts);
        if (parsed is null || parsed.Value.F.Length != f.Length) { reason = "the circuitRF result could not be read on the preview's frequency grid."; return null; }
        return (parsed.Value.S11dB, parsed.Value.S21dB);
    }

    private void Progress(JsonObject msg, string text)
        => Wire.Write(_out, new JsonObject
        {
            ["type"] = "progress", ["id"] = msg["id"]?.DeepClone(), ["fraction"] = 0.0, ["text"] = text,
        });

    private JsonObject ParamsOf(JsonObject msg)
    {
        var p = msg["params"] as JsonObject ?? new JsonObject();
        if (!_techDumped && p["technology"] is { } t)
        {
            _techDumped = true;
            try { File.WriteAllText(Path.Combine(_staging, "technology-param.json"), t.ToJsonString(new() { WriteIndented = true })); }
            catch { /* diagnostics only */ }
        }
        return p;
    }

    private string? FilePath(JsonNode? n)
    {
        string? s = n switch
        {
            JsonValue v when v.TryGetValue<string>(out var t) => t,
            JsonObject o => (o["path"] ?? o["file"])?.GetValue<string>(),
            _ => null,
        };
        s = s?.Trim().Trim('"', '\'');
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("~/")) s = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), s[2..]);
        if (!Path.IsPathRooted(s) && !string.IsNullOrEmpty(_workspace)) s = Path.Combine(_workspace!, s);
        return s;
    }

    /// <summary>
    /// "Calibrate from EM result" reads the EM-simulated Touchstone of the cell this panel placed, measures its -3 dB centre
    /// and bandwidth, and scales the design target so the next cell lands on the specification. "Reset calibration" clears it.
    /// </summary>
    private void Calibrate(JsonObject msg, bool reset)
    {
        var p = ParamsOf(msg);
        double fc = DesignEngine.Num(p, "f0Corr", 1.0), bc = DesignEngine.Num(p, "bwCorr", 1.0);
        var extra = new List<(string, string)>();
        if (reset)
        {
            fc = bc = 1.0;
            extra.Add(("info", "EM calibration cleared: the filter is designed for the specified centre and bandwidth."));
        }
        else
        {
            try
            {
                string? path = FilePath(p["emResult"]);
                if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Choose the EM result (.s2p) of the placed cell first.");
                var ts = Touchstone.Read(path) ?? throw new InvalidOperationException($"Could not read the EM result {path}.");
                Progress(msg, "Measuring the EM result");
                var p0 = (JsonObject)p.DeepClone();
                p0["f0Corr"] = 1.0; p0["bwCorr"] = 1.0; p0["optimize"] = false;
                var spec = _engine.Run(p0);
                double target = spec.Verification.Ideal.Bandwidth3dBHz;
                var u = EmCalibration.Compute(spec.F0, target, fc, bc, ts.F, ts.S21dB, ts.S11dB);
                string measured = $"EM result: centre {u.MeasuredCenterHz / 1e9:0.####} GHz, 3 dB bandwidth {u.MeasuredBw3dBHz / 1e6:0.#} MHz " +
                                  $"(wanted {spec.F0 / 1e9:0.####} GHz and {target / 1e6:0.#} MHz).";
                if (u.WithinTolerance)
                    extra.Add(("info", measured + " Within tolerance; the calibration is unchanged."));
                else
                {
                    fc = u.F0Corr; bc = u.BwCorr;
                    extra.Add(("info", measured + $" Design factors are now {fc:0.####} (centre) and {bc:0.####} (bandwidth). " +
                                       "Place the cell again, run EM, then calibrate again until the result stops moving."));
                }
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or FormatException)
            {
                Wire.Write(_out, Program.Error(msg, e.Message));
                return;
            }
        }
        p["f0Corr"] = fc; p["bwCorr"] = bc;
        Reply(msg, new JsonObject { ["f0Corr"] = fc, ["bwCorr"] = bc }, extra);
    }

    private void Reply(JsonObject msg, JsonObject? set = null, List<(string Severity, string Text)>? extra = null)
    {
        var p = ParamsOf(msg);
        var enabled = new JsonObject
        {
            ["ripple"] = DesignEngine.Str(p, "response", "Chebyshev") == "Chebyshev",
            ["tipGap"] = DesignEngine.Str(p, "filter", "Coupled-line") == "Open-loop ring",
            ["orientation"] = DesignEngine.Str(p, "filter", "Coupled-line") == "Open-loop ring",
            ["calibrate"] = !string.IsNullOrWhiteSpace(FilePath(p["emResult"])),
        };
        FilterResult r;
        try { r = _engine.Run(p, t => Progress(msg, t)); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or ArgumentOutOfRangeException)
        {
            var err = Program.Error(msg, e.Message);
            err["enabled"] = enabled;
            Wire.Write(_out, err);
            return;
        }

        string stripLayer = r.Substrate.IsStripline ? "Inner Copper" : "Top Copper";
        var (art, bin) = Views.Artwork(r, stripLayer);
        var v = r.Verification;
        var messages = new JsonArray();
        foreach (var (sev, text) in extra ?? new()) messages.Add(new JsonObject { ["severity"] = sev, ["text"] = text });
        (double[] S11dB, double[] S21dB)? crf = null;
        if (DesignEngine.Bool(p, "crfCheck", false))
        {
            Progress(msg, "Checking with circuitRF's own circuit models");
            try
            {
                crf = CircuitRfCheck(msg, r, out var why);
                if (crf is null) messages.Add(new JsonObject { ["severity"] = "warning", ["text"] = $"circuitRF circuit check skipped: {why}" });
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                messages.Add(new JsonObject { ["severity"] = "warning", ["text"] = $"circuitRF circuit check failed: {e.Message}" });
            }
        }
        foreach (var w in r.Warnings) messages.Add(new JsonObject { ["severity"] = "warning", ["text"] = w });
        foreach (var w in r.Notes) messages.Add(new JsonObject { ["severity"] = "info", ["text"] = w });
        foreach (var w in MediumNotes(r)) messages.Add(new JsonObject { ["severity"] = "info", ["text"] = w });

        double worstRl = 0;
        {
            double lo = r.F0 - r.Bandwidth / 2, hi = r.F0 + r.Bandwidth / 2;
            double worst = double.NegativeInfinity;
            for (int i = 0; i < v.FrequenciesHz.Length; i++)
                if (v.FrequenciesHz[i] >= lo && v.FrequenciesHz[i] <= hi) worst = Math.Max(worst, v.CircuitS11dB[i]);
            worstRl = double.IsNegativeInfinity(worst) ? v.Circuit.WorstInBandReturnLossDb : -worst;
        }
        string summary =
            $"{(r.OpenLoop ? "Open-loop ring" : r.Hairpin ? "Hairpin" : "Coupled-line")} on {(r.Substrate.IsStripline ? "stripline" : "microstrip")}, " +
            $"Er {r.Substrate.Er:0.###}, {(r.Substrate.IsStripline ? "b" : "h")} {r.Substrate.H * 1e3:0.###} mm. " +
            $"Circuit model: centre {v.Circuit.CenterHz / 1e9:0.####} GHz, 3 dB bandwidth {v.Circuit.Bandwidth3dBHz / 1e6:0.#} MHz " +
            $"(ideal {v.Ideal.Bandwidth3dBHz / 1e6:0.#}), peak {v.Circuit.PeakS21dB:0.##} dB, worst return loss across the specified bandwidth {worstRl:0.#} dB.";

        var reply = new JsonObject
        {
            ["type"] = "reply", ["id"] = msg["id"]?.DeepClone(),
            ["readouts"] = new JsonObject { ["summary"] = summary, ["dims"] = Views.Table(r) },
            ["views"] = new JsonObject { ["response"] = Views.Response(r, crf), ["artwork"] = art, ["dims"] = Views.Table(r) },
            ["messages"] = messages, ["enabled"] = enabled,
        };
        if (set is not null) reply["set"] = set;
        Wire.Write(_out, reply, bin);
    }

    private static IEnumerable<string> MediumNotes(FilterResult r)
    {
        if (r.OpenLoop)
            yield return "Open-loop rings: the ring ends face each other across the open gap, modelled with a quasi-static gap capacitance (valid for gaps of roughly 0.1 to 1 times the line width). Bends and the gap ends are not modelled, so the first EM run lands several percent high (more at higher frequency, where the ring is smaller): run EM, calibrate and place again.";
        if (!r.Substrate.IsStripline) yield break;
        yield return "Stripline: epsilon_eff = Er, no dispersion; the open-end extension is an approximation.";
        if (r.Hairpin) yield return "Stripline hairpins couple only through the folds, so the reachable bandwidth is lower and the gaps tighter than in microstrip.";
    }

    private void Commit(JsonObject msg)
    {
        var p = ParamsOf(msg);
        string cell = DesignEngine.Str(p, "cellName", "FILTER").Trim();
        if (cell.Length == 0 || cell.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
            throw new InvalidOperationException("Give the cell a name made of letters, digits and underscores.");
        var r = _engine.Run(p, t => Progress(msg, t));
        var messages = new List<string>();
        var files = CellWriter.Write(_staging, cell, r, p, messages, _workspace);
        var reply = new JsonObject
        {
            ["type"] = "placed", ["id"] = msg["id"]?.DeepClone(),
            ["cells"] = new JsonArray(new JsonObject
            {
                ["name"] = cell, ["files"] = new JsonArray(files.Select(f => (JsonNode?)f).ToArray()),
            }),
        };
        if (messages.Count > 0)
            reply["messages"] = new JsonArray(messages.Select(m => (JsonNode?)new JsonObject { ["severity"] = "warning", ["text"] = m }).ToArray());
        Wire.Write(_out, reply);
        if (!string.IsNullOrEmpty(_workspace)) WorkspaceLinker.Start(_workspace!);
    }
}
