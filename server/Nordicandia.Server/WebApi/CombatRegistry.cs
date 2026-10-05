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

    private const int MaxQuantityFromMagicFindMultiplier = 5; // GameParameters.MaxQuantityFromMagicFindMultiplier

    /// <summary>Largest wall-clock gap the live simulation will advance in one step. A longer gap
    /// (tab closed, host asleep) is handled by the offline-reward flow instead of fast-forwarding
    /// combat, which would otherwise run the player through hours of un-attended damage.</summary>
    private const double MaxCatchUpSeconds = 5.0;

    public CombatRegistry(GameStore store, TimeProvider clock = null)
    {
        this.store = store;
        this.clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // D04: monster combat stats come from the recovered Game.Monster.Get* level curves
    // (MonsterScaling), not from hand-tuned multipliers. Archetypes therefore only carry what
    // the client itself varies: damage distribution, resistances, ranged/caster behaviour, brain
    // and rarity. Names match gamedata monsters so the client resolves the portrait icons.
    // W01: names are real gamedata monsters and the damage type comes from Monsters.json
    // (DamageType: 0 Physical / 1 Fire / 2 Cold / 3 Lightning / 4 Poison). Resistances stay as
    // Provisional game-balance adaptations (the client derives them from the attribute engine and
    // area affixes, not from the monster definition).
    private static readonly MonsterProfile[] MonsterProfiles =
    {
        new("Bat", Speed: 3.2, Damage: MonsterCatalog.DamageBundle("Bat"),
            Resistances: new ResistanceBundle(Fire: -0.2), Brain: "Standard"),
        new("DemonOrc", Speed: 2.4, Damage: MonsterCatalog.DamageBundle("DemonOrc"),
            Resistances: new ResistanceBundle(Fire: 0.3, Cold: -0.2), Brain: "Standard"),
        // Ranged (gamedata SkeletonArcher1): attacks from range and backs off when the player closes.
        new("SkeletonArcher1", Speed: 2.2, Damage: MonsterCatalog.DamageBundle("SkeletonArcher1"),
            Resistances: new ResistanceBundle(Poison: 0.5, Fire: -0.3),
            Ranged: true, AttackRange: 8, PreferredDistance: 4, Brain: "StandardFleeingWhenClose"),
        new("GrayWolf", Speed: 3.0, Damage: MonsterCatalog.DamageBundle("GrayWolf"),
            Resistances: new ResistanceBundle(Poison: 0.4), Brain: "Standard"),
        new("StoneGolem", Speed: 1.6, Damage: MonsterCatalog.DamageBundle("StoneGolem"),
            Resistances: new ResistanceBundle(Lightning: -0.2, Fire: 0.2), Brain: "Standard"),
        // Caster (gamedata CasterDemon1): long-range cold attacker; Champion (rarity 4).
        new("CasterDemon1", Speed: 2.0, Damage: MonsterCatalog.DamageBundle("CasterDemon1"),
            Resistances: new ResistanceBundle(Fire: 0.4, Cold: 0.4),
            Ranged: true, AttackRange: 9, PreferredDistance: 5, Brain: "StandardCurseSlow",
            Champion: true, Rarity: 4),
    };

    /// <summary>Scaled archetypes for a Niflheim run. Provisional: the client's
    /// NiflheimPortalGameMode spawns "packs" sized by the portal affix (NumMonsterPacks); the
    /// exact per-pack scaling was not decoded, so the same archetypes raise ExpMult (the client's
    /// global monster difficulty multiplier).</summary>
    private static MonsterProfile[] NiflheimProfiles(int tier) =>
        ProfilesForWorldTier(tier).Select(p => p with { ExpMult = 1.8 }).ToArray();

    /// <summary>W03: resolve the world's BossMonsterId through the extracted monster roster.
    /// Boss rarity and exact resistance/phase tuning remain separate fidelity work.</summary>
    private static MonsterProfile? BossProfileForWorldTier(int tier)
    {
        var bossName = WorldCatalog.ForTier(tier)?.BossName;
        var monster = MonsterCatalog.ByName(bossName);
        if (monster is not { } definition) return null;
        return new MonsterProfile(definition.Name, Speed: 2.0,
            Damage: MonsterCatalog.DamageBundle(definition.Name),
            Ranged: definition.Ranged, Brain: definition.BrainName, Rarity: 6);
    }

    /// <summary>W01/W04: the spawn pool for a world tier, built from that world's
    /// MonsterTypeSpawnWeights and the real monster roster (name, damage type, ranged, brain).
    /// Falls back to the small default archetype set when the world or its types are unknown.</summary>
    private static MonsterProfile[] ProfilesForWorldTier(int tier)
    {
        var world = WorldCatalog.ForTier(tier);
        if (world is null) return MonsterProfiles;
        var pool = new List<MonsterProfile>();
        foreach (var (typeName, typeWeight) in world.Value.SpawnWeights.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (typeWeight <= 0) continue;
            var monsters = MonsterCatalog.ByType(typeName).ToList();
            if (monsters.Count == 0) continue;
            // MonsterTypeSpawnWeights define the total probability mass of the type. Until
            // per-monster within-type weights are recovered, divide that mass evenly among
            // its spawnable members rather than accidentally biasing populous types.
            var memberWeight = (double)typeWeight / monsters.Count;
            foreach (var monster in monsters)
                pool.Add(new MonsterProfile(monster.Name, Speed: 2.4,
                    Damage: MonsterCatalog.DamageBundle(monster.Name),
                    Ranged: monster.Ranged, Brain: monster.BrainName, SpawnWeight: memberWeight,
                    AvailableRarities: monster.AvailableRarities, SpawnGroup: typeName));
        }
        return pool.Count >= 3 ? pool.ToArray() : MonsterProfiles;
    }

    /// <summary>The reached world tier from the character's attribute map (W07: World_Tier_Unlocked
    /// id 9, falling back to World_Tier id 8).</summary>
    private static int WorldTier(IReadOnlyDictionary<int, double> map)
    {
        var tier = (int)map.GetValueOrDefault(9);
        if (tier <= 0) tier = (int)map.GetValueOrDefault(8);
        return Math.Max(1, tier);
    }

    private static int SelectedWorldTier(IReadOnlyDictionary<int, double> map)
    {
        var unlocked = WorldTier(map);
        var selected = (int)map.GetValueOrDefault(8);
        return selected <= 0 || selected > unlocked ? unlocked : selected;
    }

    /// <summary>W04: the theme kit for a world tier. The client ThemeId (1/2/3) selects the
    /// shipped dungeon theme kits; the exact ThemeId -> kit mapping is Inferred.</summary>
    private static string ThemeForTier(int tier)
    {
        var world = WorldCatalog.ForTier(tier);
        return (world?.ThemeId ?? 1) switch
        {
            2 => "dungeon_sand",
            3 => "dungeon_undead",
            1 => "dungeon_grass",
            _ => "dungeon_default",
        };
    }

    private static WebMapLayout? BuildMap(Entry entry)
    {
        var layout = entry.Instance.Layout;
        if (layout is null) return null;
        var anchors = layout.SpawnAnchors
            .Select(a =>
            {
                var (x, z) = layout.World(a.X, a.Z, CombatInstance.ArenaHalf);
                return new WebMapAnchor(x, z);
            }).ToList();
        return new WebMapLayout(layout.Width, layout.Height, layout.Theme, layout.ToRows(), anchors);
    }

    private sealed class Entry
    {
        public CombatInstance Instance = null!;
        public ClassPowerPool BasePowers = null!;
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
        // Carries the fractional part of the recovered item-quantity roll between drops.
        public double ItemQuantityRemainder;
        // Wall-clock seconds the player was away when this entry was created; claimed once.
        public long PendingOfflineSeconds;
        // Niflheim portal run: while true the instance uses the scaled monster profiles.
        public bool Niflheim;
        public bool Town;
        public int NiflheimPacks;
        public int NiflheimRunsCleared;
        public int LastDungeonsCleared;
        // Native DungeonRunStarted/Completed carry tier, waypoint and duration. The web maps
        // a cleared boss dungeon onto those persisted checkpoint fields.
        public int CurrentWorldWaypoint = 1;
        public int LastWorldProgressDungeonsCleared;
        public double WorldRunElapsedSeconds;
        public readonly List<LootDropView> RecentLoot = new();
        public readonly List<SerializedItem> PendingLootItems = new();
        public bool PendingNiflheimReturn;
        public bool PendingNiflheimCompletion;
    }

    public CombatInstance GetOrCreate(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            if (entries.TryGetValue(characterId, out var existing)) return existing.Instance;
            var persisted = store.ProjectWebSnapshot(owner, characterId);
            var items = store.GetItems(owner, characterId);
            var (equipOffense, equipDefense, equipRecovery) = LootTable.EquipmentBonus(items);
            var seed = (ulong)(uint)characterId.GetHashCode() << 32 | (uint)characterId.GetHashCode();
            var loadout = store.GetLoadout(owner, characterId);
            var powerRanks = store.GetPowerRanks(owner, characterId);
            var basePowers = PowerCatalog.BuildPool(persisted.Class, loadout.Active, loadout.Passive, powerRanks);
            var ranks = store.GetMasteryRanks(owner, characterId);
            var niflheim = store.IsNiflheimActive(owner, characterId);
            var town = store.IsTownActive(owner, characterId);
            var attributeMap = CharacterAttributeMap(owner, characterId, basePowers);
            var stats = CharacterRatings.Apply(
                CombatantStats.FromRealtime(
                    persisted.Offense + equipOffense,
                    persisted.Defense + equipDefense,
                    persisted.Recovery + equipRecovery,
                    (int)persisted.Level) with { ProjectileAutoAttack = UsesRangedAutoAttack(items) },
                attributeMap);
            // P1: seed the instance's version from the persisted counter so it never resets
            // to 1 across a restart (which would make the command log boundary reject fresh
            // commands from a client that already saw a higher version).
            var persistedVersion = store.GetCombatVersion(owner, characterId);
            var worldTier = SelectedWorldTier(attributeMap);
            var savedWorld = store.GetWorldProgress(owner, characterId);
            var currentWaypoint = savedWorld.Waypoints.GetValueOrDefault(worldTier).CurrentWaypoint;
            if (currentWaypoint <= 0) currentWaypoint = 1;
            var layout = WorldLayout(characterId, worldTier);
            var instance = new CombatInstance(stats, persisted.Experience, persisted.Silver, persisted.Opals,
                (int)persisted.MonsterKills, seed,
                monsterProfiles: niflheim ? NiflheimProfiles(worldTier) : ProfilesForWorldTier(worldTier),
                classPowers: EffectivePowers(basePowers, ranks), initialVersion: persistedVersion, layout: layout,
                classId: (int)persisted.Class, bossProfile: BossProfileForWorldTier(worldTier),
                currentWorldTier: worldTier);
            instance.LootTableName = niflheim ? "Niflheim" : "Default";
            var savedRun = niflheim ? store.GetNiflheimRun(owner, characterId) : null;
            if (niflheim)
            {
                // Old saves have only the active flag; resume them at a minimum two-pack run.
                // New saves restore the exact current pack, RNG and monster HP below.
                if (savedRun is not null) instance.RestoreNiflheimRun(savedRun);
                else instance.SetWorld(NiflheimProfiles(worldTier), packs: 2, layout: layout,
                    worldBossProfile: BossProfileForWorldTier(worldTier), currentWorldTier: worldTier,
                    nativeNiflheimRun: true);
            }
            if (town) instance.EnterTown();
            entries[characterId] = new Entry
            {
                Instance = instance,
                BasePowers = basePowers,
                LastAdvanceUtc = Now,
                EquipOffense = equipOffense,
                EquipDefense = equipDefense,
                EquipRecovery = equipRecovery,
                PendingOfflineSeconds = store.GetOfflineWindow(owner, characterId),
                Niflheim = niflheim,
                NiflheimPacks = savedRun?.TotalPacks ?? (niflheim ? 2 : 0),
                NiflheimRunsCleared = store.GetNiflheimRunsCleared(owner, characterId),
                Town = town,
                LastDungeonsCleared = instance.DungeonsCleared,
                CurrentWorldWaypoint = currentWaypoint,
                LastWorldProgressDungeonsCleared = instance.DungeonsCleared,
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
            if (!entry.Niflheim) RecordWorldProgressLocked(owner, characterId, entry);
            CollectLootLocked(owner, characterId, entry);
            ApplyNiflheimCompletionLocked(owner, characterId, entry);
            FlushLocked(owner, characterId, entry);
            return TakeState(entry);
        }
    }

    // ----- offline rewards (M3) -----

    /// <summary>Offline reward preview: the away window, the capped eligible seconds and the
    /// experience a claim would grant. Does not grant anything.</summary>
    public OfflineView Offline(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            var (efficiency, tier) = OfflineRate(owner, characterId, entry);
            var (experience, seconds) = OfflineRewards.Compute(entry.Instance.PlayerLevel, entry.PendingOfflineSeconds, efficiency, tier);
            return new OfflineView(entry.PendingOfflineSeconds, seconds, experience, experience > 0);
        }
    }

    /// <summary>Grants the pending offline experience once and clears the window. A second call
    /// returns nothing (idempotent).</summary>
    public OfflineView ClaimOffline(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            var (efficiency, tier) = OfflineRate(owner, characterId, entry);
            var (experience, seconds) = OfflineRewards.Compute(entry.Instance.PlayerLevel, entry.PendingOfflineSeconds, efficiency, tier);
            if (experience > 0)
            {
                entry.Instance.GrantExperience(experience);
                FlushLocked(owner, characterId, entry);
            }
            entry.PendingOfflineSeconds = 0;
            return new OfflineView(0, seconds, experience, false);
        }
    }

    /// <summary>Recovered offline inputs: Offline_Battle_Efficiency_Multiplier (attribute 565,
    /// which resolves to Base_Stamina_Multiplier) and the reached world tier.
    ///
    /// W07: the client's Character.GetSecondHighestReachedWorldCheckpoint reads the
    /// <c>World_Tier_Unlocked</c> attribute (id 9, offset 0x70) and steps back one checkpoint within
    /// the tier. The web slice does not yet track per-tier checkpoint progress, so the checkpoint
    /// step (which only changes the tier when a tier was just unlocked) is not applied; the
    /// World_Tier attribute (id 8) is kept as a fallback when 9 is unset.</summary>
    private (double Efficiency, int Tier) OfflineRate(Guid owner, Guid characterId, Entry entry)
    {
        var map = CharacterAttributeMap(owner, characterId, entry.BasePowers);
        var eval = CharacterAttributeEngine.Instance.Evaluate(map);
        return (eval.Resolve("Offline_Battle_Efficiency_Multiplier"), WorldTier(map));
    }

    // ----- Aesir blessings (M3) -----

    /// <summary>Active blessings and the offering options. Buying is server-validated; the
    /// effect flows through the recovered attribute engine on the next stat recompute.</summary>
    public BlessingsView ActiveBlessings(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            var active = store.GetActiveBlessingSeconds(owner, characterId);
            var rows = Blessings.Types.Select(type => new BlessingState(
                type, Blessings.TypeName(type), active.ContainsKey(type),
                active.TryGetValue(type, out var seconds) ? seconds : 0, Blessings.Effect(type))).ToList();
            var sizes = Blessings.Sizes
                .Select(size => new OfferingSize(size, Blessings.SizeName(size), Blessings.OpalCost(size),
                    (long)Blessings.Duration(size).TotalSeconds)).ToList();
            var opals = store.ProjectWebSnapshot(owner, characterId).Opals;
            return new BlessingsView(rows, sizes, opals);
        }
    }

    /// <summary>Buys an offering and immediately applies its blessing (idempotent per call: the
    /// opal cost is deducted once, and the expiry is extended).</summary>
    public (bool Applied, string Reason, BlessingsView View) Offer(Guid owner, Guid characterId, int type, int size)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            if (!Blessings.Types.Contains(type)) return (false, "unknown_type", ActiveBlessings(owner, characterId));
            if (size is < 1 or > 4) return (false, "unknown_size", ActiveBlessings(owner, characterId));
            var cost = Blessings.OpalCost(size);
            if (store.ProjectWebSnapshot(owner, characterId).Opals < cost)
                return (false, "not_enough_opals", ActiveBlessings(owner, characterId));
            store.MakeOffering(owner, characterId, type, size, cost);
            // Rebuild the transient attribute map so the blessing affects combat ratings now.
            RecomputeStats(owner, characterId, entry);
            return (true, "ok", ActiveBlessings(owner, characterId));
        }
    }

    // ----- Niflheim portal (M3) -----

    /// <summary>Whether the character holds a portal and/or is inside a run.</summary>
    public WebWorldProgress WorldProgress(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            var saved = store.GetWorldProgress(owner, characterId);
            var worlds = WorldCatalog.Entries
                .Where(w => w.Tier is > 0)
                .GroupBy(w => w.Tier!.Value)
                .Select(g => g.First())
                .OrderBy(w => w.Tier)
                .Select(w =>
                {
                    var tier = w.Tier!.Value;
                    var waypoint = saved.Waypoints.GetValueOrDefault(tier);
                    var maxWaypoint = Math.Max(1, waypoint.MaxWaypoint);
                    var currentWaypoint = Math.Clamp(waypoint.CurrentWaypoint <= 0 ? 1 : waypoint.CurrentWaypoint,
                        1, maxWaypoint);
                    return new WebWorldOption(tier, w.IntegerId, w.Name, w.BossName ?? "",
                        tier <= saved.UnlockedTier, tier == saved.CurrentTier,
                        currentWaypoint, maxWaypoint);
                }).ToList();
            return new WebWorldProgress(saved.CurrentTier, saved.UnlockedTier, worlds);
        }
    }

    public (bool Applied, string Reason, WebCombatState State) SelectWorld(Guid owner, Guid characterId,
        int tier, int waypoint = 0)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            if (entry.Niflheim) return (false, "in_niflheim", PeekState(entry));
            var saved = store.GetWorldProgress(owner, characterId);
            if (tier < 1 || tier > saved.UnlockedTier)
                return (false, "world_locked", PeekState(entry));
            if (WorldCatalog.ForTier(tier) is null)
                return (false, "unknown_world", PeekState(entry));

            var savedWaypoint = saved.Waypoints.GetValueOrDefault(tier);
            var maxWaypoint = Math.Max(1, savedWaypoint.MaxWaypoint);
            var selectedWaypoint = waypoint > 0 ? waypoint
                : savedWaypoint.CurrentWaypoint > 0 ? savedWaypoint.CurrentWaypoint : 1;
            if (selectedWaypoint < 1 || selectedWaypoint > maxWaypoint)
                return (false, "checkpoint_locked", PeekState(entry));
            SettleBeforeTravelLocked(owner, characterId, entry);
            store.SetCurrentWorldTier(owner, characterId, tier);
            store.SetCurrentWorldWaypoint(owner, characterId, tier, selectedWaypoint);
            store.SetTownActive(owner, characterId, false);
            entry.Town = false;
            var layout = WorldLayout(characterId, tier);
            entry.Instance.SetWorld(ProfilesForWorldTier(tier), layout: layout,
                worldBossProfile: BossProfileForWorldTier(tier), currentWorldTier: tier);
            entry.LastDungeonsCleared = entry.Instance.DungeonsCleared;
            entry.LastWorldProgressDungeonsCleared = entry.Instance.DungeonsCleared;
            entry.CurrentWorldWaypoint = selectedWaypoint;
            entry.WorldRunElapsedSeconds = 0;
            FlushLocked(owner, characterId, entry);
            return (true, "ok", TakeState(entry));
        }
    }

    public WebTownState TownState(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            return new WebTownState(entry.Town, entry.Instance.CurrentWorldTier, entry.CurrentWorldWaypoint);
        }
    }

    public WebCombatState EnterTown(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            SettleBeforeTravelLocked(owner, characterId, entry);
            if (entry.Niflheim) LeaveNiflheimLocked(owner, characterId, entry);
            if (!entry.Town)
            {
                entry.Niflheim = false;
                entry.Town = true;
                store.SetNiflheimActive(owner, characterId, false);
                store.SetTownActive(owner, characterId, true);
                entry.Instance.EnterTown();
                entry.WorldRunElapsedSeconds = 0;
            }
            FlushLocked(owner, characterId, entry);
            return TakeState(entry);
        }
    }

    public WebCombatState LeaveTown(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            if (!entry.Town) return TakeState(entry);
            var saved = store.GetWorldProgress(owner, characterId);
            var waypoint = saved.Waypoints.GetValueOrDefault(saved.CurrentTier);
            var selectedWaypoint = waypoint.CurrentWaypoint > 0 ? waypoint.CurrentWaypoint : 1;
            var (_, _, state) = SelectWorld(owner, characterId, saved.CurrentTier, selectedWaypoint);
            return state;
        }
    }

    public WebPortalState Portal(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            var portal = store.GetItems(owner, characterId).FirstOrDefault(i => i != null && i.DefinitionIntegerId == PortalDefinitionIntegerId);
            return new WebPortalState(entry.Niflheim, portal is not null, portal?.Id ?? Guid.Empty,
                entry.NiflheimRunsCleared, entry.NiflheimPacks, entry.Instance.NiflheimExitReady,
                entry.Instance.NiflheimChestSize, entry.Instance.NiflheimChestOpened);
        }
    }

    /// <summary>Consumes one Niflheim portal and switches the instance to the portal world. If a
    /// run is already active nothing is consumed (idempotent retry).</summary>
    public (bool Applied, string Reason, WebCombatState State) EnterPortal(Guid owner, Guid characterId, Guid itemId)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            if (entry.Niflheim) return (false, "already_in_niflheim", PeekState(entry));
            var portal = store.GetItems(owner, characterId).FirstOrDefault(i => i != null && i.Id == itemId && i.DefinitionIntegerId == PortalDefinitionIntegerId);
            if (portal is null) return (false, "no_portal_item", PeekState(entry));
            SettleBeforeTravelLocked(owner, characterId, entry);
            var packs = (int)LootTable.AttributeOf(portal, MerchantCatalog.NumMonsterPacksAttributeId);
            var extraFinalPackMonsters = (int)LootTable.AttributeOf(portal, 2717); // Area_Contains_More_Bosses
            var wasTown = entry.Town;
            var portalTier = SelectedWorldTier(CharacterAttributeMap(owner, characterId, entry.BasePowers));
            entry.Instance.SetWorld(NiflheimProfiles(portalTier), Math.Max(2, packs),
                layout: WorldLayout(characterId, portalTier),
                worldBossProfile: BossProfileForWorldTier(portalTier), currentWorldTier: portalTier,
                nativeNiflheimRun: true, extraFinalPackMonsters: extraFinalPackMonsters);
            if (!store.TryEnterNiflheim(owner, characterId, itemId, entry.Instance.CaptureNiflheimRun()!))
            {
                entry.Instance.SetWorld(ProfilesForWorldTier(portalTier), layout: WorldLayout(characterId, portalTier),
                    worldBossProfile: BossProfileForWorldTier(portalTier), currentWorldTier: portalTier);
                if (wasTown) entry.Instance.EnterTown();
                return (false, "consume_failed", PeekState(entry));
            }
            entry.Town = false;
            entry.Niflheim = true;
            entry.NiflheimPacks = Math.Max(2, packs);
            entry.WorldRunElapsedSeconds = 0;
            entry.Instance.LootTableName = "Niflheim";
            entry.LastDungeonsCleared = entry.Instance.DungeonsCleared;
            FlushLocked(owner, characterId, entry);
            return (true, "ok", TakeState(entry));
        }
    }

    /// <summary>Leaves a Niflheim run for the safe town hub (no item is consumed).</summary>
    public WebCombatState ReturnPortal(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            if (!entry.Niflheim) return TakeState(entry);
            SettleBeforeTravelLocked(owner, characterId, entry);
            if (entry.Niflheim) LeaveNiflheimLocked(owner, characterId, entry);
            FlushLocked(owner, characterId, entry);
            return TakeState(entry);
        }
    }

    /// <summary>Open the native exit chest once, keeping its drops and opened state in the same
    /// durable progress transaction.</summary>
    public (bool Applied, string Reason, WebCombatState State) OpenNiflheimChest(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            if (!entry.Niflheim || !entry.Instance.NiflheimExitReady)
                return (false, "exit_not_ready", PeekState(entry));
            if (entry.Instance.NiflheimChestSize == 0)
                return (false, "no_chest", PeekState(entry));
            if (entry.Instance.NiflheimChestOpened)
                return (false, "already_opened", PeekState(entry));
            entry.Instance.OpenNiflheimChest();
            CollectLootLocked(owner, characterId, entry);
            FlushLocked(owner, characterId, entry);
            return (true, "ok", TakeState(entry));
        }
    }

    private void ApplyNiflheimCompletionLocked(Guid owner, Guid characterId, Entry entry)
    {
        if (!entry.Niflheim)
        {
            entry.LastDungeonsCleared = entry.Instance.DungeonsCleared;
            return;
        }
        if (entry.Instance.DungeonsCleared > entry.LastDungeonsCleared)
        {
            LeaveNiflheimLocked(owner, characterId, entry, completed: true);
        }
    }

    private void LeaveNiflheimLocked(Guid owner, Guid characterId, Entry entry, bool completed = false)
    {
        completed |= entry.Instance.CompleteNiflheimExit();
        entry.PendingNiflheimReturn = true;
        entry.PendingNiflheimCompletion = completed;
        if (completed) entry.NiflheimRunsCleared++;
        entry.Niflheim = false;
        entry.NiflheimPacks = 0;
        entry.Town = true;
        entry.LastDungeonsCleared = entry.Instance.DungeonsCleared;
        entry.LastWorldProgressDungeonsCleared = entry.Instance.DungeonsCleared;
        entry.WorldRunElapsedSeconds = 0;
        // The destination is town. LeaveTown later restores the selected tier/checkpoint.
        entry.Instance.EnterTown();
        entry.Instance.LootTableName = "Default";
        entry.LastDungeonsCleared = entry.Instance.DungeonsCleared;
    }

    private const int PortalDefinitionIntegerId = 159; // Items.json: NiflheimPortal

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

            // Record clear events produced either by the elapsed simulation above or this command.
            if (!entry.Niflheim) RecordWorldProgressLocked(owner, characterId, entry);
            // R2: simulation time comes only from the server clock (AdvanceLocked), never
            // from the act of sending a command. Commands only change intent.
            CollectLootLocked(owner, characterId, entry);
            ApplyNiflheimCompletionLocked(owner, characterId, entry);
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
            var rows = items.Where(i => i != null).Select(i => ToItemDetail(i, entry.Instance.PlayerLevel)).ToList();
            return new WebInventory(rows, entry.Instance.Offense, entry.Instance.Defense, entry.Instance.Recovery,
                entry.Instance.PlayerLevel);
        }
    }

    /// <summary>Attribute allocation view: the remaining pool, the allocated values, and the
    /// synthesised totals the engine derives from them.</summary>
    public WebAttributes Attributes(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            var stored = CharacterAttributeMap(owner, characterId, entry.BasePowers);
            var eval = CharacterAttributeEngine.Instance.Evaluate(stored);
            var (available, str, dex, intel, vit, con, agi, mind) = store.GetAttributeAllocation(owner, characterId);
            // These values come from the same recovered attribute evaluator used by combat.
            // Keep the browser's detail labels separate from the engine names.
            var detailNames = new[] {
                "AttackRating_Total", "Armor_Total", "Evasion_Total", "Crit_Chance_MainHand_Total",
                "Crit_Damage_Total", "Attack_Speed_Percent_Total", "Attack_Range_MainHand_Total",
                "Life_Max_Total", "Mana_Max_Total", "Life_Regen_Total", "Mana_Regen_Total",
                "Dodge_Chance_Total", "Block_Chance_Total", "Resistance_Fire_Total_Capped",
                "Resistance_Cold_Total_Capped", "Resistance_Lightning_Total_Capped", "Resistance_Poison_Total_Capped",
                "Magic_Find_Percent_Total", "Item_Quantity_Bonus_Percent_Total", "Movement_Speed_Total",
                "Weapon_Physical_Damage_Min_MainHand_Total", "Weapon_Physical_Damage_Delta_MainHand_Total",
                "Weapon_Fire_Damage_Min_MainHand_Total", "Weapon_Fire_Damage_Delta_MainHand_Total",
                "Weapon_Cold_Damage_Min_MainHand_Total", "Weapon_Cold_Damage_Delta_MainHand_Total",
                "Weapon_Lightning_Damage_Min_MainHand_Total", "Weapon_Lightning_Damage_Delta_MainHand_Total",
                "Weapon_Poison_Damage_Min_MainHand_Total", "Weapon_Poison_Damage_Delta_MainHand_Total"
            };
            var details = detailNames.ToDictionary(name => name, name => eval.Resolve(name));
            return new WebAttributes(available,
                str, dex, intel, vit, con, agi, mind,
                eval.Strength, eval.Dexterity, eval.Intelligence, eval.Vitality, eval.Constitution, eval.Agility, eval.Mindpower,
                details);
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

    /// <summary>Equips a 6-active / 3-passive loadout from the class pool and rebuilds the instance.
    /// Unknown or out-of-class names are dropped; an empty active selection is rejected.</summary>
    public (bool Applied, string Reason) SetLoadout(Guid owner, Guid characterId, IList<string> active, IList<string> passive)
    {
        lock (gate)
        {
            var entry = GetEntry(owner, characterId);
            var persisted = store.ProjectWebSnapshot(owner, characterId);
            if (!PowerCatalog.PoolByClass.TryGetValue(persisted.Class, out var pool))
                return (false, "no_pool");
            var activeNames = (active ?? Array.Empty<string>())
                .Where(n => pool.Active.Any(x => x.Name == n)).Distinct()
                .Take(PowerCatalog.MaxActiveSkills).ToList();
            var passiveNames = (passive ?? Array.Empty<string>())
                .Where(n => pool.Passive.Any(x => x.Name == n)).Distinct()
                .Take(PowerCatalog.MaxPassiveSkills).ToList();
            if (activeNames.Count == 0) return (false, "need_active_skill");
            var (storedActive, storedPassive) = store.SetLoadout(owner, characterId, activeNames, passiveNames);
            entry.BasePowers = PowerCatalog.BuildPool(persisted.Class, storedActive, storedPassive,
                store.GetPowerRanks(owner, characterId));
            entry.Instance.UpdatePowers(EffectivePowers(entry.BasePowers, store.GetMasteryRanks(owner, characterId)));
            return (true, "ok");
        }
    }

    private (bool, string) AllocateMastery(Guid owner, Guid characterId, Entry entry, int masteryId)
    {
        var ranks = store.GetMasteryRanks(owner, characterId);
        MasteryProfile found = null;
        foreach (var skill in entry.BasePowers.Active.Take(PowerCatalog.MaxActiveSkills))
        {
            found = PowerCatalog.MasteriesFor(skill.Name).FirstOrDefault(m => m.IntegerId == masteryId);
            if (found is not null) break;
        }
        if (found is null) return (false, "unknown_mastery");
        var current = ranks.TryGetValue(masteryId, out var r) ? r : 0;
        if (current >= found.MaxPoints) return (false, "mastery_maxed");
        if (ranks.Values.Sum() >= MasteryBudget(entry)) return (false, "no_mastery_points");
        // C07: prerequisites — all dependencies must have at least 1 rank.
        if (MasteryDependencies.Map.TryGetValue(masteryId, out var deps))
        {
            foreach (var depId in deps)
            {
                if (!ranks.TryGetValue(depId, out var depRank) || depRank <= 0)
                    return (false, "missing_prerequisite");
            }
        }

        store.SetMasteryRank(owner, characterId, masteryId, current + 1);
        ranks[masteryId] = current + 1;
        entry.Instance.UpdatePowers(EffectivePowers(entry.BasePowers, ranks));
        return (true, "ok");
    }

    private List<SkillMasteryView> BuildMasteryView(Entry entry, Dictionary<int, int> ranks)
    {
        var rows = new List<SkillMasteryView>();
        var slot = 0;
        foreach (var skill in entry.BasePowers.Active.Take(PowerCatalog.MaxActiveSkills))
        {
            var masteries = PowerCatalog.MasteriesFor(skill.Name)
                .Select(m => new MasteryView(m.Name, m.IntegerId, ranks.TryGetValue(m.IntegerId, out var rank) ? rank : 0, m.MaxPoints, m.Specs,
                    MasteryDependencies.Map.TryGetValue(m.IntegerId, out var deps) ? deps : Array.Empty<int>()))
                .ToList();
            rows.Add(new SkillMasteryView(skill.Name, slot++, masteries.Sum(m => m.Rank), masteries.Sum(m => m.MaxPoints), masteries));
        }
        return rows;
    }

    private static int MasteryBudget(Entry entry) => Math.Max(0, 3 + entry.Instance.PlayerLevel - 1);

    /// <summary>Apply allocated mastery specs to the class kit's base values.</summary>
    private static ClassPowerPool EffectivePowers(ClassPowerPool basePowers, Dictionary<int, int> ranks)
    {
        var active = new List<SkillProfile>();
        foreach (var skill in basePowers.Active.Take(PowerCatalog.MaxActiveSkills))
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
            var updated = modified
                ? skill with
                {
                    Multiplier = values.TryGetValue("Base_Power_Weapon_Damage_Multiplier", out var m) ? m : skill.Multiplier,
                    Cooldown = Math.Max(1, values.TryGetValue("Base_Cooldown", out var cd) ? cd : skill.Cooldown),
                    Radius = values.TryGetValue("Base_Power_Radius", out var r) ? r : skill.Radius,
                    ManaCost = Math.Max(0, values.TryGetValue("Base_Mana_Cost", out var mc) ? mc : skill.ManaCost),
                    Values = values,
                    Confidence = "mastery-modified",
                }
                : skill;
            // Re-slot the equipped subset so the client's skill ids stay 0..N-1.
            active.Add(updated with { Slot = active.Count });
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
            if (!LootTable.IsEquipped(item)) return (false, "not_equipped");
            store.ApplyItemOperations(owner, characterId, new List<ItemOperationEntry>
            {
                new MoveItemOperationEntry { ItemId = itemId, ToSlot = ItemSlotTypes.Inventory, ToLocation = item.Location },
            });
            RecomputeStats(owner, characterId, entry);
            return (true, "ok");
        }

        var slot = LootTable.EquipSlotOf(item);
        if (slot is < 0 or > 13) return (false, "not_equippable");
        if (item.Slot != ItemSlotTypes.Inventory) return (false, "not_in_inventory");
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

    // ShootRangedProjectile handles bow/crossbow auto attacks; equipping a melee weapon
    // must not turn its attacks into projectiles just because Fork is in the passive loadout.
    private static bool UsesRangedAutoAttack(IEnumerable<Game.SerializedItem> items)
        => items.Any(item => item != null && item.Slot == ItemSlotTypes.MainHand &&
            ItemCatalog.Definitions.Any(d => d.IntegerId == item.DefinitionIntegerId &&
                (d.Type == "Bow" || d.Type.StartsWith("Crossbow", StringComparison.Ordinal))));

    private void RecomputeStats(Guid owner, Guid characterId, Entry entry)
    {
        var equippedItems = store.GetItems(owner, characterId);
        var (offense, defense, recovery) = LootTable.EquipmentBonus(equippedItems);
        entry.Instance.UpdateStats(CharacterRatings.Apply(
            CombatantStats.FromRealtime(
                entry.Instance.Offense - entry.EquipOffense + offense,
                entry.Instance.Defense - entry.EquipDefense + defense,
                entry.Instance.Recovery - entry.EquipRecovery + recovery,
                entry.Instance.PlayerLevel) with { ProjectileAutoAttack = UsesRangedAutoAttack(equippedItems) },
            CharacterAttributeMap(owner, characterId, entry.BasePowers)));
        entry.EquipOffense = offense;
        entry.EquipDefense = defense;
        entry.EquipRecovery = recovery;
    }

    /// <summary>Merges the recovered passives' client attribute bonuses into a character attribute
    /// map so the attribute engine's totals (resistances, magic find, ...) include them.</summary>
    private static Dictionary<int, double> WithPassiveBonuses(Dictionary<int, double> map, ClassPowerPool pool)
    {
        foreach (var passive in pool.Passive)
        {
            if (passive.AttributeBonuses is null) continue;
            foreach (var (id, value) in passive.AttributeBonuses)
                map[id] = map.GetValueOrDefault(id) + value;
        }
        return map;
    }

    /// <summary>Character attribute map with the recovered passive bonuses and the active Aesir
    /// blessing effects merged in (both transient; only the base map is persisted).</summary>
    private Dictionary<int, double> CharacterAttributeMap(Guid owner, Guid characterId, ClassPowerPool pool)
    {
        var map = WithPassiveBonuses(store.GetAttributeMap(owner, characterId), pool);
        foreach (var (id, value) in Blessings.AttributeBonuses(store.GetActiveBlessingSeconds(owner, characterId).Keys))
            map[id] = map.GetValueOrDefault(id) + value;
        return map;
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
        // Clamp the catch-up: a long away gap is turned into offline progress, not simulated
        // combat. Without this a tab left closed for hours would fast-forward the fight in one
        // step and usually kill the player.
        var elapsed = Math.Clamp((now - entry.LastAdvanceUtc).TotalSeconds, 0, MaxCatchUpSeconds);
        entry.Instance.Advance(elapsed);
        if (!entry.Niflheim && !entry.Town) entry.WorldRunElapsedSeconds += elapsed;
        entry.LastAdvanceUtc = now;
    }

    private static MapLayout WorldLayout(Guid characterId, int tier)
    {
        var seed = (ulong)(uint)characterId.GetHashCode() << 32 | (uint)characterId.GetHashCode();
        return MapLayout.Generate(21, 21, 5, seed ^ 0x4D41504C41594F55UL ^ (ulong)tier, ThemeForTier(tier));
    }

    private void SettleBeforeTravelLocked(Guid owner, Guid characterId, Entry entry)
    {
        // Charge elapsed time to the old scene, grant its drops, then discard transient state.
        // In particular, time spent safely in town must never catch up in the destination fight.
        AdvanceLocked(entry);
        if (!entry.Niflheim && !entry.Town) RecordWorldProgressLocked(owner, characterId, entry);
        CollectLootLocked(owner, characterId, entry);
        ApplyNiflheimCompletionLocked(owner, characterId, entry);
        FlushLocked(owner, characterId, entry);
    }

    /// <summary>W05: a cleared boss dungeon is the web counterpart of the native
    /// OnDungeonRunCompleted(tier, waypoint, duration) progress event. It advances only the
    /// selected tier's waypoint; tier unlock rules remain controlled by persisted native data.</summary>
    private void RecordWorldProgressLocked(Guid owner, Guid characterId, Entry entry)
    {
        var clears = entry.Instance.DungeonsCleared - entry.LastWorldProgressDungeonsCleared;
        if (clears <= 0) return;
        var tier = entry.Instance.CurrentWorldTier;
        for (var i = 0; i < clears; i++)
        {
            var waypoint = Math.Max(1, entry.CurrentWorldWaypoint);
            var durationSeconds = clears == 1 ? entry.WorldRunElapsedSeconds : entry.WorldRunElapsedSeconds / clears;
            store.ApplyWorldProgress(owner, characterId, tier, waypoint,
                TimeSpan.FromSeconds(Math.Max(0, durationSeconds)));
            var nextWaypoint = waypoint + 1;
            store.SetCurrentWorldWaypoint(owner, characterId, tier, nextWaypoint);
            entry.CurrentWorldWaypoint = nextWaypoint;
            entry.WorldRunElapsedSeconds = Math.Max(0, entry.WorldRunElapsedSeconds - durationSeconds);
        }
        entry.LastWorldProgressDungeonsCleared = entry.Instance.DungeonsCleared;
    }

    private void CollectLootLocked(Guid owner, Guid characterId, Entry entry)
    {
        var drops = entry.Instance.DrainDrops();
        if (drops.Count == 0) return;
        // Magic find uses the recovered per-type saturation. Item quantity uses the recovered
        // floor-with-remainder rule (Num_Items_Granted * (Item_Quantity_Final_Multiplier + 1)),
        // capped at GameParameters.MaxQuantityFromMagicFindMultiplier = 5.
        var (magicFind, itemQuantity) = LootLuck(owner, characterId, entry);
        var items = new List<SerializedItem>();
        foreach (var drop in drops)
        {
            var rollsPerDrop = RollItemsPerDrop(itemQuantity, ref entry.ItemQuantityRemainder);
            for (var i = 0; i < rollsPerDrop; i++)
            {
                var current = i == 0 ? drop : drop with { Seed = drop.Seed + (ulong)i * 0x9E3779B97F4A7C15UL };
                var item = LootTable.CreateItem(current, magicFind: magicFind);
                items.Add(item);
            }
        }
        entry.PendingLootItems.AddRange(items);
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

    /// <summary>Recovered per-drop item count: the fractional quantity carries into the next drop
    /// instead of being discarded, matching the client's <c>PlayerItemQuantityRemainder</c>.</summary>
    public static int RollItemsPerDrop(double itemQuantity, ref double remainder)
    {
        var raw = 1 + itemQuantity + remainder;
        var count = (int)Math.Floor(raw);
        remainder = raw - count;
        return Math.Clamp(count, 1, MaxQuantityFromMagicFindMultiplier);
    }

    /// <summary>Character magic find (Magic_Find_Percent_Total) and item quantity
    /// (Item_Quantity_Bonus_Percent_Total) from the recovered attribute engine, including the
    /// equipped passives' recovered bonuses. Fractions: 0.3 = +30%.</summary>
    private (double MagicFind, double ItemQuantity) LootLuck(Guid owner, Guid characterId, Entry entry)
    {
        var map = CharacterAttributeMap(owner, characterId, entry.BasePowers);
        var eval = CharacterAttributeEngine.Instance.Evaluate(map);
        return (Math.Max(0, eval.Resolve("Magic_Find_Percent_Total")),
            Math.Max(0, eval.Resolve("Item_Quantity_Bonus_Percent_Total")));
    }

    private void FlushLocked(Guid owner, Guid characterId, Entry entry)
    {
        var instance = entry.Instance;
        var killsDelta = instance.Kills - entry.FlushedKills;
        var versionChanged = instance.Version != entry.FlushedVersion;
        if (instance.Experience == entry.FlushedExperience && instance.Silver == entry.FlushedSilver
            && instance.Opals == entry.FlushedOpals && killsDelta == 0 && !versionChanged
            && entry.PendingLootItems.Count == 0 && !entry.PendingNiflheimReturn)
            return;
        // R1: persist the *base* stats only. Flushing the equipment-inclusive totals and then
        // re-adding EquipmentBonus on restore double-counted the gear.
        // P1: also persist the combat version so it survives a restart even when nothing but
        // the version changed (e.g. a rejected move still bumps it).
        var saved = store.SaveRealtimeProgress(owner, characterId, instance.Experience,
            instance.Silver - entry.FlushedSilver, instance.Opals - entry.FlushedOpals,
            new GameStore.CombatSnapshot(
                instance.Offense - entry.EquipOffense,
                instance.Defense - entry.EquipDefense,
                instance.Recovery - entry.EquipRecovery,
                Math.Max(0, killsDelta), 0), instance.Version, currencyDeltas: true,
                updateNiflheimRun: entry.Niflheim || entry.PendingNiflheimReturn,
                niflheimRun: entry.Niflheim ? instance.CaptureNiflheimRun() : null,
                grantItems: entry.PendingLootItems, finishNiflheimInTown: entry.PendingNiflheimReturn,
                completedNiflheim: entry.PendingNiflheimCompletion);
        entry.PendingLootItems.Clear();
        entry.PendingNiflheimReturn = false;
        entry.PendingNiflheimCompletion = false;
        instance.ReconcileWallet(saved.Silver, saved.Opals);
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
        var combat = entry.Instance.Snapshot() with { Events = entry.Instance.DrainEvents() };
        return new WebCombatState(combat, loot, BuildMap(entry));
    }

    /// <summary>Snapshot without consuming pending loot or combat events, for duplicate/rejected commands.</summary>
    private static WebCombatState PeekState(Entry entry)
    {
        var combat = entry.Instance.Snapshot() with { Events = Array.Empty<Nordicandia.Simulation.CombatInstance.CombatEvent>() };
        return new WebCombatState(combat, Array.Empty<LootDropView>(), BuildMap(entry));
    }

    private static WebItemDetail ToItemDetail(SerializedItem item, int playerLevel)
    {
        var equipped = LootTable.IsEquipped(item);
        var equipSlot = LootTable.EquipSlotOf(item);
        var requiredLevel = (int)LootTable.AttributeOf(item, LootTable.AttrRequiredLevel);
        var reason = equipSlot is < 0 or > 13 ? "not_equippable"
            : item.Slot != ItemSlotTypes.Inventory ? "not_in_inventory"
            : requiredLevel > playerLevel + LevelRequirementGrace ? "level_requirement" : "ok";
        var stack = (int)LootTable.AttributeOf(item, 19); // Item_Stack
        return new WebItemDetail(
            item.Id,
            item.Name,
            item.DefinitionIntegerId,
            (int)item.Slot,
            (int)item.BaseRarity,
            equipSlot,
            equipped,
            LootTable.AttributeOf(item, LootTable.AttrOffense),
            LootTable.AttributeOf(item, LootTable.AttrDefense),
            LootTable.AttributeOf(item, LootTable.AttrRecovery),
            AffixNames(item),
            requiredLevel,
            Math.Max(1, stack),
            !equipped && reason == "ok",
            reason);
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
