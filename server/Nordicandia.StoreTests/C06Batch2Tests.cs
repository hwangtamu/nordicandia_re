using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;

/// <summary>
/// C06 batch 2: Blizzard/ElementalSeal (persistent AoE), FrozenArrow (explosive
/// projectile), Tornado (moving vortex), Slam (line attack).
/// </summary>
static class C06Batch2Tests
{
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private static CombatantStats Stats(double physical = 100) => new(100, 1000, 0, 1,
        AttackRating: 1e12, CritChance: 0, Damage: new DamageBundle(Physical: physical));

    private static CombatInstance Create(int classId, string skill, ulong seed = 789, int count = 3)
    {
        var pool = PowerCatalog.BuildPool(classId, new[] { skill }, System.Array.Empty<string>());
        var inst = new CombatInstance(Stats(), 0, 0, 0, 0, seed, monsterCount: count, classPowers: pool);
        foreach (var m in inst.Monsters)
        {
            m.Hp = m.MaxHp = 100000; m.Armor = m.Defense = 0;
            m.Speed = 0; m.Offense = 0; m.AttackCooldown = 1000; m.Resistances = default;
        }
        return inst;
    }

    public static void Run()
    {
        Blizzard();
        ElementalSeal();
        FrozenArrow();
        Tornado();
        Slam();
    }

    private static void Blizzard()
    {
        // Mage: 4s persistent AoE, 4y radius, 1.0x per tick (cold).
        var inst = Create(5, "Blizzard", count: 2);
        inst.Monsters[0].X = 2; inst.Monsters[0].Z = 0;
        inst.Monsters[1].X = 20; inst.Monsters[1].Z = 0;
        var cast = inst.UseSkill(0);
        Check(cast.Cast && inst.Clouds.Count == 1, "blizzard: cast spawns a persistent AoE");
        var cloud = inst.Clouds[0];
        Check(cloud.Kind == "Blizzard" && Math.Abs(cloud.Radius - 4.0) < 1e-9
            && Math.Abs(cloud.Remaining - 4.0) < 1e-9,
            "blizzard: radius 4.0, duration 4.0 from recovered attributes");
        var hp0 = inst.Monsters[0].Hp;
        inst.Advance(1.1);
        var dealt = hp0 - inst.Monsters[0].Hp;
        Check(dealt > 0, $"blizzard: tick deals damage ({dealt:F0})");
        Check(!inst.Monsters[0].Buffs.Has("slow", "Blizzard"),
            "blizzard: no slow (unlike PoisonCloud)");
        Check(inst.Monsters[1].Hp == inst.Monsters[1].MaxHp,
            "blizzard: distant monster unaffected");
        inst.Advance(4.0);
        Check(inst.Clouds.Count == 0, "blizzard: expires after 4s");
    }

    private static void ElementalSeal()
    {
        // Mage: 10s persistent seal, 2.5y radius, 0.25x per tick.
        var inst = Create(5, "ElementalSeal", count: 1);
        inst.Monsters[0].X = 4; // outside melee range (avoids auto-attack)
        var cast = inst.UseSkill(0);
        Check(cast.Cast && inst.Clouds.Count == 1, "seal: cast spawns a persistent seal");
        var cloud = inst.Clouds[0];
        Check(cloud.Kind == "ElementalSeal" && Math.Abs(cloud.Radius - 2.5) < 1e-9
            && Math.Abs(cloud.Remaining - 10.0) < 1e-9,
            "seal: radius 2.5, duration 10.0 from recovered attributes");
        var hp0 = inst.Monsters[0].Hp;
        inst.Advance(1.1);
        var dealt = hp0 - inst.Monsters[0].Hp;
        // 0.25x * 100 * 2 (Mage passive?) — just verify positive and smaller than Blizzard's 1.0x.
        Check(dealt > 20 && dealt < 50,
            $"seal: tick deals 0.25x damage ({dealt:F0})");
        inst.Advance(5.0);
        inst.Advance(5.5);
        Check(inst.Clouds.Count == 0, "seal: expires after 10s");
    }

