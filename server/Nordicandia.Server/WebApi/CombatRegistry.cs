using Game;
using Nordicandia.Server.State;
using Nordicandia.Simulation;
using SharedNet.Api;
using SharedNet.Constants.Game;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// Owns one authoritative <see cref="CombatInstance"/> per character for the web slice.
/// The instance is seeded from the persisted snapshot, advanced lazily by elapsed time on
/// each request (no background timer needed), and flushed back through
/// <see cref="GameStore.SaveRealtimeProgress"/> so experience survives refresh/restart.
///
/// Loot is generated on the server when a monster dies, persisted through
/// <see cref="GameStore.GrantItems"/>, and handed to the caller once. Commands carry a
/// unique command id; a retry replays the original snapshot instead of double-applying.
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

    // Five normal enemy archetypes (provisional tuning, names match gamedata monsters so the
    // client can resolve their portrait icons). The boss is spawned separately.
    private static readonly MonsterProfile[] MonsterProfiles =
    {
        new("Bat", HpMult: 0.7, OffenseMult: 1.0, DefenseMult: 0.7, Speed: 3.2),
        new("DemonOrc", HpMult: 1.3, OffenseMult: 1.2, DefenseMult: 1.0, Speed: 2.4),
        new("Skeleton", HpMult: 1.0, OffenseMult: 0.9, DefenseMult: 1.1, Speed: 2.2),
        new("BloodHound", HpMult: 0.8, OffenseMult: 1.4, DefenseMult: 0.6, Speed: 3.0),
        new("StoneGolem", HpMult: 1.8, OffenseMult: 0.8, DefenseMult: 1.6, Speed: 1.6),
    };

    private sealed class Entry
    {
        public CombatInstance Instance = null!;
        public DateTime LastAdvanceUtc;
        // Equipment bonus currently folded into the instance, so a recompute can swap it
        // out without discarding the level-up growth that lives only in the instance.
        public double EquipOffense;
        public double EquipDefense;
        public double EquipRecovery;
        public double FlushedExperience;
        public int FlushedSilver;
        public int FlushedOpals;
        public int FlushedKills;
        public readonly List<LootDropView> RecentLoot = new();
        public readonly Dictionary<string, WebCombatState> ProcessedCommands = new();
        public readonly Queue<string> CommandOrder = new();
    }

    public CombatInstance GetOrCreate(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            if (entries.TryGetValue(characterId, out var existing)) return existing.Instance;
            var persisted = store.ProjectWebSnapshot(owner, characterId);
            var items = store.GetItems(owner, characterId);
            var (equipOffense, equipDefense, equipRecovery) = LootTable.EquipmentBonus(items);
            var stats = CombatantStats.FromRealtime(
                persisted.Offense + equipOffense,
                persisted.Defense + equipDefense,
                persisted.Recovery + equipRecovery,
                (int)persisted.Level);
            var seed = (ulong)(uint)characterId.GetHashCode() << 32 | (uint)characterId.GetHashCode();
            var instance = new CombatInstance(stats, persisted.Experience, persisted.Silver, persisted.Opals,
                (int)persisted.MonsterKills, seed, monsterProfiles: MonsterProfiles,
                classPowers: PowerCatalog.ForClass(persisted.Class));
            entries[characterId] = new Entry
            {
                Instance = instance,
                LastAdvanceUtc = Now,
                EquipOffense = equipOffense,
                EquipDefense = equipDefense,
                EquipRecovery = equipRecovery,
                FlushedExperience = persisted.Experience,
                FlushedSilver = persisted.Silver,
                FlushedOpals = persisted.Opals,
                FlushedKills = (int)persisted.MonsterKills,
            };
            return instance;
        }
    }

    public WebCombatState Advance(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            AdvanceLocked(entry);
            CollectLootLocked(owner, characterId, entry);
            FlushLocked(owner, characterId, entry);
            return TakeState(entry);
        }
    }

    public (bool Applied, string Reason, WebCombatState State) ApplyCommand(
        Guid owner, Guid characterId, string commandId, long expectedVersion, WebCommandRequest command)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            if (!string.IsNullOrEmpty(commandId) && entry.ProcessedCommands.TryGetValue(commandId, out var previous))
                return (true, "duplicate", previous);
            if (expectedVersion > entry.Instance.Version)
                return (false, "future_version", TakeState(entry));

            AdvanceLocked(entry);

            var reason = "ok";
            var applied = true;
            switch (command?.Type)
            {
                case "move":
                    entry.Instance.MoveTo(command.X, command.Z);
                    break;
                case "skill":
                    var outcome = entry.Instance.UseSkill(command.SkillId);
                    applied = outcome.Cast;
                    reason = outcome.Reason;
                    break;
                case "equip":
                    (applied, reason) = Equip(owner, characterId, entry, command.ItemId, equip: true);
                    break;
                case "unequip":
                    (applied, reason) = Equip(owner, characterId, entry, command.ItemId, equip: false);
                    break;
                default:
                    applied = false;
                    reason = "unknown_type";
                    break;
            }

            entry.Instance.Advance(CombatInstance.StepSeconds);
            CollectLootLocked(owner, characterId, entry);
            FlushLocked(owner, characterId, entry);
            var state = TakeState(entry);
            RememberCommand(entry, commandId, state);
            return (applied, reason, state);
        }
    }

    /// <summary>Read the character's items with computed equipment stats (does not advance combat).</summary>
    public WebInventory Inventory(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            var items = store.GetItems(owner, characterId);
            var rows = items.Where(i => i != null).Select(ToItemDetail).ToList();
            return new WebInventory(rows, entry.Instance.Offense, entry.Instance.Defense, entry.Instance.Recovery);
        }
    }

    private (bool, string) Equip(Guid owner, Guid characterId, Entry entry, Guid itemId, bool equip)
    {
        var items = store.GetItems(owner, characterId);
        var item = items.FirstOrDefault(i => i != null && i.Id == itemId);
        if (item is null) return (false, "no_item");

        if (!equip)
        {
            store.ApplyItemOperations(owner, characterId, new List<ItemOperationEntry>
            {
                new MoveItemOperationEntry { ItemId = itemId, ToSlot = ItemSlotTypes.Inventory, ToLocation = item.Location },
            });
            RecomputeStats(owner, characterId, entry);
            return (true, "ok");
        }

        var slot = LootTable.EquipSlotOf(item);
        if (slot is < 0 or > 13) return (false, "not_equippable");

        // Enforce one item per equip slot: anything already there returns to the bag.
        var operations = new List<ItemOperationEntry>();
        foreach (var conflict in items.Where(i => i != null && i.Id != itemId && LootTable.IsEquipped(i) && LootTable.EquipSlotOf(i) == slot))
            operations.Add(new MoveItemOperationEntry { ItemId = conflict.Id, ToSlot = ItemSlotTypes.Inventory, ToLocation = conflict.Location });
        operations.Add(new MoveItemOperationEntry { ItemId = itemId, ToSlot = (ItemSlotTypes)slot, ToLocation = item.Location });
        store.ApplyItemOperations(owner, characterId, operations);
        RecomputeStats(owner, characterId, entry);
        return (true, "ok");
    }

    private void RecomputeStats(Guid owner, Guid characterId, Entry entry)
    {
        var (offense, defense, recovery) = LootTable.EquipmentBonus(store.GetItems(owner, characterId));
        entry.Instance.UpdateStats(CombatantStats.FromRealtime(
            entry.Instance.Offense - entry.EquipOffense + offense,
            entry.Instance.Defense - entry.EquipDefense + defense,
            entry.Instance.Recovery - entry.EquipRecovery + recovery,
            entry.Instance.PlayerLevel));
        entry.EquipOffense = offense;
        entry.EquipDefense = defense;
        entry.EquipRecovery = recovery;
    }

    private Entry GetEntry(Guid owner, Guid characterId)
    {
        if (entries.TryGetValue(characterId, out var entry)) return entry;
        GetOrCreate(owner, characterId);
        return entries[characterId];
    }

    private void AdvanceLocked(Entry entry)
    {
        var now = Now;
        entry.Instance.Advance((now - entry.LastAdvanceUtc).TotalSeconds);
        entry.LastAdvanceUtc = now;
    }

    private void CollectLootLocked(Guid owner, Guid characterId, Entry entry)
    {
        var drops = entry.Instance.DrainDrops();
        if (drops.Count == 0) return;
        var items = drops.Select(LootTable.CreateItem).ToList();
        store.GrantItems(owner, characterId, items);
        foreach (var item in items)
        {
            var slot = LootTable.EquipSlotOf(item);
            entry.RecentLoot.Add(new LootDropView(item.Name, slot, (int)item.BaseRarity, 0,
                LootTable.AttributeOf(item, LootTable.AttrOffense),
                LootTable.AttributeOf(item, LootTable.AttrDefense),
                LootTable.AttributeOf(item, LootTable.AttrRecovery),
                AffixNames(item)));
        }
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

    private static WebCombatState TakeState(Entry entry)
    {
        var loot = entry.RecentLoot.ToList();
        entry.RecentLoot.Clear();
        return new WebCombatState(entry.Instance.Snapshot(), loot);
    }

    private static WebItemDetail ToItemDetail(SerializedItem item)
    {
        var equipped = LootTable.IsEquipped(item);
        return new WebItemDetail(
            item.Id,
            item.Name,
            (int)item.Slot,
            (int)item.BaseRarity,
            LootTable.EquipSlotOf(item),
            equipped,
            LootTable.AttributeOf(item, LootTable.AttrOffense),
            LootTable.AttributeOf(item, LootTable.AttrDefense),
            LootTable.AttributeOf(item, LootTable.AttrRecovery),
            AffixNames(item));
    }

    private static List<string> AffixNames(SerializedItem item)
        => item.Affixes == null
            ? new List<string>()
            : item.Affixes.Where(a => a != null).Select(a => LootTable.AffixName(a.DefinitionIntegerId)).ToList();

    private static void RememberCommand(Entry entry, string commandId, WebCombatState state)
    {
        if (string.IsNullOrEmpty(commandId)) return;
        if (entry.ProcessedCommands.ContainsKey(commandId)) return;
        entry.ProcessedCommands[commandId] = state;
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

public sealed record WebCommandRequest(string Type, double X = 0, double Z = 0, Guid ItemId = default, int SkillId = 0);
