using Game;
using Nordicandia.Server.State;
using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;
using SharedNet.Api;
using SharedNet.Constants.Game;

/// <summary>
/// C05: end-to-end verification for the first 4 representative skills:
/// Shatter (Warrior, direct), PowerShot (Hunter, projectile),
/// PoisonCloud (Mage, sustained AoE), SummonSkeleton (Necromancer, summon).
/// Each covers: original spec -> server resolution -> frontend events.
/// </summary>
static class C05SkillTests
{
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private static CombatantStats Stats => new(100, 1000, 0, 1,
        AttackRating: 1e12, CritChance: 0, Damage: new DamageBundle(Physical: 100));

    private static CombatInstance Create(int classId, string skill, ulong seed = 123, int count = 3)
    {
        var pool = Nordicandia.Server.WebApi.PowerCatalog.BuildPool(classId, new[] { skill }, Array.Empty<string>());
        var inst = new CombatInstance(Stats, 0, 0, 0, 0, seed, monsterCount: count, classPowers: pool);
        foreach (var m in inst.Monsters)
        {
            m.Hp = m.MaxHp = 10000; m.Armor = m.Defense = 0;
            m.Speed = 0; m.Offense = 0; m.AttackCooldown = 1000; m.Resistances = default;
        }
        return inst;
    }

    public static void Run()
    {
        Shatter();
        PowerShot();
        PoisonCloud();
        SummonSkeleton();
        Growth();
        EventsThroughWebState();
        MeditationCooldown();
        MeditationCooldownReset();
        PersistedPassiveRank();
        Relogin();
    }

    private static void Shatter()
    {
        // Warrior direct attack: 9.0x weapon damage, single melee target, 35 mana, 5s cooldown.
        var inst = Create(0, "Shatter", count: 1);
        inst.Monsters[0].X = 2; inst.Monsters[0].Z = 0;
        var manaBefore = inst.PlayerMana;
        var cast = inst.UseSkill(0);
        Check(cast.Cast && cast.Damage > 0, "shatter: cast deals damage to the target");
        Check(inst.PlayerMana == manaBefore - 35, "shatter: 35 mana deducted");
        Check(!inst.UseSkill(0).Cast, "shatter: 5s cooldown blocks immediate recast");
        // 9.0x multiplier on the 100-physical weapon, doubled by the Warrior kit
        // passive (+100% offense), ±8% variance.
        Check(cast.Damage > 1600 && cast.Damage < 2000,
            $"shatter: 9.0x weapon damage (got {cast.Damage:F1})");
    }

    private static void PowerShot()
    {
        // Hunter projectile: 4.5x, 95% pierce chance, flies the real C03 path.
        var inst = Create(4, "PowerShot", count: 3);
        for (var i = 0; i < 3; i++) { inst.Monsters[i].X = 2 + i * 2; inst.Monsters[i].Z = 0; }
        var cast = inst.UseSkill(0);
        Check(cast.Cast && cast.Damage == 0 && inst.ActiveProjectiles.Count == 1
            && inst.Monsters.All(m => m.Hp == m.MaxHp),
            "powershot: cast launches a persistent projectile without instant damage");
        inst.Advance(1.0);
        var events = inst.DrainEvents();
        Check(events.Any(e => e.Type == "projectile" && e.Detail.StartsWith("spawn")),
            "powershot: projectile spawn event emitted for rendering");
        Check(events.Any(e => e.Type == "projectile" && e.Detail.StartsWith("hit")),
            "powershot: projectile hit event emitted for rendering");
    }

