using Game;
using Nordicandia.Server.State;
using SharedNet.Api;
using SharedNet.Constants.Game;
using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;

static class LuckAndOnHitRecoveryTests
{
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    public static void RarityWeights()
    {
        // Run before any item-definition access: the old lazy-load order silently used 1/0/0.
        Check(ItemCatalog.RarityWeightsFor() == (10000000, 500, 375), "MF: first call loads all rarity weights");
        var goldens = new[] {
            (0.3, 1.0, 9904153.0, 549.0, 399.0),
            (1.0, 1.0, 9901961.0, 655.0, 452.0),
            (10.0, 1.0, 9901088.0, 1457.0, 816.0),
            (100.0, 1.0, 9901000.0, 2482.0, 1218.0),
            (10.0, 2.0, 9804305.0, 1716.0, 952.0),
            (10.0, 0.5, 9950273.0, 1172.0, 675.0),
            (1000000.0, 1.0, 9900990.0, 2750.0, 1312.0),
        };
        foreach (var (mf, factor, normal, unique, set) in goldens)
            Check(ItemCatalog.RarityWeightsFor(mf, factor) == (normal, unique, set), $"MF: native golden weights MF={mf}, factor={factor}");
        Check(ItemCatalog.RarityWeightsFor(10, 0) == ItemCatalog.RarityWeightsFor() &&
              ItemCatalog.RarityWeightsFor(-1) == ItemCatalog.RarityWeightsFor(), "MF: disabled/negative bonus preserves base weights");
        var weights = ItemCatalog.RarityWeightsFor(10);
        var expected = new CombatRandom(772);
        var actual = new CombatRandom(772);
        for (var i = 0; i < 200000; i++)
        {
            var draw = expected.NextDouble() * (weights.Normal + weights.Unique + weights.Set);
            var type = draw < weights.Set ? ItemCatalog.RarityType.Set :
                draw < weights.Set + weights.Unique ? ItemCatalog.RarityType.Unique : ItemCatalog.RarityType.Normal;
            if (ItemCatalog.RollRarityType(actual, 10) != type) throw new Exception("MF: roll did not consume recovered weights");
        }
        Check(true, "MF: 200,000 seeded rolls use exact adjusted weights");
    }

