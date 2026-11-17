using System.Text.Json.Nodes;

namespace FiltSyn.Plugin;

/// <summary>
/// Two things the host does not do for a plugin's cell, done here against the workspace folder circuitRF names in
/// <c>hello</c>: (1) find the technology's file name (the host delivers its display name, the file is named by its id),
/// and (2) after placement, rewrite the .cem's LayoutRef. While staged, circuitRF resolves LayoutRef beside the .cem
/// (<c>../3d/NAME.c3d</c>); once placed it resolves it from the workspace folder (<c>CELL/3d/CELL.c3d</c>), and the cell
/// may have been renamed in the placement dialog, so only a look at the workspace tells the final name.
/// </summary>
internal static class WorkspaceLinker
{
    public static string? FindTechFile(string? workspace, string? id)
    {
        try
        {
            if (string.IsNullOrEmpty(workspace)) return null;
            var dir = Path.Combine(workspace, "tech");
            if (!Directory.Exists(dir)) return null;
            var files = Directory.GetFiles(dir, "*.ctech");
            if (!string.IsNullOrEmpty(id))
            {
                var want = id.EndsWith(".ctech", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(id) : id;
                foreach (var f in files)
                {
                    if (string.Equals(Path.GetFileNameWithoutExtension(f), Path.GetFileNameWithoutExtension(want), StringComparison.OrdinalIgnoreCase))
                        return Path.GetFileName(f);
                }
                foreach (var f in files)
                {
                    try
                    {
                        var name = JsonNode.Parse(File.ReadAllText(f))?["Name"]?.GetValue<string>();
                        if (string.Equals(name, id, StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(f);
                    }
                    catch { /* unreadable file: skip */ }
                }
            }
            // Fall back to the workspace's default technology.
            var cws = Path.Combine(workspace, ".cws");
            if (File.Exists(cws) && JsonNode.Parse(File.ReadAllText(cws))?["DefaultTechRef"]?.GetValue<string>() is { Length: > 0 } d)
                return Path.GetFileName(d);
        }
        catch (Exception e) { Console.Error.WriteLine($"tech lookup failed: {e.Message}"); }
        return null;
    }

    public static void Start(string workspace)
    {
        var started = DateTime.UtcNow;
        var t = new Thread(() =>
        {
            try
            {
                while (DateTime.UtcNow - started < TimeSpan.FromMinutes(5))
                {
                    Thread.Sleep(1000);
                    if (FixOnce(workspace, started.AddSeconds(-5))) { Thread.Sleep(1500); FixOnce(workspace, started.AddSeconds(-5)); return; }
                }
            }
            catch (Exception e) { Console.Error.WriteLine($"link fix stopped: {e.Message}"); }
        }) { IsBackground = true, Name = "linker" };
        t.Start();
    }

    /// <summary>Rewrites every setup written since <paramref name="since"/> whose LayoutRef still starts with ../3d/.</summary>
    internal static bool FixOnce(string workspace, DateTime since)
    {
        bool any = false;
        if (!Directory.Exists(workspace)) return false;
        foreach (var cellDir in Directory.GetDirectories(workspace))
        {
            var em = Path.Combine(cellDir, "em");
            if (!Directory.Exists(em)) continue;
            foreach (var cem in Directory.GetFiles(em, "*.cem"))
            {
                if (File.GetLastWriteTimeUtc(cem) < since) continue;
                var text = File.ReadAllText(cem);
                const string key = "\"LayoutRef\": \"../3d/";
                int i = text.IndexOf(key, StringComparison.Ordinal);
                if (i < 0) continue;
                int start = i + key.Length, end = text.IndexOf('"', start);
                if (end < 0) continue;
                var file = text.Substring(start, end - start);
                if (!File.Exists(Path.Combine(cellDir, "3d", file))) continue;
                var cell = Path.GetFileName(cellDir);
                File.WriteAllText(cem, text.Substring(0, i) + $"\"LayoutRef\": \"{cell}/3d/{file}" + text.Substring(end));
                Console.Error.WriteLine($"re-pointed {cem} at {cell}/3d/{file}");
                any = true;
            }
        }
        return any;
    }
}