    private static void PoisonCloud()
    {
        // Mage sustained AoE: 6s cloud, radius 3.0, 0.9x poison ticks, 20% slow.
        var inst = Create(5, "PoisonCloud", count: 2);
        inst.Monsters[0].X = 2; inst.Monsters[0].Z = 0;
        inst.Monsters[1].X = 20; inst.Monsters[1].Z = 0; // outside the cloud
        var cast = inst.UseSkill(0);
        Check(cast.Cast && inst.Clouds.Count == 1, "poisoncloud: cast spawns a persistent cloud");
        var cloud = inst.Clouds[0];
        Check(Math.Abs(cloud.Radius - 3.0) < 1e-9 && Math.Abs(cloud.Remaining - 6.0) < 1e-9,
            "poisoncloud: radius 3.0, duration 6.0 from recovered attributes");
        var hp0 = inst.Monsters[0].Hp;
        inst.Advance(1.1);
        var tickDamage = hp0 - inst.Monsters[0].Hp;
        Check(tickDamage > 0, $"poisoncloud: cloud tick deals poison damage (got {tickDamage:F1})");
        Check(inst.Monsters[0].Buffs.Has("slow", "PoisonCloud"),
            "poisoncloud: 20% slow debuff applied to monsters in the cloud");
        Check(inst.Monsters[1].Hp == inst.Monsters[1].MaxHp,
            "poisoncloud: monsters outside the radius are unaffected");
        inst.Advance(6.0);
        Check(inst.Clouds.Count == 0, "poisoncloud: cloud expires after 6s");
        var events = inst.DrainEvents();
        Check(events.Any(e => e.Type == "cloud" && e.Detail.StartsWith("spawn"))
            && events.Any(e => e.Type == "cloud" && e.Detail.StartsWith("expire")),
            "poisoncloud: cloud spawn/expire events emitted for rendering");
    }

    private static void SummonSkeleton()
    {
        // Necromancer summon: 3 skeleton minions that seek and attack monsters.
        var inst = Create(6, "SummonSkeleton", count: 2);
        inst.Monsters[0].X = 5; inst.Monsters[0].Z = 0;
        inst.Monsters[1].X = 8; inst.Monsters[1].Z = 0;
        var cast = inst.UseSkill(0);
        Check(cast.Cast && inst.PlayerMinions.Count == 3,
            $"summon: 3 skeleton minions spawned (got {inst.PlayerMinions.Count})");
        var hp0 = inst.Monsters[0].Hp;
        var hp1 = inst.Monsters[1].Hp;
        inst.Monsters[0].Hp = 1; // dies to the first minion hit; minions retarget
        inst.Advance(10.0);
        var dealt1 = hp1 - inst.Monsters[1].Hp;
        Check(dealt1 > 0,
            $"summon: minions seek the nearest monster and retarget after a kill ({dealt1:F0})");
        var events = inst.DrainEvents();
        Check(events.Any(e => e.Type == "summon") && events.Any(e => e.Type == "minion"),
            "summon: summon and minion-attack events emitted for rendering");
        // Minion inheritance: damage scales with the weapon via the recovered attribute.
        Check(inst.PlayerMinions.All(m => m.Damage > 100),
            "summon: minion damage inherits weapon damage (+15% bonus)");
    }

    private static void Growth()
    {
        // Growth chain: mastery rank -> EffectivePowers -> skill multiplier -> damage.
        // Same seed on both instances: identical variance rolls, so the ratio is exact.
        var pool = Nordicandia.Server.WebApi.PowerCatalog.BuildPool(0, new[] { "Shatter" }, Array.Empty<string>());
        var skill = pool.Active[0];
        var boosted = new ClassPowerPool(pool.ClassName,
            new[] { skill with { Multiplier = skill.Multiplier + 0.05 } },
            pool.Passive);
        var a = new CombatInstance(Stats, 0, 0, 0, 0, 123, monsterCount: 1, classPowers: pool);
        var b = new CombatInstance(Stats, 0, 0, 0, 0, 123, monsterCount: 1, classPowers: boosted);
        a.Monsters[0].X = b.Monsters[0].X = 2;
        var da = a.UseSkill(0).Damage;
        var db = b.UseSkill(0).Damage;
        Check(Math.Abs(db / da - 9.05 / 9.0) < 1e-4,
            $"growth: +0.05 mastery multiplier flows to immediate strike damage ({da:F1} -> {db:F1})");
    }

