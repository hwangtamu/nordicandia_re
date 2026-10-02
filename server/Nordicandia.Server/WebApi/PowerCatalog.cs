using Nordicandia.Simulation;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// Per-class active/passive powers extracted from the shipped definitions by
/// <c>tools/web-content/export_powers.py</c>. The generated half lives in
/// <c>Powers.generated.cs</c>; this half holds the lookup.
/// </summary>
public static partial class PowerCatalog
{
    /// <summary>Powers for a class, falling back to Warrior for classes without a web kit.</summary>
    public static ClassPowers ForClass(int classId)
        => ByClass.TryGetValue(classId, out var powers) ? powers : ByClass[0];

    /// <summary>Masteries for a power name, or empty when it has none.</summary>
    public static IReadOnlyList<MasteryProfile> MasteriesFor(string skillName)
        => MasteriesByPower.TryGetValue(skillName, out var rows) ? rows : Array.Empty<MasteryProfile>();
}
