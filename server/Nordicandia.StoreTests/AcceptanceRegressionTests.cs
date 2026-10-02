using Game;
using Nordicandia.Server.State;
using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;
using SharedNet.Api;
using SharedNet.Constants.Game;

/// <summary>
/// Regressions for the independent acceptance report (docs/web/ACCEPTANCE_2026-10-02.md):
/// R1 equipment double-count on restore, R2 commands granting simulation time,
/// R4 stale command replay after the idempotency window, R5 failed-command retries.
/// </summary>
static class AcceptanceRegressionTests
{
    public static void Run()
    {
        EquipmentStatsSurviveRestartWithoutDoubleCount();
        CommandsDoNotAdvanceSimulationTime();
        StaleCommandsAreRejected();
        EqualVersionEvictedCommandIsRejected();
        MissingCommandIdIsRejected();
        CombatVersionSurvivesRestart();
        WebCharacterGetsRecoveredRatings();
        EquippedWeaponFeedsWeaponDamage();
        SmeltAndDisassembleConsumeSources();
        CraftEssenceConsumesIron();
        RealAffixesCarryClientAttributes();
        SetBonusesAndEquipRequirements();
        UniqueAndSetItemDefinitions();
        NpcSlotPlacementSmelts();
        AttributeAllocationAccumulates();
        SetMerchantAndDropPity();
        FailedCommandRetryKeepsFailure();
        RegistrationPolicyIsShared();
        FreshCharacterClearsBoss();
        SkillSemanticsMatchClientClasses();
        DamageReductionMatchesClient();
        ResistanceMatchesClient();
        ChanceToHitMatchesClient();
    }

    private static void ResistanceMatchesClient()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        // Game.Calculator.ApplyDamageReduction: Max(0, raw * (1 - reduction))
        Check(Math.Abs(Nordicandia.Simulation.CombatModel.ApplyDamageReduction(0.25, 100) - 75) < 1e-9,
            "resistance: ApplyDamageReduction = raw * (1 - red)");
        Check(Math.Abs(Nordicandia.Simulation.CombatModel.ApplyDamageReduction(2.0, 100)) < 1e-9,
            "resistance: reduction above 1 floors at 0");
        Check(Math.Abs(Nordicandia.Simulation.CombatModel.EffectiveElementalDamage(100, 1.5)) < 1e-9,
            "resistance: elemental resistance is capped at 1.0");
        Check(Math.Abs(Nordicandia.Simulation.CombatModel.EffectiveElementalDamage(100, -0.5) - 150) < 1e-9,
            "resistance: a negative resistance (weakness) increases damage");