    private static void MeditationCooldown()
    {
        var pool = Nordicandia.Server.WebApi.PowerCatalog.BuildPool(0,
            new List<string> { "Shatter" }, new List<string> { "Meditation" });
        var meditation = pool.Passive.Single(p => p.Name == "Meditation");
        Check(meditation.AttributeBonuses?.GetValueOrDefault(96) == -0.1,
            "Meditation: rank-1 native cooldown modifier is mapped to attribute 96");
        var instance = new CombatInstance(Stats, 0, 0, 0, 0, 123, monsterCount: 1, classPowers: pool);
        instance.Monsters[0].X = 2;
        var before = instance.Snapshot().Skills.Single(s => s.Name == "Shatter").MaxCooldown;
        Check(Math.Abs(before - pool.Active[0].Cooldown * 0.9) < 1e-9,
            "Meditation: active skill maximum cooldown is reduced by 10 percent");
        Check(instance.UseSkill(0).Cast, "Meditation: Shatter still casts with the passive equipped");
        var after = instance.Snapshot().Skills.Single(s => s.Name == "Shatter").Cooldown;
        Check(Math.Abs(after - before) < 1e-9,
            "Meditation: the live cooldown timer uses the reduced duration");
    }

    private static void PersistedPassiveRank()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nord-c05-passive-rank-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new GameStore(directory);
            var owner = store.GetOrCreateUser("device:c05-passive-rank").UserId;
            var hash = Nordicandia.Server.State.PowerCatalog.HashNameSafe("Meditation");
            var data = Defaults.Create<SerializedCharacterData.SerializedData>();
            data.Powers = new SerializedCharacterData.SerializedPowers
            {
                Powers = new List<SerializedCharacterData.SerializedPower>
                {
                    new() { PowerHash = hash, PowerHashSafe = hash, Power_Rank = 5 },
                },
            };
            var characterId = store.CreateCharacter(owner, new CreateCharacterRequest
            {
                DisplayName = "passive-rank",
                CharacterGameMode = GameMode.Normal,
                CharacterClass = CharacterClass.Warrior,
                Data = new SerializedCharacterData { Data = data },
            }).CharacterId;
            var ranks = store.GetPowerRanks(owner, characterId);
            Check(ranks.GetValueOrDefault("Meditation") == 5,
                "passive rank: saved Power_Rank resolves from PowerHashSafe");
            var registry = new CombatRegistry(store, new FrozenClock());
            Check(registry.SetLoadout(owner, characterId,
                new List<string> { "Shatter" }, new List<string> { "Meditation" }).Applied,
                "passive rank: loadout accepts Meditation");
            var first = registry.GetOrCreate(owner, characterId).Snapshot().Skills.Single(s => s.Name == "Shatter");
            Check(Math.Abs(first.MaxCooldown - 4.3) < 1e-9,
                $"passive rank: rank 5 formula applies after loadout ({first.MaxCooldown:F2}s)");
            registry.Reset();
            var reloaded = new CombatRegistry(store, new FrozenClock()).GetOrCreate(owner, characterId)
                .Snapshot().Skills.Single(s => s.Name == "Shatter");
            Check(Math.Abs(reloaded.MaxCooldown - first.MaxCooldown) < 1e-9,
                "passive rank: ranked cooldown survives combat-registry recreation");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void MeditationCooldownReset()
    {
        var basePool = Nordicandia.Server.WebApi.PowerCatalog.BuildPool(0,
            new List<string> { "Shatter" }, new List<string>());
        var resetPassive = new PassiveProfile("ResetChance", "", "", "", 0, 0,
            "test", new Dictionary<int, double> { [176] = 1.0 });
        var pool = new ClassPowerPool(basePool.ClassName, basePool.Active, new[] { resetPassive });
        var instance = new CombatInstance(Stats, 0, 0, 0, 0, 123, monsterCount: 1, classPowers: pool);
        instance.Monsters[0].X = 2;
        Check(instance.UseSkill(0).Cast, "Meditation hook: active skill casts with reset chance");
        Check(instance.Snapshot().Skills.Single(s => s.Name == "Shatter").Cooldown == 0
            && instance.DrainEvents().Any(e => e.Type == "cooldown-reset" && e.Detail == "Shatter"),
            "Meditation hook: successful native chance removes that skill's cooldown and emits a render event");
    }

