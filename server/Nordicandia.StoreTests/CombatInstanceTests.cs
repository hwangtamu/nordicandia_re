using System.Text.Json;
using Game;
using Nordicandia.Server.State;
using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;
using SharedNet.Api;
using SharedNet.Constants.Game;

/// <summary>
/// M1 acceptance checks for the authoritative combat slice: deterministic fixed-step
/// simulation, kill rewards, command idempotency, and experience restoring after a
/// simulated server restart.
/// </summary>
static class CombatInstanceTests
{
    public static void Run()
    {
        FixedStepIsDeterministic();
        KillsAwardExperience();
        CommandsAreIdempotentAndPersist();
    }

    private static void FixedStepIsDeterministic()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var stats = CombatantStats.FromRealtime(offense: 120, defense: 30, recovery: 3, level: 8);
        var a = new CombatInstance(stats, 0, 0, 0, 0, seed: 42);
        var b = new CombatInstance(stats, 0, 0, 0, 0, seed: 42);
        for (var i = 0; i < 400; i++) { a.Advance(0.05); b.Advance(0.05); }
        var sa = JsonSerializer.Serialize(a.Snapshot());
        var sb = JsonSerializer.Serialize(b.Snapshot());
        Check(sa == sb, "m1 combat: fixed steps with the same seed are identical");
        Check(a.Snapshot().Kills > 0, "m1 combat: the fixed scenario actually kills monsters");
    }

    private static void KillsAwardExperience()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var stats = CombatantStats.FromRealtime(offense: 5000, defense: 500, recovery: 50, level: 20);
        var instance = new CombatInstance(stats, experience: 0, silver: 0, opals: 0, kills: 0, seed: 7);
        for (var i = 0; i < 400; i++) instance.Advance(0.05);
        var snapshot = instance.Snapshot();
        Check(snapshot.Kills >= 3, "m1 combat: repeated kills accumulate");
        Check(snapshot.Experience > 0, "m1 combat: kills grant experience");
        Check(instance.PlayerLevel >= stats.Level, "m1 combat: level never decreases");
    }

    private static void CommandsAreIdempotentAndPersist()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var dir = Path.Combine(Path.GetTempPath(), "nord-m1-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            using var store = new GameStore(dir);
            var clock = new FakeClock();
            var owner = store.GetOrCreateUser("device:m1-test").UserId;
            var data = Defaults.Create<SerializedCharacterData.SerializedData>();
            data.CombatStats = new SerializedCharacterData.SerializedCombatStats { Offense = 5000, Defense = 500, Recovery = 50 };
            var characterId = store.CreateCharacter(owner, new CreateCharacterRequest
            {
                DisplayName = "m1",
                CharacterClass = CharacterClass.Warrior,
                CharacterRace = CharacterRace.Human,
                CharacterGameMode = GameMode.Normal,
                Data = new SerializedCharacterData { Data = data },
            }).CharacterId;

            var registry = new CombatRegistry(store, clock);
            var initial = registry.Advance(owner, characterId);
            Check(initial.Combat.Version > 0, "m1 command: state seeds a versioned instance");

            var first = registry.ApplyCommand(owner, characterId, "cmd-skill-1", initial.Combat.Version,
                new WebCommandRequest("skill", SkillId: 1));
            var duplicate = registry.ApplyCommand(owner, characterId, "cmd-skill-1", initial.Combat.Version,
                new WebCommandRequest("skill", SkillId: 1));
            Check(first.Applied && first.Reason == "ok", "m1 command: skill applies");
            Check(duplicate.Reason == first.Reason && duplicate.Applied == first.Applied,
                "m1 command: retried command id replays the original result");
            Check(duplicate.State.Combat.Version == first.State.Combat.Version, "m1 command: duplicate returns same version");

            // Advance the fake clock so the lazy simulation actually runs, then poll.
            clock.Advance(TimeSpan.FromSeconds(25));
            var progressed = registry.Advance(owner, characterId);
            Check(progressed.Combat.Kills > 0, "m1 command: kills accrue over simulated time");
            Check(progressed.Combat.Experience > 0, "m1 command: experience accrues");

            var persisted = store.ProjectWebSnapshot(owner, characterId);
            Check(Math.Abs(persisted.Experience - progressed.Combat.Experience) < 0.001, "m1 command: experience flushed to store");

            // Simulate a server restart: drop in-memory combat and re-seed from the store.
            registry.Reset();
            var restored = registry.Advance(owner, characterId);
            Check(restored.Combat.Experience >= progressed.Combat.Experience, "m1 command: refresh restores experience");
            Check((int)persisted.MonsterKills == progressed.Combat.Kills, "m1 command: kill count persisted");

            var stale = registry.ApplyCommand(owner, characterId, "cmd-future", restored.Combat.Version + 1000,
                new WebCommandRequest("move", 1, 1));
            Check(!stale.Applied && stale.Reason == "future_version", "m1 command: future version rejected");
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan delta) => now += delta;
    }
}
