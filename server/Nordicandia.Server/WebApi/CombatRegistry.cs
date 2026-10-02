using Nordicandia.Server.State;
using Nordicandia.Simulation;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// Owns one authoritative <see cref="CombatInstance"/> per character for the web slice.
/// The instance is seeded from the persisted snapshot, advanced lazily by elapsed time on
/// each request (no background timer needed), and flushed back through
/// <see cref="GameStore.SaveRealtimeProgress"/> so experience survives refresh/restart.
///
/// Command idempotency lives here: each processed command id maps to the snapshot that was
/// returned, so a retried request replays the original result instead of double-applying.
/// </summary>
public sealed class CombatRegistry
{
    private static readonly Lazy<CombatRegistry> Default = new(() => new CombatRegistry(GameStore.Instance));
    public static CombatRegistry Instance => Default.Value;

    private const int MaxRememberedCommands = 64;
    private readonly GameStore store;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Entry> entries = new();

    public CombatRegistry(GameStore store, TimeProvider clock = null)
    {
        this.store = store;
        this.clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private sealed class Entry
    {
        public CombatInstance Instance = null!;
        public DateTime LastAdvanceUtc;
        public double FlushedExperience;
        public int FlushedSilver;
        public int FlushedOpals;
        public int FlushedKills;
        public readonly Dictionary<string, CombatSnapshot> ProcessedCommands = new();
        public readonly Queue<string> CommandOrder = new();
    }

    /// <summary>Get (or lazily create) the character's instance, seeded from persisted state.</summary>
    public CombatInstance GetOrCreate(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            if (entries.TryGetValue(characterId, out var existing)) return existing.Instance;
            var persisted = store.ProjectWebSnapshot(owner, characterId);
            var stats = CombatantStats.FromRealtime(persisted.Offense, persisted.Defense, persisted.Recovery, (int)persisted.Level);
            // Stable seed from the character id keeps fixed scenarios reproducible.
            var seed = (ulong)characterId.GetHashCode() << 32 | (uint)characterId.GetHashCode();
            var instance = new CombatInstance(stats, persisted.Experience, persisted.Silver, persisted.Opals,
                (int)persisted.MonsterKills, seed);
            var entry = new Entry
            {
                Instance = instance,
                LastAdvanceUtc = Now,
                FlushedExperience = persisted.Experience,
                FlushedSilver = persisted.Silver,
                FlushedOpals = persisted.Opals,
                FlushedKills = (int)persisted.MonsterKills,
            };
            entries[characterId] = entry;
            return instance;
        }
    }

    /// <summary>Advance the instance by the real elapsed time since the last request, then persist changes.</summary>
    public CombatSnapshot Advance(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            var now = Now;
            var delta = (now - entry.LastAdvanceUtc).TotalSeconds;
            entry.LastAdvanceUtc = now;
            entry.Instance.Advance(delta);
            FlushLocked(owner, characterId, entry);
            return entry.Instance.Snapshot();
        }
    }

    /// <summary>Apply a command exactly once. Returns the snapshot to send back.</summary>
    public (bool Applied, string Reason, CombatSnapshot Snapshot) ApplyCommand(
        Guid owner, Guid characterId, string commandId, long expectedVersion, WebCommandRequest command)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            if (!string.IsNullOrEmpty(commandId) && entry.ProcessedCommands.TryGetValue(commandId, out var previous))
                return (true, "duplicate", previous);
            if (expectedVersion > entry.Instance.Version)
                return (false, "future_version", entry.Instance.Snapshot());

            AdvanceLocked(entry);

            var reason = "ok";
            var applied = true;
            switch (command?.Type)
            {
                case "move":
                    entry.Instance.MoveTo(command.X, command.Z);
                    break;
                case "skill":
                    var outcome = entry.Instance.UseSkill();
                    applied = outcome.Cast;
                    reason = outcome.Reason;
                    break;
                default:
                    applied = false;
                    reason = "unknown_type";
                    break;
            }

            // A short advance so the immediate result already reflects movement/attacks.
            entry.Instance.Advance(CombatInstance.StepSeconds);
            FlushLocked(owner, characterId, entry);
            var snapshot = entry.Instance.Snapshot();
            RememberCommand(entry, commandId, snapshot);
            return (applied, reason, snapshot);
        }
    }

    private Entry GetEntry(Guid owner, Guid characterId)
    {
        if (entries.TryGetValue(characterId, out var entry)) return entry;
        // Reuse GetOrCreate's seeding logic by calling it then looking up.
        GetOrCreate(owner, characterId);
        return entries[characterId];
    }

    private void AdvanceLocked(Entry entry)
    {
        var now = Now;
        entry.Instance.Advance((now - entry.LastAdvanceUtc).TotalSeconds);
        entry.LastAdvanceUtc = now;
    }

    private void FlushLocked(Guid owner, Guid characterId, Entry entry)
    {
        var instance = entry.Instance;
        var killsDelta = instance.Kills - entry.FlushedKills;
        if (instance.Experience == entry.FlushedExperience && instance.Silver == entry.FlushedSilver
            && instance.Opals == entry.FlushedOpals && killsDelta == 0)
            return;
        store.SaveRealtimeProgress(owner, characterId, instance.Experience, instance.Silver,
            instance.Opals, new GameStore.CombatSnapshot(instance.Offense, instance.Defense, instance.Recovery,
                Math.Max(0, killsDelta), 0));
        entry.FlushedExperience = instance.Experience;
        entry.FlushedSilver = instance.Silver;
        entry.FlushedOpals = instance.Opals;
        entry.FlushedKills = instance.Kills;
    }

    private static void RememberCommand(Entry entry, string commandId, CombatSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(commandId)) return;
        if (entry.ProcessedCommands.ContainsKey(commandId)) return;
        entry.ProcessedCommands[commandId] = snapshot;
        entry.CommandOrder.Enqueue(commandId);
        while (entry.CommandOrder.Count > MaxRememberedCommands)
        {
            var oldest = entry.CommandOrder.Dequeue();
            entry.ProcessedCommands.Remove(oldest);
        }
    }

    /// <summary>Test/ops hook: drop in-memory combat so the next request re-seeds from the store.</summary>
    public void Reset()
    {
        lock (gate) entries.Clear();
    }
}

public sealed record WebCommandRequest(string Type, double X = 0, double Z = 0);
