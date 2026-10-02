namespace Nordicandia.Server.WebApi;

/// <summary>
/// Per-class active/passive powers extracted from the shipped definitions by
/// <c>tools/web-content/export_powers.py</c>. The generated half lives in
/// <c>Powers.generated.cs</c>; this half holds the lookup.
/// </summary>
public static partial class PowerCatalog
{
    /// <summary>Powers for a class, falling back to Warrior for classes without a web kit.</summary>
    public static Nordicandia.Simulation.ClassPowers ForClass(int classId)
        => ByClass.TryGetValue(classId, out var powers) ? powers : ByClass[0];
}
