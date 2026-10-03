using Nordicandia.Server.WebApi;

/// <summary>
/// E03: set-piece breakpoints and their attribute bonuses are fully extracted and resolve.
/// </summary>
static class E03SetAndSlotsTests
{
    public static void Run()
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }

        Check(SetCatalog.Count == 11, $"E03: all client item sets loaded ({SetCatalog.Count}, expected 11)");
        Check(SetCatalog.For(0) is { } set0
            && set0.Breakpoints.Select(b => b.NumItems).SequenceEqual(new[] { 2, 4, 6, 9 }),
            "E03: set 0 has 2/4/6/9-piece breakpoints");
        Check(SetCatalog.ActiveBonuses(0, 1).Count == 0 && SetCatalog.ActiveBonuses(0, 2).Count > 0,
            "E03: set bonuses only activate at their breakpoint");
        Check(SetCatalog.ActiveBonuses(0, 4).Count >= SetCatalog.ActiveBonuses(0, 2).Count,
            "E03: higher breakpoints stack on lower ones");

        // Every bonus attribute resolves to a client name (validated with the full attribute table).
        var unnamed = 0;
        var bonuses = 0;
        foreach (var setId in SetCatalog.Ids)
            if (SetCatalog.For(setId) is { } set)
                foreach (var breakpoint in set.Breakpoints)
                    foreach (var bonus in breakpoint.Bonuses)
                    {
                        bonuses++;
                        if (string.IsNullOrEmpty(bonus.AttributeName)) unnamed++;
                    }
        Check(bonuses >= 40 && unnamed == 0,
            $"E03: set bonuses carry resolved attribute names ({bonuses} bonuses, {unnamed} unnamed)");
    }
}
