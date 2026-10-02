using System.Reflection;
using System.Text.Json;

namespace Nordicandia.Simulation;

/// <summary>
/// Maps a skill to its damage type from the client's PowerTags (Physical/Fire/Cold/Lightning/Poison),
/// extracted to <c>tools/web-content/generated/skill_damage_types.json</c> and embedded. A skill
/// without an elemental tag is Physical.
/// </summary>
public static class SkillDamageTypes
{
    private static readonly Lazy<Dictionary<string, string>> Types = new(Load);

    public static string For(string skillName)
        => skillName != null && Types.Value.TryGetValue(skillName, out var type) ? type : "Physical";

    private static Dictionary<string, string> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.skill_damage_types.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
