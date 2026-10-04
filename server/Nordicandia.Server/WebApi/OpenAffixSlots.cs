using Game;
using SharedNet.Constants.Game;

namespace Nordicandia.Server.WebApi;

/// <summary>Item.FillOpenAffixSlotWith (0x02C8A6DC) and its predicate (0x02C8CDD8):
/// replace the first open slot with the same GenerationType; never invent capacity from rarity.
/// Definition identities come from Affixes.json. Generation of new open slots is a separate path.</summary>
public static class OpenAffixSlots
{
    public const int PrefixDefinition = 1076, SuffixDefinition = 1077;
    public static int? TypeOf(SerializedAffix affix)
    {
        if (affix.DefinitionIntegerId == PrefixDefinition) return 0;
        if (affix.DefinitionIntegerId == SuffixDefinition) return 1;
        var definition = AffixCatalog.Entries.FirstOrDefault(a => a.IntegerId == affix.DefinitionIntegerId);
        if (definition.Name != null) return definition.GenerationType;
        // Existing web saves record the generation type explicitly.
        if (affix.Attributes?.Values?.TryGetValue(AttributeOrigin.Item, out var map) == true
            && map != null && map.TryGetValue(LootTable.AttrAffixType, out var type)) return (int)type.ValueD;
        return null;
    }
    public static bool IsOpen(SerializedAffix affix) => affix.DefinitionIntegerId is PrefixDefinition or SuffixDefinition
        || affix.Attributes?.Values?.Values.Any(m => m != null && (m.ContainsKey(389) || m.ContainsKey(390))) == true;

    public static bool Merge(List<SerializedAffix> target, SerializedAffix incoming)
    {
        if (IsOpen(incoming) || TypeOf(incoming) is not (0 or 1)) return false;
        var old = target.FindIndex(a => a.DefinitionIntegerId == incoming.DefinitionIntegerId);
        if (old >= 0)
        {
            if (incoming.Rarity <= target[old].Rarity) return false;
            target[old] = incoming;
            return true;
        }
        var slot = target.FindIndex(a => IsOpen(a) && TypeOf(a) == TypeOf(incoming));
        if (slot < 0) return false;
        target[slot] = incoming;
        return true;
    }

    public static SerializedAffix Create(bool prefix) => new()
    {
        DefinitionIntegerId = prefix ? PrefixDefinition : SuffixDefinition,
        Rarity = Rarity.E,
        Attributes = new SerializedAttributes { Values = new()
        {
            [AttributeOrigin.Item] = new() { [prefix ? 389 : 390] = new() { Value = 1, ValueD = 1 } }
        }, MultiplicativeValues = new() },
    };
}
