using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace FiltSyn.Plugin;

/// <summary>
/// circuitRF plugin frames: [uint32 jsonLen][uint32 binLen][json UTF-8][binLen bytes of int64], little endian,
/// on stdin and stdout. stderr is the plugin's log.
/// </summary>
internal static class Wire
{
    public static (JsonObject Json, long[] Bin)? Read(Stream s)
    {
        var head = new byte[8];
        if (!ReadExactly(s, head)) return null;
        uint jl = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0, 4));
        uint bl = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4, 4));
        var jb = new byte[jl];
        if (!ReadExactly(s, jb)) return null;
        var bb = new byte[bl];
        if (!ReadExactly(s, bb)) return null;
        var json = JsonNode.Parse(Encoding.UTF8.GetString(jb)) as JsonObject
                   ?? throw new InvalidDataException("A frame's JSON is not an object.");
        var bin = new long[bl / 8];
        for (int i = 0; i < bin.Length; i++) bin[i] = BinaryPrimitives.ReadInt64LittleEndian(bb.AsSpan(i * 8, 8));
        return (json, bin);
    }

    public static void Write(Stream s, JsonObject json, long[]? bin = null)
    {
        var jb = Encoding.UTF8.GetBytes(json.ToJsonString());
        int n = bin?.Length ?? 0;
        var head = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(0, 4), (uint)jb.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4, 4), (uint)(n * 8));
        s.Write(head);
        s.Write(jb);
        if (n > 0)
        {
            var bb = new byte[n * 8];
            for (int i = 0; i < n; i++) BinaryPrimitives.WriteInt64LittleEndian(bb.AsSpan(i * 8, 8), bin![i]);
            s.Write(bb);
        }
        s.Flush();
    }

    private static bool ReadExactly(Stream s, byte[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int r = s.Read(buf, off, buf.Length - off);
            if (r <= 0) return false;
            off += r;
        }
        return true;
    }
}