    private static void EventsThroughWebState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nordicandia-c05-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new GameStore(directory);
            var owner = store.GetOrCreateUser("device:c05-events").UserId;
            var characterId = store.CreateCharacter(owner, new CreateCharacterRequest
            {
                DisplayName = "events",
                CharacterGameMode = GameMode.Normal,
                CharacterClass = CharacterClass.Necromancer,
                Data = new SerializedCharacterData { Data = new SerializedCharacterData.SerializedData() },
            }).CharacterId;
            var registry = new CombatRegistry(store, new FrozenClock());
            Check(registry.SetLoadout(owner, characterId, new List<string> { "SummonSkeleton" }, new List<string>()).Applied,
                "web events: install SummonSkeleton for integration test");
            var instance = registry.GetOrCreate(owner, characterId);
            Check(instance.UseSkill(0).Cast, "web events: skill emits a simulation combat event");
            var state = registry.Advance(owner, characterId);
            Check(state.Combat.Events?.Any(e => e.Type == "summon" && e.Detail == "SummonSkeleton") == true,
                "web events: authoritative state transports drained summon events");
            var next = registry.Advance(owner, characterId);
            Check(next.Combat.Events?.Count == 0,
                "web events: event batches are consumed once and do not repeat on later polls");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void Relogin()
    {
        // Relogin: mastery ranks live in the store; a fresh registry (new login)
        // reapplies them via EffectivePowers. Detected via the "mastery-modified" mark.
        var directory = Path.Combine(Path.GetTempPath(), "nord-c05-" + Guid.NewGuid());
        try
        {
            Guid owner, charId;
            using (var store = new GameStore(directory))
            {
                owner = store.GetOrCreateUser("device:c05-relogin").UserId;
                charId = store.CreateCharacter(owner, new CreateCharacterRequest
                {
                    DisplayName = "c05", CharacterGameMode = GameMode.Normal,
                    CharacterClass = CharacterClass.Warrior,
                    Data = new SerializedCharacterData { Data = new SerializedCharacterData.SerializedData() },
                }).CharacterId;
                // MasteryShatterImprovedShatter (id 2): +0.05 weapon-damage multiplier / rank.
                store.SetMasteryRank(owner, charId, 2, 1);
            }
            string conf1, conf2;
            var store1 = new GameStore(directory);
            try
            {
                var registry1 = new CombatRegistry(store1);
                var inst1 = registry1.GetOrCreate(owner, charId);
                // Equip Shatter (a fresh character only carries the 3-skill starter kit).
                registry1.SetLoadout(owner, charId,
                    new List<string> { "Shatter", "Slam", "Might", "Pounce", "WarStomp", "Sprint" },
                    new List<string>());
                conf1 = inst1.Snapshot().Skills.First(s => s.Name == "Shatter").Confidence;
            }
            finally { store1.Dispose(); }
            // "Relogin": a brand-new registry over the same store.
            var store2 = new GameStore(directory);
            try
            {
                var registry2 = new CombatRegistry(store2);
                conf2 = registry2.GetOrCreate(owner, charId).Snapshot().Skills
                    .First(s => s.Name == "Shatter").Confidence;
            }
            finally { store2.Dispose(); }
            Check(conf1 == "mastery-modified" && conf2 == "mastery-modified",
                "relogin: mastery rank persists in the store and applies after relogin");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class FrozenClock : TimeProvider
    {
        private readonly DateTimeOffset now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
    }
}
