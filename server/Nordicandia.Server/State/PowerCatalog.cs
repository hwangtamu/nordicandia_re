using System.Text.Json;

namespace Nordicandia.Server.State;

/// <summary>
/// Loads the decrypted <c>Powers.json</c> so the server can translate the <c>PowerId</c>
/// (a definition Guid) the client sends in skill-assignment calls into the integer
/// <c>PowerHashSafe</c> stored on the character. The hash is the client's
/// <c>Common.Helpers.StringHashHelper.HashNameSafe(powerName)</c>.
/// </summary>
public static class PowerCatalog
{
    public readonly record struct PowerInfo(string Name, int HashSafe);

    private static readonly Lazy<Dictionary<Guid, PowerInfo>> Map = new(Load, isThreadSafe: true);

    public static bool TryGet(Guid powerId, out PowerInfo info) => Map.Value.TryGetValue(powerId, out info);

    public static int HashNameSafe(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        name = name.ToLowerInvariant();
        uint h1 = 0x15051505, h2 = 0x15051505;
        var i = 0;
        while (i < name.Length)
        {
            h1 = (h1 * 0x21) ^ name[i];
            if (i == name.Length - 1) break;
            h2 = (h2 * 0x21) ^ name[i + 1];
            i += 2;
        }
        return unchecked((int)(h1 + h2 * 0x5d588b65));
    }

    private static Dictionary<Guid, PowerInfo> Load()
    {
        var result = new Dictionary<Guid, PowerInfo>();
        var dir = Environment.GetEnvironmentVariable("NORD_GAMEDATA_DIR") ?? "gamedata_decrypted";
        var path = Path.Combine(dir, "Powers.json");
        if (!File.Exists(path))
        {
            foreach (var candidate in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "gamedata_decrypted", "Powers.json"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "gamedata_decrypted", "Powers.json"),
            })
                if (File.Exists(candidate)) { path = candidate; break; }
        }
        if (!File.Exists(path)) return result;

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("Id", out var idProp) || !Guid.TryParse(idProp.GetString(), out var id)) continue;
            if (!entry.TryGetProperty("SerializedData", out var data)) continue;
            var name = data.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;
            result[id] = new PowerInfo(name, HashNameSafe(name));
        }
        return result;
    }
}
