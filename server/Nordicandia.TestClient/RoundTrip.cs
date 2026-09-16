using System.Collections;
using MessagePack;
using MessagePack.Resolvers;
using Game;

namespace Nordicandia.TestClient;

/// <summary>Finds fields that the server-side DTOs drop when they unpack and repack a
/// character save. Reads the original and the server-roundtripped bytes with a contractless
/// resolver and recursively diffs the two object graphs.</summary>
public static class RoundTrip
{
    private static readonly MessagePackSerializerOptions Plain = MessagePackSerializerOptions.Standard
        .WithResolver(ContractlessStandardResolver.Instance);

    public static int Run(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var lz4 = Plain.WithCompression(MessagePackCompression.Lz4BlockArray);
        var server = MessagePackSerializer.DefaultOptions.WithCompression(MessagePackCompression.Lz4BlockArray);

        var original = MessagePackSerializer.Deserialize<object>(bytes, lz4);
        var typed = MessagePackSerializer.Deserialize<SerializedCharacterData.SerializedData>(bytes, server);
        var again = MessagePackSerializer.Serialize(typed, server);
        var roundtripped = MessagePackSerializer.Deserialize<object>(again, lz4);

        Console.WriteLine($"bytes {bytes.Length} -> {again.Length}");
        var diffs = new List<string>();
        Diff(original, roundtripped, "$", diffs);
        foreach (var d in diffs.Take(80)) Console.WriteLine(d);
        Console.WriteLine($"DIFFS {diffs.Count}");
        return diffs.Count == 0 ? 0 : 1;
    }

    private static void Diff(object a, object b, string path, List<string> diffs)
    {
        if (a is IDictionary da && b is IDictionary db)
        {
            foreach (var k in da.Keys)
            {
                var key = k?.ToString();
                if (!db.Contains(k)) { diffs.Add($"MISSING {path}.{key}"); continue; }
                Diff(da[k], db[k], path + "." + key, diffs);
            }
            foreach (var k in db.Keys)
                if (!da.Contains(k)) diffs.Add($"EXTRA {path}.{k}");
            return;
        }
        if (a is IList la && b is IList lb)
        {
            for (var i = 0; i < Math.Max(la.Count, lb.Count); i++)
            {
                if (i >= la.Count) { diffs.Add($"EXTRA {path}[{i}]"); continue; }
                if (i >= lb.Count) { diffs.Add($"MISSING {path}[{i}]"); continue; }
                Diff(la[i], lb[i], $"{path}[{i}]", diffs);
            }
            return;
        }
        if (a is byte[] ba && b is byte[] bb)
        {
            if (ba.Length != bb.Length) diffs.Add($"BYTES {path} {ba.Length} -> {bb.Length}");
            return;
        }
        if (a == null && b == null) return;
        if (a == null || b == null) { diffs.Add($"NULL {path} {a ?? "null"} -> {b ?? "null"}"); return; }
        if (!a.Equals(b)) diffs.Add($"VALUE {path} {a} -> {b}");
    }
}
