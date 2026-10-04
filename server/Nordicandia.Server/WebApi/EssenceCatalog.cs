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

    /// <summary>The essence for an affix definition, or null when the affix has no essence.</summary>
    public static Essence? ForAffixDefinition(int affixDefinitionIntegerId)
    {
        var affix = AffixCatalog.Entries.FirstOrDefault(e => e.IntegerId == affixDefinitionIntegerId);
        if (affix.Name is null) return null;
        return ByAffix.Value.TryGetValue(affix.Name, out var essence) ? essence : null;
    }

    /// <summary>A guaranteed essence item (inventory, no implicit rolls).</summary>
    public static SerializedItem CreateItem(Essence essence, int level)
    {
        return new SerializedItem
        {
            Id = Guid.NewGuid(),
            Name = essence.Name,
            Slot = ItemSlotTypes.Inventory,
            DefinitionIntegerId = essence.IntegerId,
            BaseRarity = Rarity.C,
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
            Affixes = new List<SerializedAffix>(),
        };
    }

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