    public static void EquippedWeaponGate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nord-projectile-" + Guid.NewGuid());
        try
        {
            using var store = new GameStore(directory);
            var owner = store.GetOrCreateUser("device:projectile-gate").UserId;
            var id = store.CreateCharacter(owner, new CreateCharacterRequest
            {
                DisplayName = "projectile-test", CharacterGameMode = GameMode.Normal,
                Data = new SerializedCharacterData { Data = Defaults.Create<SerializedCharacterData.SerializedData>() },
            }).CharacterId;
            var bow = LootTable.CreateItem(new LootDrop(12, 0, 1, false, 1));
            bow.DefinitionIntegerId = ItemCatalog.Definitions.First(d => d.Type == "Bow").IntegerId;
            bow.Slot = ItemSlotTypes.MainHand;
            var sword = LootTable.CreateItem(new LootDrop(12, 0, 1, false, 2));
            sword.DefinitionIntegerId = ItemCatalog.Definitions.First(d => d.Type == "Sword1H").IntegerId;
            sword.Slot = ItemSlotTypes.Inventory;
            store.GrantItems(owner, id, new List<SerializedItem> { bow, sword });
            var registry = new CombatRegistry(store);
            var instance = registry.GetOrCreate(owner, id);
            Check(instance.ProjectileAutoAttack, "fork: registry restores projectile mode from equipped bow");
            var equipped = registry.ApplyCommand(owner, id, "sword", instance.Version, new WebCommandRequest("equip", ItemId: sword.Id));
            Check(equipped.Applied && !instance.ProjectileAutoAttack, "fork: equipping a sword immediately disables projectile procs");
            equipped = registry.ApplyCommand(owner, id, "bow", instance.Version, new WebCommandRequest("equip", ItemId: bow.Id));
            Check(equipped.Applied && instance.ProjectileAutoAttack, "fork: re-equipping a bow restores projectile procs");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static readonly ClassPowerPool TestPowers = new("test", new[] {
        new SkillProfile(0, "TestPhysical", "", "", "strike", 1, 0, 8, 0, 0, 0, 0, "test", new Dictionary<string,double>())
    }, Array.Empty<PassiveProfile>());

    private static CombatInstance Create(CombatantStats stats, ulong seed = 123, int count = 1)
    {
        var result = new CombatInstance(stats, 0, 0, 0, 0, seed, monsterCount: count, classPowers: TestPowers);
        foreach (var m in result.Monsters)
        {
            m.X = 5 + m.Index; m.Z = 0; m.Hp = m.MaxHp = 10000;
            m.Armor = m.Defense = 0; m.Speed = 0; m.Offense = 0;
            m.AttackCooldown = 1000; m.Resistances = default;
        }
        return result;
    }

    private static CombatantStats Stats => new(100, 1000, 0, 1,
        AttackRating: 1e12, CritChance: 1, Damage: new DamageBundle(Physical: 100));

    public static void OnHit()
    {
        var poison = Create(Stats with { PoisonChance = 1 });
        var monster = poison.Monsters[0];
        monster.Resistances = new ResistanceBundle(Poison: 0.5);
        var cast = poison.UseSkill(0);
        Check(cast.Cast && cast.Damage > 0 && monster.PoisonTimer == 1 &&
            Math.Abs(monster.PoisonDps - cast.Damage * 0.2) < 1e-9,
            "poison: physical hit creates 1s poison at 20% of hit damage (no weapon-poison dependency)");
        var hp = monster.Hp;
        poison.Advance(1.25);
        Check(Math.Abs(hp - monster.Hp - cast.Damage * 0.2 * 0.5) < 1e-8 && monster.PoisonTimer == 0 && monster.PoisonDps == 0,
            "poison: exactly 1s damage, respects resistance, expires without further procs");
        monster.PoisonTimer = 0.0125; monster.PoisonDps = 100; hp = monster.Hp;
        poison.Advance(0.05);
        Check(Math.Abs(hp - monster.Hp - 0.625) < 1e-8, "poison: final fractional tick never exceeds remaining duration");

        var immune = Create(Stats with { PoisonOnHit = true });
        immune.Monsters[0].Resistances = new ResistanceBundle(Poison: 1);
        immune.UseSkill(0); hp = immune.Monsters[0].Hp;
        immune.Advance(1.1);
        Check(immune.Monsters[0].Hp == hp, "poison: 100% poison resistance prevents DoT, without a minimum-damage floor");

        var first = Create(Stats with { PoisonChance = 1, DoubleDamageOnCritPoisoned = 1 });
        var baseFirst = Create(Stats with { PoisonChance = 1 });
        Check(first.UseSkill(0).Damage == baseFirst.UseSkill(0).Damage,
            "poison crit: newly applied poison does not double the same hit");
        var bonus = Create(Stats with { DoubleDamageOnCritPoisoned = 1 });
        var noBonus = Create(Stats);
        bonus.Monsters[0].PoisonTimer = noBonus.Monsters[0].PoisonTimer = 1;
        Check(bonus.UseSkill(0).Damage == 2 * noBonus.UseSkill(0).Damage,
            "poison crit: pre-existing poison enables double damage");

        var kill = Create(Stats with { PoisonOnHit = true });
        kill.Monsters[0].Hp = 1; kill.UseSkill(0); kill.Advance(1.1);
        Check(kill.Kills == 1 && kill.Monsters[0].PoisonTimer == 0 && kill.Monsters[0].PoisonDps == 0,
            "poison: killing blow clears status and rewards only once");

        var foundMiss = false;
        for (ulong seed = 1; seed <= 100 && !foundMiss; seed++)
        {
            var miss = Create(Stats with { PoisonOnHit = true }, seed);
            miss.Monsters[0].Defense = 1e30;
            if (miss.UseSkill(0).Damage != 0) continue;
            foundMiss = true;
            Check(miss.Monsters[0].PoisonTimer == 0, "poison: a missed attack never applies poison");
        }
        Check(foundMiss, "poison: miss branch exercised");

        var fork = Create(Stats with { ProjectileAutoAttack = true, ForkChance = 1, ChainChance = 1 }, count: 4);
        for (var i = 0; i < 4; i++) fork.Monsters[i].X = 1 + i;
        fork.Monsters[2].Armor = 1e9;
        fork.Advance(0.05);
        var losses = fork.Monsters.Select(m => m.MaxHp - m.Hp).ToArray();
        Check(losses[0] > 130 && losses[1] > 60 && losses[1] < 100 && losses[2] > 0 && losses[2] < 15 && losses[3] == 0,
            "fork: two 50% children resolve their own armor; no chain or recursive extra hits");
        var melee = Create(Stats with { ForkChance = 1, ChainChance = 1 }, count: 4);
        for (var i = 0; i < 4; i++) melee.Monsters[i].X = 1 + i;
        melee.Advance(0.05);
        Check(melee.Monsters.Count(m => m.Hp < m.MaxHp) == 1, "fork: melee attacks cannot fork or chain");
        var secondaryPoison = Create(Stats with { ProjectileAutoAttack = true, ForkChance = 1, PoisonOnHit = true }, count: 4);
        for (var i = 0; i < 4; i++) secondaryPoison.Monsters[i].X = 1 + i;
        secondaryPoison.Advance(0.05);
        Check(secondaryPoison.Monsters.Count(m => m.PoisonTimer > 0) == 3,
            "fork: child hits apply on-hit poison without recursively forking");
    }

    public static void ConversionPenetration()
    {
        // ApplyWeaponDamageConversion: source reduced by exactly the moved amount.
        var split = CombatModel.ConvertDamage(new DamageBundle(Physical: 100), new DamageBundle(Fire: 0.2, Cold: 0.3));
        Check(Math.Abs(split.Physical - 50) < 1e-9 && Math.Abs(split.Fire - 20) < 1e-9 && Math.Abs(split.Cold - 30) < 1e-9,
            "conversion: 100 physical becomes 50 physical + 20 fire + 30 cold");
        // Sum above 1 normalises to a full split.
        var over = CombatModel.ConvertDamage(new DamageBundle(Physical: 100), new DamageBundle(Fire: 0.8, Cold: 0.8));
        Check(Math.Abs(over.Physical) < 1e-9 && Math.Abs(over.Fire - 50) < 1e-9 && Math.Abs(over.Cold - 50) < 1e-9,
            "conversion: percentages above 100% normalise before applying");
        Check(CombatModel.ConvertDamage(new DamageBundle(Physical: 10), default) == new DamageBundle(Physical: 10),
            "conversion: no conversion leaves the bundle unchanged");

        // Penetration subtracts from resistance; a weakness (negative) raises damage.
        var pen = CombatModel.ApplyPenetration(new ResistanceBundle(Fire: 0.5), new ResistanceBundle(Fire: 0.75));
        Check(Math.Abs(pen.Fire + 0.25) < 1e-9, "penetration: 0.5 - 0.75 = -0.25");
        var withPen = CombatModel.MitigateDamage(new DamageBundle(Fire: 100), 0, pen);
        var without = CombatModel.MitigateDamage(new DamageBundle(Fire: 100), 0, new ResistanceBundle(Fire: 0.5));
        Check(Math.Abs(without - 50) < 1e-9 && withPen > 120,
            $"penetration: negative resistance raises fire damage ({without:F1} -> {withPen:F1})");
    }

    public static void ItemQuantityRemainder()
    {
        var remainder = 0.0;
        var counts = new int[4];
        for (var i = 0; i < 4; i++) counts[i] = CombatRegistry.RollItemsPerDrop(0.5, ref remainder);
        Check(counts.SequenceEqual(new[] { 1, 2, 1, 2 }),
            "item quantity: +50% carries the fraction across drops (1,2,1,2)");
        var zero = 0.0;
        Check(CombatRegistry.RollItemsPerDrop(0, ref zero) == 1 && zero == 0,
            "item quantity: no bonus drops exactly one and keeps no remainder");
        var high = 0.0;
        Check(CombatRegistry.RollItemsPerDrop(100, ref high) == 5,
            "item quantity: capped at MaxQuantityFromMagicFindMultiplier = 5");
    }

    public static void RangedKiting()
    {
        var stats = CombatantStats.FromRealtime(0, 100000, 0, 1);
        var profile = new MonsterProfile("Archer", HpMult: 1e6, OffenseMult: 0, Speed: 4,
            Ranged: true, AttackRange: 8, PreferredDistance: 4);
        var instance = new CombatInstance(stats, 0, 0, 0, 0, seed: 5, monsterCount: 1,
            monsterProfiles: new[] { profile });
        instance.MoveTo(0, 0);
        var archer = instance.Monsters[0];
        archer.X = 2;
        archer.Z = 0;
        instance.Advance(0.05);
        var afterClose = Math.Sqrt(archer.X * archer.X + archer.Z * archer.Z);
        Check(afterClose > 2, $"ai: a ranged monster backs off when the player is inside its preferred distance ({afterClose:F2})");
        archer.X = 20;
        archer.Z = 0;
        instance.Advance(0.05);
        Check(archer.X < 20, $"ai: a ranged monster closes in while outside its attack range ({archer.X:F2})");
    }

    public static void BrainSelection()
    {
        Check(BrainCatalog.For("Standard") is not null, "brain: the Standard tree loads");
        var attack = BrainCatalog.Choose(BrainCatalog.For("Standard"), n => n == "StateCombat", () => 0.0);
        Check(attack?.Power == "DefaultAttackProxy", $"brain: Standard attacks in combat ({attack?.Power})");
        var wander = BrainCatalog.Choose(BrainCatalog.For("Standard"), n => n == "StateWander", () => 0.0);
        Check(wander?.Power == "Wander", "brain: Standard wanders out of combat");
        Check(BrainCatalog.Choose(BrainCatalog.For("Standard"), _ => false, () => 0.0) is null,
            "brain: no eligible action selects nothing");
        // Boss trees expose their named special powers.
        var boss = BrainCatalog.For("Boss_WolfKing");
        Check(boss is not null && boss.Actions.Any(a => a.Power == "WolfKingRoar")
            && boss.Actions.Any(a => a.Power == "WolfKingSummonPack"),
            "brain: a boss tree exposes its special powers");
        var instance = new CombatInstance(CombatantStats.FromRealtime(0, 1e6, 0, 1), 0, 0, 0, 0, seed: 3,
            monsterCount: 1, monsterProfiles: new[] { new MonsterProfile("Bat", Brain: "Standard") });
        instance.MoveTo(0, 0);
        instance.Advance(0.05);
        Check(instance.Monsters[0].BrainAction is "DefaultAttackProxy" or "Wander" or "Flee",
            $"brain: a monster selects and runs an action ({instance.Monsters[0].BrainAction})");
    }

    public static void BossPowers()
    {
        Check(MonsterPowerCatalog.For("WolfKingSummonPack")!.Count == 3
            && MonsterPowerCatalog.For("BoneDragonSummonSkeleton")!.Count == 1
            && MonsterPowerCatalog.For("HelSummonPack")!.Count == 8,
            "boss powers: recovered summon counts (3/1/8)");
        Check(MonsterPowerCatalog.For("VileDragonNova")!.Radius == 6.0
            && MonsterPowerCatalog.For("WolfKingRoar")!.Radius == 3.0,
            "boss powers: recovered nova radii (6/3)");
        // Every boss power referenced by a brain has an effect descriptor.
        var missing = new List<string>();
        foreach (var brain in BrainCatalog.Brains.Values)
            foreach (var action in brain.Actions)
                if (action.Power.EndsWith("Nova") || action.Power.EndsWith("SummonPack")
                    || action.Power.EndsWith("Whirlwind") || action.Power.EndsWith("Charge")
                    || action.Power is "WolfKingRoar" or "DragonFireBreath" or "FallenAngelRay"
                        or "BolomahlTripleStrike" or "BoneDragonSummonSkeleton" or "VileDragonNova"
                        or "HelHomingFire")
                    if (MonsterPowerCatalog.For(action.Power) is null) missing.Add(action.Power);
        Check(missing.Count == 0, $"boss powers: all boss powers have descriptors ({string.Join(",", missing)})");

        // A Boss_WolfKing boss summons its pack and then stops (IHaveMinions).
        var stats = CombatantStats.FromRealtime(0, 1e6, 0, 10);
        var instance = new CombatInstance(stats, 0, 0, 0, 0, seed: 9, monsterCount: 1,
            monsterProfiles: new[] { new MonsterProfile("Boss", HpMult: 1e6, OffenseMult: 0, Brain: "Boss_WolfKing") });
        instance.MoveTo(0, 0);
        var summoned = 0;
        for (var i = 0; i < 4000 && summoned == 0; i++)
        {
            instance.Advance(0.05);
            summoned = instance.Monsters.Count(m => m.Alive && !m.IsBoss) - 1;
        }
        Check(summoned >= 1, $"boss powers: Boss_WolfKing summons a pack ({summoned} minions)");
        Check(instance.Monsters.Any(m => m.Name == "WolfKingMinion"),
            $"boss powers: summons use the gamedata minion ({string.Join(",", instance.Monsters.Select(m => m.Name).Distinct())})");
    }

    public static void Curses()
    {
        Check(MonsterPowerCatalog.For("MonsterCurseSlow")!.Curse == CurseEffect.Slow
            && MonsterPowerCatalog.For("MonsterCurseLowerResistances")!.Curse == CurseEffect.LowerResistances
            && MonsterPowerCatalog.For("MonsterCurseAmplifyDamageTaken")!.Curse == CurseEffect.AmplifyDamageTaken
            && MonsterPowerCatalog.For("MonsterCurseReducedWeaponDamage")!.Curse == CurseEffect.ReducedWeaponDamage
            && MonsterPowerCatalog.For("MonsterCurseLivingLeech")!.Curse == CurseEffect.Leech,
            "curses: the recovered target attributes are mapped");
        // A champion caster uses the StandardCurseSlow brain and slows the player.
        var stats = CombatantStats.FromRealtime(0, 1e6, 0, 10);
        var instance = new CombatInstance(stats, 0, 0, 0, 0, seed: 4, monsterCount: 1,
            monsterProfiles: new[] { new MonsterProfile("Curser", HpMult: 1e6, OffenseMult: 0,
                Brain: "StandardCurseSlow", Champion: true) });
        instance.MoveTo(0, 0);
        var slowed = false;
        for (var i = 0; i < 3000 && !slowed; i++)
        {
            instance.Advance(0.05);
            slowed = instance.CurseSlow > 0;
        }
        Check(slowed, $"curses: a champion curser slows the player ({instance.CurseSlow:F2})");
    }

    public static void SkillMechanics()
    {
        var stats = CombatantStats.FromRealtime(5000, 5000, 0, 30) with { ManaMax = 500 };
        // ChainLightning: ChainLightning_Max_Num_Chains = 4 (client).
        var chainPool = Nordicandia.Server.WebApi.PowerCatalog.BuildPool(5, new[] { "ChainLightning" }, Array.Empty<string>());
        var chain = new CombatInstance(stats, 0, 0, 0, 0, seed: 21, monsterCount: 8, classPowers: chainPool);
        chain.MoveTo(0, 0);
        for (var i = 0; i < chain.Monsters.Count; i++) { chain.Monsters[i].X = 2 + i; chain.Monsters[i].Z = 0; }
        var chainOutcome = chain.UseSkill(0);
        var chainHits = chain.Monsters.Count(m => m.Hp < m.MaxHp);
        Check(chainOutcome.Cast && chainHits == 4, $"skill: ChainLightning hits its 4 chains ({chainHits})");

        // IceNova: Power_Freeze_Duration = 2s (client).
        var icePool = Nordicandia.Server.WebApi.PowerCatalog.BuildPool(5, new[] { "IceNova" }, Array.Empty<string>());
        var ice = new CombatInstance(stats, 0, 0, 0, 0, seed: 22, monsterCount: 6, classPowers: icePool);
        ice.MoveTo(0, 0);
        for (var i = 0; i < ice.Monsters.Count; i++) { ice.Monsters[i].X = 1 + i; ice.Monsters[i].Z = 0; }
        ice.UseSkill(0);
        Check(ice.Monsters.Any(m => m.StunTimer > 0), "skill: IceNova freezes enemies");
    }

    public static void DodgeBlock()
    {
        // AttackPayload.Resolve: miss -> dodge -> evade -> block -> crit.
        var attacker = CombatantStats.FromRealtime(1000, 0, 0, 10);
        var rng = new CombatRandom(7);
        var dodger = CombatantStats.FromRealtime(1000, 0, 0, 10) with { DodgeChance = 1.0 };
        var dodged = CombatModel.ResolveBundleAttack(attacker, new DamageBundle(Physical: 100), dodger,
            default, new AttackProfile(1.0, 0, 0, 0), rng);
        Check(!dodged.Hit && dodged.Damage == 0, "damage order: a 100% dodge avoids the hit");
        var blocker = CombatantStats.FromRealtime(1000, 0, 0, 10) with { BlockChance = 1.0, BlockedDamageMultiplier = 0.5 };
        var blocked = CombatModel.ResolveBundleAttack(attacker, new DamageBundle(Physical: 100), blocker,
            default, new AttackProfile(1.0, 0, 0, 0), rng);
        var plain = CombatModel.ResolveBundleAttack(attacker, new DamageBundle(Physical: 100),
            CombatantStats.FromRealtime(1000, 0, 0, 10), default, new AttackProfile(1.0, 0, 0, 0), rng);
        Check(blocked.Hit && Math.Abs(blocked.Damage - plain.Damage * 0.5) < 1e-9,
            $"damage order: a 100% block halves the hit ({plain.Damage:F1} -> {blocked.Damage:F1})");
        // Ignores_Critical_Hits (0x1168) on the defender prevents crits.
        var critter = CombatantStats.FromRealtime(1000, 0, 0, 10) with { CritChance = 1.0 };
        var immune = CombatantStats.FromRealtime(1000, 0, 0, 10) with { IgnoresCrits = true };
        var noCrit = CombatModel.ResolveBundleAttack(critter, new DamageBundle(Physical: 100), immune,
            default, new AttackProfile(1.0, 0, 2.0, 0), new CombatRandom(3));
        Check(noCrit.Hit && !noCrit.Critical, "damage order: Ignores_Critical_Hits prevents a crit");
        // Always_Hits (0x5C8) bypasses the hit roll.
        var always = CombatantStats.FromRealtime(0, 0, 0, 1) with { AlwaysHits = true };
        var evasive = CombatantStats.FromRealtime(0, 0, 0, 50) with { Evasion = 1e9 };
        var guaranteed = CombatModel.ResolveBundleAttack(always, new DamageBundle(Physical: 100), evasive,
            default, new AttackProfile(1.0, 0, 0, 0), new CombatRandom(3));
        Check(guaranteed.Hit, "damage order: Always_Hits bypasses the hit roll");
        // IgnoreArmor / IgnoreResistances bypass the defender's mitigation (HitPayload ctor flags).
        var armored = CombatantStats.FromRealtime(1000, 0, 0, 10) with { Armor = 1000 };
        var mixed = new DamageBundle(Physical: 100, Fire: 100);
        var mitigated = CombatModel.ResolveBundleAttack(attacker, mixed, armored, new ResistanceBundle(Fire: 0.9),
            new AttackProfile(1.0, 0, 0, 0), new CombatRandom(3));
        var bypassed = CombatModel.ResolveBundleAttack(attacker, mixed, armored, new ResistanceBundle(Fire: 0.9),
            new AttackProfile(1.0, 0, 0, 0, IgnoreArmor: true, IgnoreResistances: true), new CombatRandom(3));
        Check(bypassed.Damage > mitigated.Damage,
            $"damage order: IgnoreArmor/IgnoreResistances bypass mitigation ({mitigated.Damage:F1} -> {bypassed.Damage:F1})");
    }
}
