using Nordicandia.Server.WebApi;

/// <summary>
/// W04/W05: the embedded world roster exposes tiers, bosses and spawn weights.
/// </summary>
static class W04WorldCatalogTests
{
    public static void Run()
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }

        Check(WorldCatalog.Count == 36, $"W04: full client world roster loaded ({WorldCatalog.Count}, expected 36)");
        Check(WorldCatalog.ForTier(1) is { } world1
            && world1.Name == "Grasshill" && world1.SpawnWeights.ContainsKey("Beast"),
            "W04: tier 1 is Grasshill with a Beast spawn weight");
        Check(WorldCatalog.ForTier(1)?.BossName == "Boss_WolfKing",
            "W04: tier 1 boss is Boss_WolfKing");
        Check(WorldCatalog.ById(0)?.Name == "Grasshill",
            "W05: worlds are addressable by their client integer id");
        Check(WorldCatalog.Entries.Count(w => w.Tier is not null) == 35,
            "W05: 35 of the 36 worlds are tiered (the other is a town)");
    }
}
