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
        FailedCommandRetryKeepsFailure();
        RegistrationPolicyIsShared();
        FreshCharacterClearsBoss();
        SkillSemanticsMatchClientClasses();
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
