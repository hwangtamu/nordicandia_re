using System.Reflection;
using System.Text.Json;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// The recovered <c>InternalInitializePowerParameters</c> inputs for the 19 powers that were
/// previously empty in <c>power_values.json</c>, from
/// <c>tools/web-content/recover_power_parameters.py</c> (see
/// <c>docs/web/DATA_RECOVERY_2026-10-02.md</c>). The table is intentionally separate from the
/// scalar <c>Powers.generated.cs</c> values because some entries are per-rank formulas, runtime
/// inputs or parent-forwarding rather than fixed constants.
///
/// Only <c>linear_rank</c> and <c>constant</c> entries are numerically evaluable here; the
/// <c>call</c>/<c>field</c>/<c>user_attribute</c>/<c>expression</c> entries record where the real
/// value comes from and are not turned into invented constants. These parameters are recovered
/// data, not yet applied to web combat.
/// </summary>
public static class PowerParameterCatalog
{
    public readonly record struct Parameter(
        string Name, string Kind, double Rank1, double PerRank, double? CapMax, double? Constant)
    {
        /// <summary>True when <see cref="Evaluate"/> has a concrete numeric result.</summary>
        public bool IsScalar => Kind is "linear_rank" or "constant";
    }

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, Parameter>>> All = new(Load);

    /// <summary>Parameters keyed by power name; every recovered power is present, including the
    /// inherited and runtime-input ones (which have no scalar parameters).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, Parameter>> Powers => All.Value;

    /// <summary>Parameters for one power, or empty when it was not recovered.</summary>
    public static IReadOnlyDictionary<string, Parameter> ForPower(string powerName)
        => All.Value.TryGetValue(powerName, out var rows) ? rows : Empty;

    private static readonly IReadOnlyDictionary<string, Parameter> Empty = new Dictionary<string, Parameter>();

    /// <summary>Client formula <c>rank1 + perRank * (rank - 1)</c> with the optional cap applied
    /// afterwards. Non-scalar parameters throw because there is no recovered numeric value.</summary>
    public static double Evaluate(Parameter parameter, int rank)
    {
        var r = Math.Max(1, rank);
        return parameter.Kind switch
        {
            "constant" => parameter.Constant ?? 0.0,
            "linear_rank" => Cap(parameter, parameter.Rank1 + parameter.PerRank * (r - 1)),
            _ => throw new InvalidOperationException(
                $"Power parameter '{parameter.Name}' ({parameter.Kind}) has no scalar value."),
        };
    }

    /// <summary>Scalar parameters for a power evaluated at <paramref name="rank"/>; runtime-only
    /// entries are skipped rather than zero-filled.</summary>
    public static IReadOnlyDictionary<string, double> EvaluatePower(string powerName, int rank)
    {
        var result = new Dictionary<string, double>();
        foreach (var (name, parameter) in ForPower(powerName))
            if (parameter.IsScalar)
                result[name] = Evaluate(parameter, rank);
        return result;
    }

    private static double Cap(Parameter parameter, double value)
        => parameter.CapMax is { } cap ? Math.Min(cap, value) : value;

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, Parameter>> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.power_parameter_recovery.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var document = JsonDocument.Parse(stream);
        var result = new Dictionary<string, IReadOnlyDictionary<string, Parameter>>();
        if (!document.RootElement.TryGetProperty("powers", out var powers)) return result;
        foreach (var power in powers.EnumerateObject())
        {
            var parameters = new Dictionary<string, Parameter>();
            foreach (var group in new[] { "fields", "attributes" })
            {
                if (!power.Value.TryGetProperty(group, out var section)) continue;
                foreach (var entry in section.EnumerateObject())
                    parameters[entry.Name] = Read(entry.Name, entry.Value);
            }
            result[power.Name] = parameters;
        }
        return result;
    }

    private static Parameter Read(string name, JsonElement element)
    {
        var kind = element.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
        double Get(string key) => element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;
        double? GetNullable(string key)
            => element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        return new Parameter(name, kind, Get("rank1"), Get("perRank"), GetNullable("capMax"), GetNullable("value"));
    }
}
