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

    private readonly GameStore store;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Entry> entries = new();

    /// <summary>Grace band above the character's level within which an item may still be equipped
    /// (the web loot drops near the player's level; the exact client rule is not recovered).</summary>
    private const int LevelRequirementGrace = 20;

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
        new("Bat", HpMult: 0.7, OffenseMult: 1.0, DefenseMult: 0.7, Speed: 3.2,
            Resistances: new ResistanceBundle(Fire: -0.2)),
        new("DemonOrc", HpMult: 1.3, OffenseMult: 1.2, DefenseMult: 1.0, Speed: 2.4,
            Damage: new DamageBundle(Fire: 0.5), Resistances: new ResistanceBundle(Fire: 0.3, Cold: -0.2)),
        new("Skeleton", HpMult: 1.0, OffenseMult: 0.9, DefenseMult: 1.1, Speed: 2.2,
            Damage: new DamageBundle(Cold: 0.4), Resistances: new ResistanceBundle(Poison: 0.5, Fire: -0.3)),
        new("BloodHound", HpMult: 0.8, OffenseMult: 1.4, DefenseMult: 0.6, Speed: 3.0,
            Damage: new DamageBundle(Poison: 0.5), Resistances: new ResistanceBundle(Poison: 0.4)),
        new("StoneGolem", HpMult: 1.8, OffenseMult: 0.8, DefenseMult: 1.6, Speed: 1.6,
            Resistances: new ResistanceBundle(Lightning: -0.2, Fire: 0.2)),
    };

    private sealed class Entry
    {
        public CombatInstance Instance = null!;
        public ClassPowers BasePowers = null!;
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
        public long FlushedVersion;
        public readonly List<LootDropView> RecentLoot = new();
    }

    public CombatInstance GetOrCreate(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            if (entries.TryGetValue(characterId, out var existing)) return existing.Instance;
            var persisted = store.ProjectWebSnapshot(owner, characterId);
            var items = store.GetItems(owner, characterId);
            var (equipOffense, equipDefense, equipRecovery) = LootTable.EquipmentBonus(items);
            var stats = CharacterRatings.Apply(
                CombatantStats.FromRealtime(
                    persisted.Offense + equipOffense,
                    persisted.Defense + equipDefense,
                    persisted.Recovery + equipRecovery,
                    (int)persisted.Level),
                store.GetAttributeMap(owner, characterId));
            var seed = (ulong)(uint)characterId.GetHashCode() << 32 | (uint)characterId.GetHashCode();
            var basePowers = PowerCatalog.ForClass(persisted.Class);
            var ranks = store.GetMasteryRanks(owner, characterId);
            // P1: seed the instance's version from the persisted counter so it never resets
            // to 1 across a restart (which would make the command log boundary reject fresh
            // commands from a client that already saw a higher version).
            var persistedVersion = store.GetCombatVersion(owner, characterId);
            var instance = new CombatInstance(stats, persisted.Experience, persisted.Silver, persisted.Opals,
                (int)persisted.MonsterKills, seed, monsterProfiles: MonsterProfiles,
                classPowers: EffectivePowers(basePowers, ranks), initialVersion: persistedVersion);
            entries[characterId] = new Entry
            {
                Instance = instance,
                BasePowers = basePowers,
                LastAdvanceUtc = Now,
                EquipOffense = equipOffense,
                EquipDefense = equipDefense,
                EquipRecovery = equipRecovery,
                FlushedExperience = persisted.Experience,
                FlushedSilver = persisted.Silver,
                FlushedOpals = persisted.Opals,
                FlushedKills = (int)persisted.MonsterKills,
                FlushedVersion = instance.Version,
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
            // A command without an id cannot be de-duplicated, so a retry would be
            // indistinguishable from a fresh command. Reject it rather than risk double-apply.
            if (string.IsNullOrEmpty(commandId))
                return (false, "missing_command_id", PeekState(entry));
            var (found, oldestBoundary) = store.LookupCommand(owner, characterId, commandId);
            if (found is not null)
                // R5: replay the original applied/reason, with the current snapshot.
                return (found.Applied, found.Reason, PeekState(entry));
            if (expectedVersion > 0 && expectedVersion < oldestBoundary)
                // R4/P2: outside the retained retry window — reject instead of re-executing.
                // The boundary is a server-assigned processed version, so it is not fooled by
                // a client reusing the same (low) expected version for evicted commands.
                // A zero expected version means the caller is not using optimistic concurrency.
                return (false, "stale_command", PeekState(entry));
            if (expectedVersion > entry.Instance.Version)
                return (false, "future_version", PeekState(entry));

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
                case "mastery":
                    (applied, reason) = AllocateMastery(owner, characterId, entry, command.MasteryId);
                    break;
                default:
                    applied = false;
                    reason = "unknown_type";
                    break;
            }

            // R2: simulation time comes only from the server clock (AdvanceLocked), never
            // from the act of sending a command. Commands only change intent.
            CollectLootLocked(owner, characterId, entry);
            FlushLocked(owner, characterId, entry);
            var state = TakeState(entry);
            store.AppendCommand(owner, characterId, new GameStore.CommandRecord
            {
                CommandId = commandId,
                ExpectedVersion = expectedVersion,
                Applied = applied,
                Reason = reason,
                ProcessedVersion = entry.Instance.Version,
            }, entry.Instance.Version);
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

    /// <summary>Mastery tree view for the class's three active skills.</summary>
    public IReadOnlyList<SkillMasteryView> MasteryView(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            var ranks = store.GetMasteryRanks(owner, characterId);
            return BuildMasteryView(entry, ranks);
        }
    }

    private (bool, string) AllocateMastery(Guid owner, Guid characterId, Entry entry, int masteryId)
    {
        var ranks = store.GetMasteryRanks(owner, characterId);
        MasteryProfile found = null;
        foreach (var skill in entry.BasePowers.Active.Take(3))
        {
            found = PowerCatalog.MasteriesFor(skill.Name).FirstOrDefault(m => m.IntegerId == masteryId);
            if (found is not null) break;
        }
        if (found is null) return (false, "unknown_mastery");
        var current = ranks.TryGetValue(masteryId, out var r) ? r : 0;
        if (current >= found.MaxPoints) return (false, "mastery_maxed");
        if (ranks.Values.Sum() >= MasteryBudget(entry)) return (false, "no_mastery_points");

        store.SetMasteryRank(owner, characterId, masteryId, current + 1);
        ranks[masteryId] = current + 1;
        entry.Instance.UpdatePowers(EffectivePowers(entry.BasePowers, ranks));
        return (true, "ok");
    }

    private List<SkillMasteryView> BuildMasteryView(Entry entry, Dictionary<int, int> ranks)
    {
        var rows = new List<SkillMasteryView>();
        var slot = 0;
        foreach (var skill in entry.BasePowers.Active.Take(3))
        {
            var masteries = PowerCatalog.MasteriesFor(skill.Name)
                .Select(m => new MasteryView(m.Name, m.IntegerId, ranks.TryGetValue(m.IntegerId, out var rank) ? rank : 0, m.MaxPoints, m.Specs))
                .ToList();
            rows.Add(new SkillMasteryView(skill.Name, slot++, masteries.Sum(m => m.Rank), masteries.Sum(m => m.MaxPoints), masteries));
        }
        return rows;
    }

    private static int MasteryBudget(Entry entry) => Math.Max(0, 3 + entry.Instance.PlayerLevel - 1);

    /// <summary>Apply allocated mastery specs to the class kit's base values.</summary>
    private static ClassPowers EffectivePowers(ClassPowers basePowers, Dictionary<int, int> ranks)
    {
        var active = new List<SkillProfile>();
        foreach (var skill in basePowers.Active)
        {
            var values = new Dictionary<string, double>(skill.Values);
            var modified = false;
            foreach (var mastery in PowerCatalog.MasteriesFor(skill.Name))
            {
                if (!ranks.TryGetValue(mastery.IntegerId, out var rank) || rank <= 0) continue;
                foreach (var spec in mastery.Specs)
                {
                    if (string.IsNullOrEmpty(spec.AttributeName)) continue;
                    var delta = spec.ContributionForRank(rank);
                    values[spec.AttributeName] = values.TryGetValue(spec.AttributeName, out var cur) ? cur + delta : delta;
                    modified = true;
                }
            }
            if (!modified)
            {
                active.Add(skill);
                continue;
            }
            active.Add(skill with
            {
                Multiplier = values.TryGetValue("Base_Power_Weapon_Damage_Multiplier", out var m) ? m : skill.Multiplier,
                Cooldown = Math.Max(1, values.TryGetValue("Base_Cooldown", out var cd) ? cd : skill.Cooldown),
                Radius = values.TryGetValue("Base_Power_Radius", out var r) ? r : skill.Radius,
                ManaCost = Math.Max(0, values.TryGetValue("Base_Mana_Cost", out var mc) ? mc : skill.ManaCost),
                Values = values,
                Confidence = "mastery-modified",
            });
        }
        return basePowers with { Active = active };
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
        // Level requirement: the item's level must not exceed the character's by more than a
        // grace band (the web loot drops near the player's level; the band is Provisional).
        var requiredLevel = (int)LootTable.AttributeOf(item, LootTable.AttrRequiredLevel);
        if (requiredLevel > entry.Instance.PlayerLevel + LevelRequirementGrace)
            return (false, "level_requirement");

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
        entry.Instance.UpdateStats(CharacterRatings.Apply(
            CombatantStats.FromRealtime(
                entry.Instance.Offense - entry.EquipOffense + offense,
                entry.Instance.Defense - entry.EquipDefense + defense,
                entry.Instance.Recovery - entry.EquipRecovery + recovery,
                entry.Instance.PlayerLevel),
            store.GetAttributeMap(owner, characterId)));
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
        var versionChanged = instance.Version != entry.FlushedVersion;
        if (instance.Experience == entry.FlushedExperience && instance.Silver == entry.FlushedSilver
            && instance.Opals == entry.FlushedOpals && killsDelta == 0 && !versionChanged)
            return;
        // R1: persist the *base* stats only. Flushing the equipment-inclusive totals and then
        // re-adding EquipmentBonus on restore double-counted the gear.
        // P1: also persist the combat version so it survives a restart even when nothing but
        // the version changed (e.g. a rejected move still bumps it).
        store.SaveRealtimeProgress(owner, characterId, instance.Experience, instance.Silver,
            instance.Opals, new GameStore.CombatSnapshot(
                instance.Offense - entry.EquipOffense,
                instance.Defense - entry.EquipDefense,
                instance.Recovery - entry.EquipRecovery,
                Math.Max(0, killsDelta), 0), instance.Version);
        entry.FlushedExperience = instance.Experience;
        entry.FlushedSilver = instance.Silver;
        entry.FlushedOpals = instance.Opals;
        entry.FlushedKills = instance.Kills;
        entry.FlushedVersion = instance.Version;
    }

    private static WebCombatState TakeState(Entry entry)
    {
        var loot = entry.RecentLoot.ToList();
        entry.RecentLoot.Clear();
        return new WebCombatState(entry.Instance.Snapshot(), loot);
    }

    /// <summary>Snapshot without consuming pending loot, for duplicate/rejected commands.</summary>
    private static WebCombatState PeekState(Entry entry)
        => new(entry.Instance.Snapshot(), Array.Empty<LootDropView>());

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

    /// <summary>Test/ops hook: drop in-memory combat so the next request re-seeds from the store.</summary>
    public void Reset()
    {
        lock (gate) entries.Clear();
    }
}

public sealed record WebCommandRequest(string Type, double X = 0, double Z = 0, Guid ItemId = default, int SkillId = 0, int MasteryId = 0);
