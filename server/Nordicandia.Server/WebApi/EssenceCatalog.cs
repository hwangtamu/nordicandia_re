using System.Reflection;
using System.Text.Json;
using Game;
using SharedNet.Constants.Game;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// E04: the client's essence items (Items.json <c>EssenceAffixId</c>), extracted by
/// tools/web-content/recover_essence_disassemble.py. An essence item carries the affix it
/// represents, so <c>GetDisassemblableAffixes</c> turns a disassemblable affix into its essence.
/// </summary>
public static class EssenceCatalog
{
    public readonly record struct Essence(int IntegerId, string Name, string AffixName);

    private static readonly Lazy<Dictionary<string, Essence>> ByAffix = new(Load);

    public static int Count => ByAffix.Value.Count;
    public static bool IsEssence(int definitionId) => ByAffix.Value.Values.Any(e => e.IntegerId == definitionId);

    /// <summary>The essence for an affix definition, or null when the affix has no essence.</summary>
    public static Essence? ForAffixDefinition(int affixDefinitionIntegerId)
    {
        var affix = AffixCatalog.Entries.FirstOrDefault(e => e.IntegerId == affixDefinitionIntegerId);
        if (affix.Name is null) return null;
        return ByAffix.Value.TryGetValue(affix.Name, out var essence) ? essence : null;
    }

    /// <summary>An essence item (inventory, no implicit rolls). ClientVerified
    /// (TabBlacksmithDisassemble.ExtractAffixEssence @0x029ABFE8): the essence carries a new affix
    /// built from the source affix definition at <paramref name="targetRarity"/>.</summary>
    public static SerializedItem CreateItem(Essence essence, int targetRarity, int affixDefinitionId, int level)
    {
        var rarity = (Rarity)Math.Clamp(targetRarity, 0, 11);
        var affixes = new List<SerializedAffix>();
        if (affixDefinitionId != 0)
            affixes.Add(new SerializedAffix { DefinitionIntegerId = affixDefinitionId, Rarity = rarity });
        return new SerializedItem
        {
            Id = Guid.NewGuid(),
            Name = essence.Name,
            Slot = ItemSlotTypes.Inventory,
            DefinitionIntegerId = essence.IntegerId,
            BaseRarity = rarity,
            Location = new SerializedItemInventoryLocation { Page = 1, Row = 0, Column = 0 },
            Attributes = new SerializedAttributes
            {
                Values = new Dictionary<AttributeOrigin, Dictionary<int, GameAttributeValue>>
                {
                    [AttributeOrigin.Item] = new()
                    {
                        [LootTable.AttrRequiredLevel] = new GameAttributeValue { Value = Math.Max(1, level), ValueD = Math.Max(1, level) },
                    },
                },
                MultiplicativeValues = new(),
            },
            Affixes = affixes,
        };
    }

    /// <summary>A bare essence item (no affix), e.g. from a lootbox.</summary>
    public static SerializedItem CreateItem(Essence essence, int level)
        => CreateItem(essence, (int)Rarity.C, 0, level);

    private static Dictionary<string, Essence> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.essence_affix_catalog.json", StringComparison.Ordinal)))!;
        var raw = JsonSerializer.Deserialize<Dictionary<string, RawEssence>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var result = new Dictionary<string, Essence>();
        foreach (var (key, value) in raw)
        {
            if (string.IsNullOrEmpty(value.Affix)) continue;
            result[value.Affix] = new Essence(int.Parse(key), value.Name ?? value.Affix, value.Affix);
        }
        return result;
    }

    private sealed record RawEssence(string? Name, string? Affix, string? EssenceAffixId);
}
