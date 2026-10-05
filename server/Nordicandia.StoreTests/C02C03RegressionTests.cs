using Nordicandia.Simulation;

static class C02C03RegressionTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    private static SkillProfile Skill(int slot, string name, string effect, double multiplier = 1,
        double radius = 3, IReadOnlyDictionary<string, double>? values = null)
        => new(slot, name, "", "", effect, multiplier, 0, radius, 0, 0, 0, 0,
            "test", values ?? new Dictionary<string, double>());

    private static CombatInstance Make(IReadOnlyList<SkillProfile> skills, int count = 1, MapLayout? layout = null)
    {
        var stats = new CombatantStats(100, 1000, 0, 1, AttackRating: 1e12,
            Damage: new DamageBundle(Physical: 100));
        var pool = new ClassPowerPool("regression", skills, Array.Empty<PassiveProfile>());
        var instance = new CombatInstance(stats, 0, 0, 0, 0, 17,
            monsterCount: count, classPowers: pool, layout: layout);
        foreach (var monster in instance.Monsters)
        {
            monster.Hp = monster.MaxHp = 10000;
            monster.Armor = monster.Defense = monster.Evasion = 0;
            monster.Speed = 0;
            monster.Offense = 0;
            monster.AttackCooldown = 1000;
            monster.Resistances = default;
        }
        return instance;
    }

    public static void Run()
    {
        PoisonStrength();
        TypedDotMitigation();
        WorldCleanup();
        ProjectileFlightAndMovingTargets();
        ProjectileWallCollision();
    }

    private static void PoisonStrength()
    {
        var buffs = new BuffManager();
        buffs.Add(new BuffInstance
        {
            DefinitionId = "poison", Source = "player", Duration = 2, Remaining = 0.9, TickDps = 10,
        });
        var strongerRateButShorter = buffs.Add(new BuffInstance
        {
            DefinitionId = "poison", Source = "player", Duration = 1, Remaining = 0.1, TickDps = 6,
        });
        Check(strongerRateButShorter && buffs.Get("poison", "player")?.TickDps == 6,
            "C02: poison override compares DPS per timeout before remaining time");
        var weakerRateButLonger = buffs.Add(new BuffInstance
        {
            DefinitionId = "poison", Source = "player", Duration = 1, Remaining = 1, TickDps = 1,
        });
        Check(!weakerRateButLonger && buffs.Get("poison", "player")?.TickDps == 6,
            "C02: longer low-rate poison does not replace stronger poison");
    }

    private static void TypedDotMitigation()
    {
        var resistances = new ResistanceBundle(Fire: 0.75, Poison: 0.1);
        var fire = CombatModel.EffectiveDamageOverTime(100, DamageOverTimeType.Fire, 0, resistances);
        var poison = CombatModel.EffectiveDamageOverTime(100, DamageOverTimeType.Poison, 0, resistances);
        Check(Math.Abs(fire - 25) < 1e-9 && Math.Abs(poison - 90) < 1e-9,
            "C02: fire and poison DoTs use their matching resistances");

        var infernal = Make(new[]
        {
            Skill(0, "InfernalBlast", "projectile", 7, 3,
                new Dictionary<string, double>
                {
                    ["Power_Duration"] = 4,
                    ["Power_Weapon_Damage_Multiplier_2"] = 3,
                }),
        });
        var target = infernal.Monsters[0];
        target.X = 6;
        target.Resistances = new ResistanceBundle(Fire: 0.75, Poison: 0);
        infernal.UseSkill(0);
        infernal.Advance(0.4); // allow the projectile to hit and apply its burn
        var burning = target.Buffs.Get("burning", "InfernalBlast");
        Check(burning is { TickDamageType: DamageOverTimeType.Fire },
            "C02: InfernalBlast applies a fire-typed burn on projectile impact");
        var hp = target.Hp;
        infernal.Advance(0.2);
        var expected = burning!.TickDps * 0.2 * 0.25;
        Check(Math.Abs(hp - target.Hp - expected) < 1e-7,
            "C02: InfernalBlast tick uses Fire resistance even when Poison resistance differs");
    }

    private static void WorldCleanup()
    {
        var instance = Make(new[]
        {
            Skill(0, "PoisonCloud", "nova", 0.9, 3,
                new Dictionary<string, double> { ["Power_Duration"] = 6, ["Power_Slow_Effect_Percent"] = 0.2 }),
            Skill(1, "SummonSkeleton", "summon"),
            Skill(2, "PowerShot", "projectile", 4.5),
        });
        instance.Monsters[0].X = 3;
        Check(instance.UseSkill(0).Cast && instance.Clouds.Count == 1, "C02: setup persistent cloud");
        Check(instance.UseSkill(1).Cast && instance.PlayerMinions.Count > 0, "C02: setup minion");
        Check(instance.UseSkill(2).Cast && instance.ActiveProjectiles.Count > 0, "C03: setup in-flight projectile");
        var renderSnapshot = instance.Snapshot();
        Check(renderSnapshot.GroundEffects?.Any(e => e.Kind == "PoisonCloud" && e.Id > 0) == true
            && renderSnapshot.Minions?.Any(m => m.Name == "Skeleton" && m.Id > 0) == true
            && renderSnapshot.Projectiles?.Any(p => p.Source == "PowerShot" && p.Id > 0) == true,
            "C05/C03: authoritative snapshots expose stable ids and state for clouds, minions, and projectiles");
        instance.PlayerBuffs.Add(new BuffInstance
        {
            DefinitionId = "temporary", Source = "test", Duration = 10, Remaining = 10,
        });
        instance.SetWorld(new[] { new MonsterProfile("NewWorldMonster") });
        Check(instance.Clouds.Count == 0 && instance.PlayerMinions.Count == 0
            && instance.ActiveProjectiles.Count == 0 && !instance.PlayerBuffs.Has("temporary", "test"),
            "C02/C03: world transition clears clouds, minions, projectiles, and nonpersistent buffs");
    }

    private static void ProjectileFlightAndMovingTargets()
    {
        var instance = Make(new[] { Skill(0, "PowerShot", "projectile", 4.5) });
        instance.Monsters[0].X = 5;
        var before = instance.Monsters[0].Hp;
        Check(instance.UseSkill(0).Cast && instance.Monsters[0].Hp == before
            && instance.ActiveProjectiles.Count == 1,
            "C03: cast creates a projectile and does not synchronously damage a distant target");
        instance.Advance(0.1);
        Check(instance.Monsters[0].Hp == before,
            "C03: target remains outside the projectile's first 2-yard flight interval");
        // The projectile is now at x=2. The actor moves into the path before impact;
        // collision must use its current position rather than the cast-time snapshot.
        instance.Monsters[0].X = 3.5;
        instance.Advance(0.1);
        Check(instance.Monsters[0].Hp < before,
            "C03: swept fixed-step collision hits an actor that moves into the path");
    }

    private static void ProjectileWallCollision()
    {
        var layout = MapLayout.Generate(21, 21, 5, 12345UL);
        var instance = Make(new[] { Skill(0, "PowerShot", "projectile", 4.5) }, layout: layout);
        var startCell = layout.Cell(instance.PlayerX, instance.PlayerZ, CombatInstance.ArenaHalf);
        var start = (instance.PlayerX, instance.PlayerZ);
        (double X, double Z)? destination = null;
        for (var z = 0; z < layout.Height && destination is null; z++)
        for (var x = 0; x < layout.Width && destination is null; x++)
        {
            if (!layout.IsFloor(x, z) || (x == startCell.X && z == startCell.Z)) continue;
            var world = layout.World(x, z, CombatInstance.ArenaHalf);
            var dx = world.X - start.Item1;
            var dz = world.Z - start.Item2;
            if (dx * dx + dz * dz > 14 * 14) continue;
            var samples = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dz * dz) / 0.1));
            for (var i = 1; i <= samples; i++)
            {
                var t = (double)i / samples;
                var cell = layout.Cell(start.Item1 + dx * t, start.Item2 + dz * t, CombatInstance.ArenaHalf);
                if (!layout.IsFloor(cell.X, cell.Z))
                {
                    destination = world;
                    break;
                }
            }
        }
        Check(destination is not null, "C03: generated map includes a projectile-blocking wall route");
        var target = instance.Monsters[0];
        target.X = destination!.Value.X;
        target.Z = destination.Value.Z;
        var hp = target.Hp;
        Check(instance.UseSkill(0).Cast, "C03: launch toward actor behind a wall");
        instance.Advance(1.0);
        Check(target.Hp == hp && instance.ActiveProjectiles.Count == 0,
            "C03: wall collision stops projectile before it reaches an actor");
    }
}
