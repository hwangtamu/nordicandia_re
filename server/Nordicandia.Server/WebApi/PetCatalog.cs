using System.Reflection;
using System.Text.Json;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// 1.9.3 pet rows recovered from MonsterDefinition affixes. PetRow.Present reads
/// OpalCost (attribute 421) and SilverCost (968) with level=1, rarity=1, multiplier=1.
/// </summary>
public static class PetCatalog
{
    public sealed record Pet(int IntegerId, string Name, string Kind, string Image,
        string? UnlockCurrency, int? UnlockCost, IReadOnlyList<PetEffect> Effects);
    public sealed record PetEffect(string Affix, int AttributeId, double Value);

    private static readonly Lazy<(IReadOnlyList<Pet> Entries, IReadOnlyDictionary<int, Pet> ById)> Data = new(Load);
    public static IReadOnlyList<Pet> Entries => Data.Value.Entries;
    public static IEnumerable<Pet> CompanionPets => Entries.Where(p => p.Kind == "Pet");
    public static IEnumerable<Pet> CombatPets => Entries.Where(p => p.Kind == "CombatPet");
    public static Pet? ByIntegerId(int id) => Data.Value.ById.GetValueOrDefault(id);

    private static (IReadOnlyList<Pet>, IReadOnlyDictionary<int, Pet>) Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.pet_catalog.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var raw = JsonSerializer.Deserialize<RawCatalog>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var entries = raw.Items.Select(p => new Pet(p.IntegerId, p.Name ?? "", p.Kind ?? "", p.Image ?? "",
            p.UnlockCurrency, p.UnlockCost, p.Effects?.Select(e => new PetEffect(e.Affix ?? "", e.AttributeId, e.Value)).ToArray()
                ?? Array.Empty<PetEffect>())).ToArray();
        return (entries, entries.ToDictionary(p => p.IntegerId));
    }

    private sealed record RawCatalog(List<RawPet> Items);
    private sealed record RawPet(int IntegerId, string? Name, string? Kind, string? Image,
        string? UnlockCurrency, int? UnlockCost, List<RawPetEffect>? Effects);
    private sealed record RawPetEffect(string? Affix, int AttributeId, double Value);
}
