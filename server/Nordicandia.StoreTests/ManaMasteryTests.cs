using Game;
using Nordicandia.Server.State;
using Nordicandia.Server.WebApi;
using SharedNet.Api;
using SharedNet.Constants.Game;

/// <summary>
/// M3 checks: the mana gate consumes mana, freeze skills stun monsters, and mastery
/// allocation is persisted and changes the authoritative skill values.
/// </summary>
static class ManaMasteryTests
{
    public static void Run()
    {
        ManaConsumesAndRegenerates();
        FrostAppliesStun();
        MasteryChangesSkillAndRespectsBudget();
    }

    private static (GameStore store, CombatRegistry registry, Guid owner, Guid characterId, FakeClock clock)
        CreateMage(string tag, double offense = 800)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nord-m3-" + tag + "-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var store = new GameStore(dir);
        var clock = new FakeClock();
        var owner = store.GetOrCreateUser("device:" + tag).UserId;
        var data = Defaults.Create<SerializedCharacterData.SerializedData>();
        data.CombatStats = new SerializedCharacterData.SerializedCombatStats { Offense = offense, Defense = 200, Recovery = 20 };
        var characterId = store.CreateCharacter(owner, new CreateCharacterRequest
        {
            DisplayName = tag,
            CharacterClass = CharacterClass.Mage,
            CharacterRace = CharacterRace.HighElf,
            CharacterGameMode = GameMode.Normal,
            Data = new SerializedCharacterData { Data = data },
        }).CharacterId;
        return (store, new CombatRegistry(store, clock), owner, characterId, clock);
    }

    private static void ManaConsumesAndRegenerates()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, registry, owner, characterId, clock) = CreateMage("mana");
        using (store)
        {
            var start = registry.Advance(owner, characterId).Combat;
            Check(start.PlayerMana == start.PlayerMaxMana && start.PlayerMaxMana > 0, "m3 mana: starts full");

            // Mage skill 1 is Teleport (20 mana). Cast it and confirm the pool drops.
            var cast = registry.ApplyCommand(owner, characterId, "tp", start.Version, new WebCommandRequest("skill", SkillId: 1));
            Check(cast.Applied && cast.Reason == "ok", "m3 mana: teleport casts");
            var after = cast.State.Combat;
            Check(after.PlayerMana <= start.PlayerMana - 20 + 1.0, "m3 mana: cast spends mana");

            clock.Advance(TimeSpan.FromSeconds(20));
            var regen = registry.Advance(owner, characterId).Combat;
            Check(regen.PlayerMana > after.PlayerMana, "m3 mana: pool regenerates over time");
        }
    }

    private static void FrostAppliesStun()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, registry, owner, characterId, clock) = CreateMage("frost", offense: 15);
        using (store)
        {
            clock.Advance(TimeSpan.FromSeconds(4));
            var state = registry.Advance(owner, characterId).Combat;
            // Mage skill 2 is IceNova (freeze 2s, radius 6).
            var cast = registry.ApplyCommand(owner, characterId, "nova", state.Version, new WebCommandRequest("skill", SkillId: 2));
            if (cast.Applied)
            {
                var frozen = cast.State.Combat.Monsters.Any(m => m.Alive && m.StunTimer > 0);
                Check(frozen, "m3 freeze: IceNova stuns monsters");
            }
            else
            {
                // No target in range yet; advance and retry once.
                clock.Advance(TimeSpan.FromSeconds(3));
                var again = registry.Advance(owner, characterId).Combat;
                var retry = registry.ApplyCommand(owner, characterId, "nova2", again.Version, new WebCommandRequest("skill", SkillId: 2));
                Check(retry.Applied && retry.State.Combat.Monsters.Any(m => m.Alive && m.StunTimer > 0),
                    "m3 freeze: IceNova stuns monsters");
            }
        }
    }

    private static void MasteryChangesSkillAndRespectsBudget()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var (store, registry, owner, characterId, _) = CreateMage("mastery");
        using (store)
        {
            var view = registry.MasteryView(owner, characterId);
            var chain = view.First(v => v.SkillName == "ChainLightning");
            var chains = chain.Masteries.First(m => m.Name == "MasteryChainLightningChains");
            var baseline = registry.Advance(owner, characterId).Combat.Skills.First(s => s.Name == "ChainLightning");
            Check(baseline.Chains == 4, "m3 mastery: ChainLightning starts at 4 chains");

            var alloc = registry.ApplyCommand(owner, characterId, "m1", 0, new WebCommandRequest("mastery", MasteryId: chains.IntegerId));
            Check(alloc.Applied && alloc.Reason == "ok", "m3 mastery: allocation applies");
            var after = alloc.State.Combat.Skills.First(s => s.Name == "ChainLightning");
            Check(after.Chains == 5, "m3 mastery: mastery raises chains to 5");

            // Budget at level 1 is 3; spend the remaining two then confirm the gate.
            registry.ApplyCommand(owner, characterId, "m2", 0, new WebCommandRequest("mastery", MasteryId: chains.IntegerId));
            var third = registry.ApplyCommand(owner, characterId, "m3", 0, new WebCommandRequest("mastery", MasteryId: chains.IntegerId));
            Check(third.Applied, "m3 mastery: third point fits the budget");
            var fourth = registry.ApplyCommand(owner, characterId, "m4", 0, new WebCommandRequest("mastery", MasteryId: chains.IntegerId));
            Check(!fourth.Applied && fourth.Reason == "no_mastery_points", "m3 mastery: budget is enforced");
            Check(registry.MasteryView(owner, characterId).First(v => v.SkillName == "ChainLightning").Masteries
                .First(m => m.Name == "MasteryChainLightningChains").Rank == 3, "m3 mastery: rank persists");

            // Simulate a restart: ranks are re-read from the store and re-applied.
            registry.Reset();
            var restored = registry.Advance(owner, characterId).Combat.Skills.First(s => s.Name == "ChainLightning");
            Check(restored.Chains == 7, "m3 mastery: 4 base + 3 allocated chains after restart");
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan delta) => now += delta;
    }
}