    private static void FrozenArrow()
    {
        // Hunter: 1.4x projectile, explodes in 4y on hit.
        var inst = Create(4, "FrozenArrow", count: 3);
        inst.Monsters[0].X = 3; inst.Monsters[0].Z = 0;
        inst.Monsters[1].X = 5; inst.Monsters[1].Z = 0; // within 4y of primary
        inst.Monsters[2].X = 15; inst.Monsters[2].Z = 0; // outside
        var cast = inst.UseSkill(0);
        Check(cast.Cast && inst.Monsters[0].Hp == inst.Monsters[0].MaxHp,
            "frozen: primary remains untouched until projectile impact");
        inst.Advance(0.5);
        var events = inst.DrainEvents();
        Check(events.Any(e => e.Type == "projectile" && e.Detail.StartsWith("explode")),
            "frozen: impact explosion event emitted");
        // Verify the AoE damaged the nearby monster by checking a fresh instance.
        var inst2 = Create(4, "FrozenArrow", count: 3);
        inst2.Monsters[0].X = 3;
        inst2.Monsters[1].X = 5; inst2.Monsters[1].Z = 0;
        inst2.Monsters[2].X = 15;
        var hp1b = inst2.Monsters[1].Hp;
        var hp2b = inst2.Monsters[2].Hp;
        inst2.UseSkill(0);
        inst2.Advance(0.5);
        Check(hp1b - inst2.Monsters[1].Hp > 0, "frozen: impact explosion damages monsters in 4y");
        Check(inst2.Monsters[2].Hp == hp2b, "frozen: monster outside 4y unaffected");
    }

    private static void Tornado()
    {
        // Mage: 2y radius vortex, 100% pierce, 2.3x per hit.
        var inst = Create(5, "Tornado", count: 3);
        inst.Monsters[0].X = 3; inst.Monsters[0].Z = 0;
        inst.Monsters[1].X = 6; inst.Monsters[1].Z = 0.5; // in the 2y corridor
        inst.Monsters[2].X = 6; inst.Monsters[2].Z = 10; // outside corridor
        var cast = inst.UseSkill(0);
        Check(cast.Cast && inst.ActiveProjectiles.Count == 1,
            "tornado: persistent vortex projectile starts in flight");
        inst.Advance(0.5);
        // 100% pierce: should hit monster[1] in the corridor.
        var inst2 = Create(5, "Tornado", count: 3);
        inst2.Monsters[0].X = 3;
        inst2.Monsters[1].X = 6; inst2.Monsters[1].Z = 0.5;
        inst2.Monsters[2].X = 6; inst2.Monsters[2].Z = 10;
        var hp1 = inst2.Monsters[1].Hp;
        var hp2 = inst2.Monsters[2].Hp;
        inst2.UseSkill(0);
        inst2.Advance(0.5);
        Check(hp1 - inst2.Monsters[1].Hp > 0, "tornado: 100% pierce hits the second monster in corridor");
        Check(inst2.Monsters[2].Hp == hp2, "tornado: monster outside corridor unaffected");
    }

    private static void Slam()
    {
        // Warrior: 5.5x in a 15y line (1y half-width), not a circle.
        var inst = Create(0, "Slam", count: 3);
        inst.Monsters[0].X = 5; inst.Monsters[0].Z = 0; // on the line
        inst.Monsters[1].X = 10; inst.Monsters[1].Z = 0.5; // on the line
        inst.Monsters[2].X = 5; inst.Monsters[2].Z = 5; // off the line
        var cast = inst.UseSkill(0);
        Check(cast.Cast && cast.Damage > 0, "slam: line attack hits");
        var hp0 = inst.Monsters[0].Hp;
        var hp1 = inst.Monsters[1].Hp;
        var hp2 = inst.Monsters[2].Hp;
        // Re-run to measure (UseSkill already consumed).
        var inst2 = Create(0, "Slam", count: 3);
        inst2.Monsters[0].X = 5;
        inst2.Monsters[1].X = 10; inst2.Monsters[1].Z = 0.5;
        inst2.Monsters[2].X = 5; inst2.Monsters[2].Z = 5;
        var h0 = inst2.Monsters[0].Hp;
        var h1 = inst2.Monsters[1].Hp;
        var h2 = inst2.Monsters[2].Hp;
        inst2.UseSkill(0);
        Check(h0 - inst2.Monsters[0].Hp > 0 && h1 - inst2.Monsters[1].Hp > 0,
            "slam: monsters on the 15y line take damage");
        Check(inst2.Monsters[2].Hp == h2,
            "slam: monster off the line (5y perpendicular) unaffected — not a circle");
    }
}