        // Full bundle: physical through armour, each element through its resistance.
        var bundle = new Nordicandia.Simulation.DamageBundle(Physical: 100, Fire: 100);
        var resist = new Nordicandia.Simulation.ResistanceBundle(Fire: 0.5);
        Check(Math.Abs(Nordicandia.Simulation.CombatModel.MitigateDamage(bundle, 0, resist) - 150) < 1e-9,
            "damage: MitigateDamage = physical + fire*(1-resist)");
        Check(Math.Abs(Nordicandia.Simulation.CombatModel.MitigateDamage(new Nordicandia.Simulation.DamageBundle(Physical: 50), 50, default)
            - 50 * (1 - 50.0 / 2550.0)) < 1e-9,
            "damage: MitigateDamage applies armour to the physical part");
    }

    private static void ChanceToHitMatchesClient()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var m = Nordicandia.Simulation.CombatModel.ChanceToHit(100, 100);
        Check(Math.Abs(m - 1.05 * 100 / (Math.Pow(50, 0.75) + 100)) < 1e-9, "formula: chanceToHit = 1.05*atk/(Pow(def/2,0.75)+atk)");
        Check(Math.Abs(Nordicandia.Simulation.CombatModel.ChanceToHit(0, 100) - 0.05) < 1e-9, "formula: chanceToHit floor 0.05");
        Check(Math.Abs(Nordicandia.Simulation.CombatModel.ChanceToHit(100000, 1) - 1.0) < 1e-9, "formula: chanceToHit cap 1.0");
    }

    private static void DamageReductionMatchesClient()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        // Game.Calculator.CalculatePhysicalDamageReduction: armor/(armor + 50*damage)
        Check(Math.Abs(Nordicandia.Simulation.CombatModel.PhysicalDamageReduction(100, 1) - 100.0 / 150.0) < 1e-9,
            "formula: reduction = armor/(armor + 50*damage)");
        Check(Math.Abs(Nordicandia.Simulation.CombatModel.PhysicalDamageReduction(100, 0.01)) <= 0.9,
            "formula: reduction is capped");
        Check(Math.Abs(Nordicandia.Simulation.CombatModel.PhysicalDamageReduction(0, 100)) < 1e-9,
            "formula: no armor -> no reduction");
    }

    private static void SkillSemanticsMatchClientClasses()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var mage = Nordicandia.Server.WebApi.PowerCatalog.ForClass(5);
        var necro = Nordicandia.Server.WebApi.PowerCatalog.ForClass(6);
        Check(mage.Active.First(a => a.Name == "IceNova").Effect == "nova", "semantics: IceNova < Nova -> nova");
        Check(mage.Active.First(a => a.Name == "ChainLightning").Effect == "chain", "semantics: ChainLightning -> chain");
        Check(necro.Active.First(a => a.Name == "SummonSkeleton").Effect == "summon", "semantics: SummonSkeleton < PowerScript -> summon");
        Check(necro.Active.First(a => a.Name == "Shadowbolt").Effect == "projectile", "semantics: Shadowbolt < ProjectileSkill -> projectile");
        Check(Nordicandia.Simulation.SkillDamageTypes.For("IceNova") == "Cold", "semantics: IceNova deals Cold");
        Check(Nordicandia.Simulation.SkillDamageTypes.For("ChainLightning") == "Lightning", "semantics: ChainLightning deals Lightning");
        Check(Nordicandia.Simulation.SkillDamageTypes.For("Slam") == "Physical", "semantics: Slam deals Physical");
    }

    private static void FreshCharacterClearsBoss()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        foreach (var (classId, label) in new[] { (0, "Warrior"), (4, "Hunter"), (5, "Mage"), (6, "Necromancer") })
        {
            var instance = new CombatInstance(
                CombatantStats.FromRealtime(Nordicandia.Server.WebApi.WebApiEndpoints.StarterOffense, Nordicandia.Server.WebApi.WebApiEndpoints.StarterDefense, Nordicandia.Server.WebApi.WebApiEndpoints.StarterRecovery, 1), 0, 0, 0, 0, seed: 777,
                classPowers: Nordicandia.Server.WebApi.PowerCatalog.ForClass(classId));
            var seconds = 0.0;
            while (instance.DungeonsCleared == 0 && seconds < 300)
            {
                instance.Advance(0.5);
                seconds += 0.5;
            }
            Console.WriteLine($"INFO fresh {label}: cleared={instance.DungeonsCleared} in {seconds:F0}s kills={instance.Kills} level={instance.PlayerLevel} hp={instance.PlayerHp:F0}");
            Check(instance.DungeonsCleared >= 1, $"R-fresh: a fresh {label} clears the boss");
        }
    }

    private static void RegistrationPolicyIsShared()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var saved = Environment.GetEnvironmentVariable("NORD_ALLOW_REGISTRATION");
        try
        {
            Environment.SetEnvironmentVariable("NORD_ALLOW_REGISTRATION", "0");
            Check(!WebApiEndpoints.RegistrationAllowed, "R3 registration respects NORD_ALLOW_REGISTRATION=0");
            Environment.SetEnvironmentVariable("NORD_ALLOW_REGISTRATION", "1");
            Check(WebApiEndpoints.RegistrationAllowed, "R3 registration stays open when allowed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NORD_ALLOW_REGISTRATION", saved);
        }
    }

    private static (GameStore store, CombatRegistry registry, Guid owner, Guid characterId, FakeClock clock)
        Create(string tag, double offense)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nord-acc-" + tag + "-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var store = new GameStore(dir);
        var clock = new FakeClock();
        var owner = store.GetOrCreateUser("device:" + tag).UserId;
        var data = Defaults.Create<SerializedCharacterData.SerializedData>();
        data.CombatStats = new SerializedCharacterData.SerializedCombatStats { Offense = offense, Defense = 50, Recovery = 5 };
        var characterId = store.CreateCharacter(owner, new CreateCharacterRequest
        {
            DisplayName = tag,
            CharacterClass = CharacterClass.Warrior,
            CharacterRace = CharacterRace.Human,
            CharacterGameMode = GameMode.Normal,
            Data = new SerializedCharacterData { Data = data },
        }).CharacterId;
        return (store, new CombatRegistry(store, clock), owner, characterId, clock);
    }

    private static void EquipmentStatsSurviveRestartWithoutDoubleCount()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, registry, owner, characterId, clock) = Create("r1", 100);
        using (store)
        {
            var item = LootTable.CreateItem(new LootDrop(12, 5, 10, false, 1234));
            var bonus = LootTable.AttributeOf(item, LootTable.AttrOffense);
            Check(bonus > 0, "R1 setup: item has an offence bonus");
            store.GrantItems(owner, characterId, new List<SerializedItem> { item });

            var version = registry.Advance(owner, characterId).Combat.Version;
            var equip = registry.ApplyCommand(owner, characterId, "r1-equip", version,
                new WebCommandRequest("equip", ItemId: item.Id));
            Check(equip.Applied, "R1 setup: item equips");

            clock.Advance(TimeSpan.FromSeconds(6));
            registry.Advance(owner, characterId);
            var before = registry.Inventory(owner, characterId).Offense;

            // Simulate a real restart: drop the instance and re-seed from the store.
            registry.Reset();
            var after = registry.Inventory(owner, characterId).Offense;
            Check(Math.Abs(after - before) < 0.01, "R1 equipment is not double-counted after restart");

            var version2 = registry.Advance(owner, characterId).Combat.Version;
            registry.ApplyCommand(owner, characterId, "r1-unequip", version2,
                new WebCommandRequest("unequip", ItemId: item.Id));
            var baseOnly = registry.Inventory(owner, characterId).Offense;
            Check(Math.Abs((before - baseOnly) - bonus) < 0.01, "R1 unequip removes exactly the bonus");
        }
    }

    private static void CommandsDoNotAdvanceSimulationTime()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, registry, owner, characterId, _) = Create("r2", 800);
        using (store)
        {
            var start = registry.Advance(owner, characterId).Combat;
            for (var i = 0; i < 200; i++)
                registry.ApplyCommand(owner, characterId, $"r2-{i}", start.Version,
                    i % 2 == 0 ? new WebCommandRequest("invalid") : new WebCommandRequest("move", 3, 3));
            var end = registry.Advance(owner, characterId).Combat;

            Check(Math.Abs(end.Experience - start.Experience) < 0.001 && end.Kills == start.Kills,
                "R2 commands grant no rewards with a frozen clock");
            Check(end.Skills.Select(s => s.Cooldown).SequenceEqual(start.Skills.Select(s => s.Cooldown)),
                "R2 commands do not tick skill cooldowns");
        }
    }

    private static void StaleCommandsAreRejected()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, registry, owner, characterId, clock) = Create("r4", 600);
        using (store)
        {
            var a = LootTable.CreateItem(new LootDrop(3, 3, 10, false, 11));
            var b = LootTable.CreateItem(new LootDrop(3, 4, 10, false, 22));
            store.GrantItems(owner, characterId, new List<SerializedItem> { a, b });

            var v0 = registry.Advance(owner, characterId).Combat.Version;
            registry.ApplyCommand(owner, characterId, "r4-a", v0, new WebCommandRequest("equip", ItemId: a.Id));
            registry.ApplyCommand(owner, characterId, "r4-b", v0, new WebCommandRequest("equip", ItemId: b.Id));

            // Push the original command out of the retained window with advancing versions.
            for (var i = 0; i < GameStore.MaxCommandLog + 40; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                var version = registry.Advance(owner, characterId).Combat.Version;
                registry.ApplyCommand(owner, characterId, $"r4-noise-{i}", version, new WebCommandRequest("invalid"));
            }

            var replay = registry.ApplyCommand(owner, characterId, "r4-a", v0, new WebCommandRequest("equip", ItemId: a.Id));
            Check(!replay.Applied && replay.Reason == "stale_command", "R4 evicted old command is rejected as stale");
            var inventory = registry.Inventory(owner, characterId);
            Check(inventory.Items.First(i => i.Id == b.Id).Equipped && !inventory.Items.First(i => i.Id == a.Id).Equipped,
                "R4 stale replay does not re-equip the old item");
        }
    }

    private static void EqualVersionEvictedCommandIsRejected()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, registry, owner, characterId, _) = Create("r4b", 600);
        using (store)
        {
            var a = LootTable.CreateItem(new LootDrop(3, 3, 10, false, 11));
            var b = LootTable.CreateItem(new LootDrop(3, 4, 10, false, 22));
            store.GrantItems(owner, characterId, new List<SerializedItem> { a, b });

            var v0 = registry.Advance(owner, characterId).Combat.Version;
            registry.ApplyCommand(owner, characterId, "r4b-a", v0, new WebCommandRequest("equip", ItemId: a.Id));
            registry.ApplyCommand(owner, characterId, "r4b-b", v0, new WebCommandRequest("equip", ItemId: b.Id));

            // The exact recheck repro: every noise command carries the same low expected
            // version and does not advance the clock. The old client-supplied boundary stayed
            // flat, so the evicted replay slipped through. The server-assigned boundary must not.
            for (var i = 0; i < GameStore.MaxCommandLog + 10; i++)
                registry.ApplyCommand(owner, characterId, $"r4b-noise-{i}", v0, new WebCommandRequest("invalid"));

            var replay = registry.ApplyCommand(owner, characterId, "r4b-a", v0, new WebCommandRequest("equip", ItemId: a.Id));
            Check(!replay.Applied && replay.Reason == "stale_command", "R4b equal-version evicted command is rejected as stale");
            var inventory = registry.Inventory(owner, characterId);
            Check(inventory.Items.First(i => i.Id == b.Id).Equipped && !inventory.Items.First(i => i.Id == a.Id).Equipped,
                "R4b stale equal-version replay does not re-equip the old item");
        }
    }

    private static void MissingCommandIdIsRejected()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, registry, owner, characterId, _) = Create("r4c", 600);
        using (store)
        {
            var v0 = registry.Advance(owner, characterId).Combat.Version;
            var result = registry.ApplyCommand(owner, characterId, string.Empty, v0, new WebCommandRequest("move", 3, 3));
            Check(!result.Applied && result.Reason == "missing_command_id", "R4c empty command id is rejected");
        }
    }

    private static void WebCharacterGetsRecoveredRatings()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, registry, owner, characterId, _) = Create("r7", 100);
        using (store)
        {
            // A freshly created web character is seeded with the client's Base_* defaults, so the
            // recovered attribute engine must produce non-zero combat ratings for it.
            var instance = registry.GetOrCreate(owner, characterId);
            Console.WriteLine($"INFO ratings: atk={instance.AttackRating:F2} armor={instance.Armor:F2} " +
                $"evasion={instance.Evasion:F2} crit={instance.CritChance:F3} life={instance.LifeMax:F1} mana={instance.ManaMax:F1}");
            Check(instance.AttackRating > 0, "ratings: fresh character has a recovered AttackRating");
            Check(instance.Armor > 0, "ratings: fresh character has recovered Armor");
            Check(instance.Evasion > 0, "ratings: fresh character has recovered Evasion");
            // Crit comes from the weapon's Item_Crit_Chance_MainHand; a weaponless character is 0.
            Check(instance.CritChance >= 0 && !double.IsNaN(instance.CritChance), "ratings: CritChance is resolved (0 without a weapon)");
            Check(instance.LifeMax > 0, "ratings: fresh character has recovered Life_Max");
            Check(instance.ManaMax > 0, "ratings: fresh character has recovered Mana_Max");
        }
    }

    private static void SmeltAndDisassembleConsumeSources()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, registry, owner, characterId, _) = Create("r9", 100);
        using (store)
        {
            var a = LootTable.CreateItem(new LootDrop(3, 5, 10, false, 1));
            var b = LootTable.CreateItem(new LootDrop(3, 6, 10, false, 2));
            a.Slot = ItemSlotTypes.Blacksmith_SourceItem;
            b.Slot = ItemSlotTypes.Blacksmith_SourceItem;
            store.GrantItems(owner, characterId, new List<SerializedItem> { a, b });

            var (ok, _, result) = store.SmeltItems(owner, characterId);
            Check(ok, "smelt: succeeds with source items");
            Check(store.GetItems(owner, characterId).All(i => i.Slot != ItemSlotTypes.Blacksmith_SourceItem),
                "smelt: the source items are consumed");
            Check(result.Items.Count == 1 && result.Items[0].Name == "Steel" && result.Items[0].DefinitionIntegerId == 589,
                "smelt: produces a Steel stack");

            var c = LootTable.CreateItem(new LootDrop(3, 4, 10, false, 3));
            c.Slot = ItemSlotTypes.Blacksmith_SourceItem;
            store.GrantItems(owner, characterId, new List<SerializedItem> { c });
            var (dOk, _, dResult) = store.DisassembleItems(owner, characterId);
            Check(dOk, "disassemble: succeeds with source items");
            Check(dResult.Items.Count == 1 && dResult.Items[0].Name == "Iron", "disassemble: produces Iron");

            var offer = LootTable.CreateItem(new LootDrop(3, 5, 10, false, 4));
            offer.Slot = ItemSlotTypes.YourTrade;
            store.GrantItems(owner, characterId, new List<SerializedItem> { offer });
            var (tOk, tConsumed, tInventory) = store.TradeWithMerchant(owner, characterId, 606, "GreatElixirOfKnowledge", 2);
            Check(tOk, "trade: applies with items in the YourTrade slot");
            Check(tConsumed.Items.Count == 1, "trade: consumes the offered items");
            Check(tInventory.Items.Any(i => i.DefinitionIntegerId == 606), "trade: grants the product stack");
            Check(store.GetItems(owner, characterId).All(i => i.Slot != ItemSlotTypes.YourTrade), "trade: empties the trade window");
        }
    }

    private static void SetMerchantAndDropPity()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, _, owner, characterId, _) = Create("r14", 100);
        using (store)
        {
            var offer = LootTable.CreateItem(new LootDrop(3, 5, 10, false, 61));
            offer.Slot = ItemSlotTypes.YourTrade;
            store.GrantItems(owner, characterId, new List<SerializedItem> { offer });
            var (applied, _, offered) = store.GenerateSetItemMerchantOffers(owner, characterId);
            Check(applied, "set merchant: generates offers for the trade-window items");
            Check(offered.Items.Count == 1 && LootTable.IsUniqueOrSet(offered.Items[0]),
                "set merchant: the offer is a set item");
            var (traded, result) = store.TradeWithSetItemMerchant(owner, characterId, offer.Id);
            Check(traded && result.Items.Count == 1 && result.Items[0].Name.StartsWith("Set_"),
                "set merchant: trading grants a set item");
        }
        // Drop pity: a forced drop is a unique.
        var forced = LootTable.CreateItem(new LootDrop(3, 5, 10, false, 62), ItemCatalog.RarityType.Unique);
        Check(LootTable.IsUniqueOrSet(forced) && forced.Name.StartsWith("Unique_"),
            "pity: a forced drop is a unique item");
    }

    private static void AttributeAllocationAccumulates()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, _, owner, characterId, _) = Create("r13", 100);
        using (store)
        {
            store.ApplyAllocatedAttributes(owner, characterId, new SharedNet.Api.AllocateCharacterAttributesRequest
            {
                CharacterId = characterId, Strength = 1, Dexterity = 2,
            });
            var first = store.GetAttributeAllocation(owner, characterId);
            Check(first.Strength == 1 && first.Dexterity == 2, "attributes: allocation stores the pending deltas");
            store.ApplyAllocatedAttributes(owner, characterId, new SharedNet.Api.AllocateCharacterAttributesRequest
            {
                CharacterId = characterId, Strength = 3,
            });
            var second = store.GetAttributeAllocation(owner, characterId);
            Check(second.Strength == 4, "attributes: a second allocation adds to (not overwrites) the first");
            Check(second.Available >= 0, "attributes: the available pool never goes negative");
        }
    }

    private static void NpcSlotPlacementSmelts()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, _, owner, characterId, _) = Create("r12", 100);
        using (store)
        {
            var a = LootTable.CreateItem(new LootDrop(3, 5, 10, false, 41));
            var b = LootTable.CreateItem(new LootDrop(3, 6, 10, false, 42));
            store.GrantItems(owner, characterId, new List<SerializedItem> { a, b });
            // The web NPC window places selected items into the Blacksmith slot before the op.
            store.SetItemSlots(owner, characterId, new Dictionary<Guid, ItemSlotTypes>
            {
                [a.Id] = ItemSlotTypes.Blacksmith_SourceItem,
                [b.Id] = ItemSlotTypes.Blacksmith_SourceItem,
            });
            var placed = store.GetItems(owner, characterId).Count(i => i.Slot == ItemSlotTypes.Blacksmith_SourceItem);
            Check(placed == 2, "npc: SetItemSlots places the chosen items");
            var (ok, _, _) = store.SmeltItems(owner, characterId);
            Check(ok, "npc: placed items can be smelted through the same flow the endpoint uses");
        }
    }

    private static void UniqueAndSetItemDefinitions()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var rng = new Nordicandia.Simulation.CombatRandom(1);
        var unique = ItemCatalog.Pick(3, ItemCatalog.RarityType.Unique, rng);
        Check(unique is { IsUnique: true }, "unique: Pick(Unique) returns a unique definition");
        Check(unique is { Implicits.Count: > 0 }, "unique: the definition carries its affix attributes");
        var set = ItemCatalog.Pick(3, ItemCatalog.RarityType.Set, rng);
        Check(set is { SetId: not null }, "set: Pick(Set) returns a set definition with a set id");
        var normal = ItemCatalog.Pick(3, ItemCatalog.RarityType.Normal, rng);
        Check(normal is { IsUnique: false, SetId: null }, "normal: Pick(Normal) is not unique/set");
        Check(ItemCatalog.RarityTypeWeights.GetValueOrDefault("Unique") > 0
            && ItemCatalog.RarityTypeWeights.GetValueOrDefault("Set") > 0,
            "droprates: unique and set weights loaded from Droprates.json");
    }

    private static void SetBonusesAndEquipRequirements()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, registry, owner, characterId, _) = Create("r11", 100);
        using (store)
        {
            // Set bonus: two equipped pieces of set 0 grant its 2-piece breakpoint.
            var piece1 = LootTable.CreateItem(new LootDrop(3, 5, 10, false, 31));
            var piece2 = LootTable.CreateItem(new LootDrop(8, 5, 10, false, 32));
            foreach (var piece in new[] { piece1, piece2 })
            {
                piece.Slot = (ItemSlotTypes)LootTable.EquipSlotOf(piece);
                piece.Attributes.Values[AttributeOrigin.Item][LootTable.AttrSetId] =
                    new GameAttributeValue { Value = 0, ValueD = 0 };
            }
            store.GrantItems(owner, characterId, new List<SerializedItem> { piece1, piece2 });
            var expected = SetCatalog.ActiveBonuses(0, 2);
            var map = store.GetAttributeMap(owner, characterId);
            Check(expected.Count > 0, "set: breakpoint 2 provides bonuses");
            Check(expected.All(kv => Math.Abs(map.GetValueOrDefault(kv.Key) - kv.Value) < 1e-6),
                "set: the 2-piece bonus is applied to the attribute map");

            // Level requirement: an item far above the character's level cannot be equipped.
            var highLevel = LootTable.CreateItem(new LootDrop(3, 5, 60, false, 33));
            store.GrantItems(owner, characterId, new List<SerializedItem> { highLevel });
            var version = registry.Advance(owner, characterId).Combat.Version;
            var result = registry.ApplyCommand(owner, characterId, "r11-high", version,
                new WebCommandRequest("equip", ItemId: highLevel.Id));
            Check(!result.Applied && result.Reason == "level_requirement", "equip: the level requirement blocks a too-high item");
        }
    }

    private static void RealAffixesCarryClientAttributes()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        Check(LootTable.AffixCatalogSize >= 10, $"affixes: real client affix catalog loaded ({LootTable.AffixCatalogSize})");
        var item = LootTable.CreateItem(new LootDrop(3, 6, 20, false, 42));
        Check(item.Affixes.Count > 0, "affixes: a rare item rolls affixes");
        Check(item.Affixes.All(a => a.DefinitionIntegerId > 0 && a.DefinitionIntegerId < 9000),
            "affixes: carry client attribute ids (not the provisional web ids)");

        // Level scaling (GameBalance.ScalingFunctions): a higher-level weapon of the same type
        // rolls at least as much base damage.
        var lowLevel = LootTable.CreateItem(new LootDrop(12, 5, 10, false, 5000));
        var highLevel = LootTable.CreateItem(new LootDrop(12, 5, 60, false, 5000));
        Console.WriteLine($"INFO scaling: weapon min lvl10={LootTable.AttributeOf(lowLevel, 507):F2} lvl60={LootTable.AttributeOf(highLevel, 507):F2}");
        Check(LootTable.AttributeOf(highLevel, 507) >= LootTable.AttributeOf(lowLevel, 507),
            "scaling: a higher-level weapon has >= base damage (same definition/rarity)");
    }

    private static void CraftEssenceConsumesIron()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, registry, owner, characterId, _) = Create("r10", 100);
        using (store)
        {
            SerializedItem Iron() => new()
            {
                Id = Guid.NewGuid(), Name = "Iron", Slot = ItemSlotTypes.Inventory, DefinitionIntegerId = 63, BaseRarity = 0,
                Attributes = new SerializedAttributes
                {
                    Values = new Dictionary<AttributeOrigin, Dictionary<int, GameAttributeValue>>
                    {
                        [AttributeOrigin.Item] = new()
                        {
                            [18] = new GameAttributeValue { Value = 1000, ValueD = 1000 },
                            [19] = new GameAttributeValue { Value = 1, ValueD = 1 },
                        },
                    },
                    MultiplicativeValues = new(),
                },
            };

            var target = LootTable.CreateItem(new LootDrop(3, 4, 10, false, 5));
            target.Slot = ItemSlotTypes.Blacksmith_TargetItem;
            var source = LootTable.CreateItem(new LootDrop(3, 4, 10, false, 6));
            source.Slot = ItemSlotTypes.Blacksmith_SourceItem;
            var items = new List<SerializedItem> { target, source };
            for (var i = 0; i < 6; i++) items.Add(Iron());
            store.GrantItems(owner, characterId, items);

            var (ok, _, _, _, ironConsumed) = store.CraftEssenceItem(owner, characterId, 10);
            Check(ok, "essence: operation runs with a target, a source and enough iron");
            Check(ironConsumed > 0, "essence: consumes the recovered iron cost");
            var remainingIron = store.GetItems(owner, characterId).Count(i => i.DefinitionIntegerId == 63);
            Check(remainingIron < 6, "essence: the iron stacks are actually deducted");

            // Affix merge: a rarity-10 target always succeeds; its affixes are the rarity-upgraded
            // union of the target's and the source's (up to capacity). Clear the previous craft's
            // Blacksmith slots so the merge uses its own target/source.
            store.SetItemSlots(owner, characterId, new Dictionary<Guid, ItemSlotTypes>
            {
                [target.Id] = ItemSlotTypes.Inventory,
                [source.Id] = ItemSlotTypes.Inventory,
            });
            var mergeTarget = LootTable.CreateItem(new LootDrop(3, 10, 20, false, 71));
            mergeTarget.Slot = ItemSlotTypes.Blacksmith_TargetItem;
            var mergeSource = LootTable.CreateItem(new LootDrop(3, 10, 20, false, 72));
            mergeSource.Slot = ItemSlotTypes.Blacksmith_SourceItem;
            var iron2 = new List<SerializedItem>();
            for (var i = 0; i < 60; i++) iron2.Add(Iron());
            store.GrantItems(owner, characterId, new List<SerializedItem> { mergeTarget, mergeSource }.Concat(iron2).ToList());
            var (mOk, mSuccess, mResult, _, _) = store.CraftEssenceItem(owner, characterId, 10);
            Check(mOk && mSuccess, "merge: a rarity-10 target succeeds deterministically");
            var originalIds = mergeTarget.Affixes.Select(a => a.DefinitionIntegerId).ToList();
            var sourceIds = mergeSource.Affixes.Select(a => a.DefinitionIntegerId).ToList();
            var resultIds = mResult.Affixes.Select(a => a.DefinitionIntegerId).ToList();
            var union = originalIds.Concat(sourceIds).Distinct().ToList();
            // Rarity 10 => affix capacity 2 + clamp(10/3,0,4) = 5; the merge is the union capped at it.
            var capacity = 2 + Math.Clamp((int)mResult.BaseRarity / 3, 0, 4);
            Check(originalIds.Count > 0 && originalIds.All(resultIds.Contains),
                "merge: the target keeps its original affixes");
            Check(resultIds.All(union.Contains), "merge: the result only contains target/source affixes");
            Check(resultIds.Count == Math.Min(capacity, union.Count),
                "merge: the merge fills the target's affix capacity");
            Check(sourceIds.Any(id => !originalIds.Contains(id) && resultIds.Contains(id)),
                "merge: at least one new source affix is adopted");

            // ProcessSuccessRate: overheat * 0.1 means overheat 0 can never succeed.
            store.SetItemSlots(owner, characterId, new Dictionary<Guid, ItemSlotTypes>
            {
                [mergeTarget.Id] = ItemSlotTypes.Inventory,
            });
            var zeroTarget = LootTable.CreateItem(new LootDrop(3, 10, 20, false, 73));
            zeroTarget.Slot = ItemSlotTypes.Blacksmith_TargetItem;
            var zeroSource = LootTable.CreateItem(new LootDrop(3, 10, 20, false, 74));
            zeroSource.Slot = ItemSlotTypes.Blacksmith_SourceItem;
            var iron3 = new List<SerializedItem>();
            for (var i = 0; i < 10; i++) iron3.Add(Iron());
            store.GrantItems(owner, characterId, new List<SerializedItem> { zeroTarget, zeroSource }.Concat(iron3).ToList());
            var (zOk, zSuccess, _, _, _) = store.CraftEssenceItem(owner, characterId, 0);
            Check(zOk && !zSuccess, "essence: overheat 0 cannot succeed (ProcessSuccessRate)");
            store.SetItemSlots(owner, characterId, new Dictionary<Guid, ItemSlotTypes>
            {
                [zeroTarget.Id] = ItemSlotTypes.Inventory,
                [zeroSource.Id] = ItemSlotTypes.Inventory,
            });

            // Relic craft: consume a source relic and bless the target's affixes.
            var relicTarget = LootTable.CreateItem(new LootDrop(3, 4, 10, false, 7));
            relicTarget.Slot = ItemSlotTypes.Blacksmith_TargetItem;
            var relicSource = LootTable.CreateItem(new LootDrop(3, 4, 10, false, 8));
            relicSource.Slot = ItemSlotTypes.Blacksmith_SourceItem;
            store.GrantItems(owner, characterId, new List<SerializedItem> { relicTarget, relicSource });
            var (rOk, _, rResult) = store.CraftRelicItem(owner, characterId);
            Check(rOk, "relic: succeeds with a target and a source relic");
            Check(store.GetItems(owner, characterId).All(i => i.Slot != ItemSlotTypes.Blacksmith_SourceItem),
                "relic: consumes the source relic");
            Check(rResult.Affixes.Count > 0 && rResult.Affixes.All(a =>
                    a.Attributes.Values[AttributeOrigin.Item].ContainsKey(99010)),
                "relic: blesses the target's affixes");

            // Sockets: add one (consumes Titansteel) then insert a gem.
            var socketTarget = LootTable.CreateItem(new LootDrop(3, 4, 10, false, 9));
            socketTarget.Slot = ItemSlotTypes.Blacksmith_TargetItem;
            var titan = Iron(); titan.DefinitionIntegerId = 592; titan.Name = "TitanSteel";
            store.GrantItems(owner, characterId, new List<SerializedItem> { socketTarget, titan });
            var (aOk, _, aResult) = store.ItemAddNewSocket(owner, characterId);
            Check(aOk, "socket: adds a socket when Titansteel is available");
            Check(aResult.Sockets.Count(s => s != null) == 1, "socket: the target gains one socket");

            var gem = LootTable.CreateItem(new LootDrop(3, 4, 10, false, 10));
            gem.Slot = ItemSlotTypes.Blacksmith_SourceItem;
            store.GrantItems(owner, characterId, new List<SerializedItem> { gem });
            var (sOk, _, sResult) = store.SocketItem(owner, characterId);
            Check(sOk, "socket: inserts the gem into the free socket");
            Check(sResult.Sockets[0].SocketedItem != null, "socket: the socket is filled");
        }
    }

    private static void EquippedWeaponFeedsWeaponDamage()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, registry, owner, characterId, _) = Create("r8", 100);
        using (store)
        {
            var weapon = LootTable.CreateItem(new LootDrop(12, 5, 10, false, 999));
            Check(LootTable.AttributeOf(weapon, 507) > 0, "weapon: main-hand carries Item_Weapon_Physical_Damage_Min");
            store.GrantItems(owner, characterId, new List<SerializedItem> { weapon });
            var version = registry.Advance(owner, characterId).Combat.Version;
            registry.ApplyCommand(owner, characterId, "r8-equip", version, new WebCommandRequest("equip", ItemId: weapon.Id));
            var instance = registry.GetOrCreate(owner, characterId);
            Console.WriteLine($"INFO weapon: bundle={instance.Damage.Total:F1} physical={instance.Damage.Physical:F1}");
            Check(instance.Damage.Total > 0, "weapon: equipped main-hand feeds the weapon-damage bundle");
            Check(instance.Damage.Total > instance.Damage.Physical,
                "weapon: the bundle carries elemental damage as well as physical");

            // Armour feeds the recovered Armor_Total (the plain Armor attribute on the item).
            var armorBefore = instance.Armor;
            var armorItem = LootTable.CreateItem(new LootDrop(3, 5, 10, false, 111));
            store.GrantItems(owner, characterId, new List<SerializedItem> { armorItem });
            var v2 = registry.Advance(owner, characterId).Combat.Version;
            registry.ApplyCommand(owner, characterId, "r8-armor", v2, new WebCommandRequest("equip", ItemId: armorItem.Id));
            var armorAfter = registry.GetOrCreate(owner, characterId).Armor;
            Console.WriteLine($"INFO armor: before={armorBefore:F1} after={armorAfter:F1}");
            Check(armorAfter > armorBefore, "equipment: armour raises the recovered Armor rating");
        }
    }

    private static void CombatVersionSurvivesRestart()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var dir = Path.Combine(Path.GetTempPath(), "nord-acc-r6-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var clock = new FakeClock();
        var owner = Guid.Empty;
        var characterId = Guid.Empty;
        long beforeRestart;
        using (var store = new GameStore(dir))
        {
            var registry = new CombatRegistry(store, clock);
            owner = store.GetOrCreateUser("device:r6").UserId;
            var data = Defaults.Create<SerializedCharacterData.SerializedData>();
            data.CombatStats = new SerializedCharacterData.SerializedCombatStats { Offense = 600, Defense = 50, Recovery = 5 };
            characterId = store.CreateCharacter(owner, new CreateCharacterRequest
            {
                DisplayName = "r6",
                CharacterClass = CharacterClass.Warrior,
                CharacterRace = CharacterRace.Human,
                CharacterGameMode = GameMode.Normal,
                Data = new SerializedCharacterData { Data = data },
            }).CharacterId;

            var version = 0L;
            for (var i = 0; i < 60; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                version = registry.Advance(owner, characterId).Combat.Version;
            }
            beforeRestart = version;
            Check(beforeRestart > 1, "R6 setup: combat version advanced past 1");
        }

        // Reopen the same directory: the in-memory instance is gone, so the version must be
        // restored from the store instead of resetting to 1.
        using (var store = new GameStore(dir))
        {
            var registry = new CombatRegistry(store, clock);
            var snapshot = registry.Advance(owner, characterId).Combat;
            Check(snapshot.Version >= beforeRestart, "R6 combat version does not regress across restart");
            var fresh = registry.ApplyCommand(owner, characterId, "r6-fresh", snapshot.Version, new WebCommandRequest("move", 3, 3));
            Check(fresh.Applied, "R6 fresh command after real reopen is accepted (not stale/future)");
        }
    }

    private static void FailedCommandRetryKeepsFailure()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, registry, owner, characterId, _) = Create("r5", 800);
        using (store)
        {
            var v0 = registry.Advance(owner, characterId).Combat.Version;
            var cast = registry.ApplyCommand(owner, characterId, "r5-cast", v0, new WebCommandRequest("skill", SkillId: 1));
            Check(cast.Applied, "R5 setup: teleport casts");
            var v1 = cast.State.Combat.Version;
            var fail = registry.ApplyCommand(owner, characterId, "r5-fail", v1, new WebCommandRequest("skill", SkillId: 1));
            Check(!fail.Applied && fail.Reason == "cooldown", "R5 setup: second cast fails on cooldown");

            var retry = registry.ApplyCommand(owner, characterId, "r5-fail", v1, new WebCommandRequest("skill", SkillId: 1));
            Check(!retry.Applied && retry.Reason == "cooldown", "R5 failed-command retry keeps the original failure");
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan delta) => now += delta;
    }
}
