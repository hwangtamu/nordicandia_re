using Nordicandia.Server.WebApi;

/// <summary>
/// E04: the essence catalog (Items.json EssenceAffixId) and the disassemble filter.
/// </summary>
static class E04EssenceTests
{
    public static void Run()
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }

        Check(EssenceCatalog.Count == 60, $"E04: the essence catalog loads ({EssenceCatalog.Count}, expected 60)");

        // The item-domain affix ArmorPercent (IntegerId 30) maps to its essence.
        Check(EssenceCatalog.ForAffixDefinition(30) is { Name: "ArmorPercent" },
            "E04: the ArmorPercent affix maps to its essence item");
        // BaseWeaponFireDamage is a prefix that also has an essence.
        var fire = AffixCatalog.Entries.First(e => e.Name == "BaseWeaponFireDamage");
        Check(EssenceCatalog.ForAffixDefinition(fire.IntegerId) is { Name: "BaseWeaponFireDamage" },
            "E04: a weapon damage prefix maps to its essence");
        Check(EssenceCatalog.ForAffixDefinition(-1) is null,
            "E04: an unknown affix has no essence");
    }
}
