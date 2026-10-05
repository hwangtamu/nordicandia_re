using Nordicandia.Simulation;

namespace Nordicandia.Simulation;

/// <summary>
/// B06: reusable combat effect units for <see cref="CombatInstance"/>.
/// The <c>UseSkill</c> prototype branches (nova / chain / projectile / strike /
/// summon / mobility / shield / aura / rally) are rewired onto these units; each unit
/// owns one effect family (damage, heal, buff, summon, death trigger) plus the combat
/// event log consumed by rendering. Behaviour is unchanged: the units only relocate the
/// existing inline logic so skills can be migrated one by one (C04–C06).
/// </summary>
public sealed partial class CombatInstance
{
    /// <summary>Combat event for rendering. Drained via <see cref="DrainEvents"/>.</summary>
    public readonly record struct CombatEvent(string Type, int TargetIndex, double Amount, string Detail);

    private const int MaxEvents = 200;
    private readonly List<CombatEvent> events = new();

    /// <summary>Drains the combat event log (damage / heal / buff / summon / death).</summary>
    public List<CombatEvent> DrainEvents()
    {
        var drained = new List<CombatEvent>(events);
        events.Clear();
        return drained;
    }

    private void EmitEvent(string type, int targetIndex, double amount, string detail)
    {
        events.Add(new CombatEvent(type, targetIndex, amount, detail));
        if (events.Count > MaxEvents) events.RemoveRange(0, events.Count - MaxEvents);
    }

    // ------------------------------------------------------------------
    // Damage units
    // ------------------------------------------------------------------

    /// <summary>Core damage unit: resolve the skill hit, apply on-hit effects, damage the
    /// monster. Returns the damage dealt.</summary>
    private double HitTarget(CombatMonster target, SkillProfile skill, double multiplier)
    {
        var hit = ResolveSkill(target, skill, multiplier);
        var dealt = ApplyOnHit(target, hit, autoAttack: false);
        DamageMonster(target, dealt);
        if (dealt > 0) EmitEvent("damage", target.Index, dealt, skill.Name);
        return dealt;
    }

    /// <summary>Nova unit: damage every living monster in radius. Returns total damage,
    /// the first target index (-1 when no target), and the monsters hit (stun and other
    /// per-target effects apply to this pre-damage list, matching the client).</summary>
    private (double total, int first, List<CombatMonster> hit) HitNova(SkillProfile skill, double radius)
    {
        var inRange = AliveMonstersInRadius(radius);
        if (inRange.Count == 0) return (0, -1, inRange);
        double total = 0;
        foreach (var monster in inRange) total += HitTarget(monster, skill, skill.Multiplier);
        return (total, inRange[0].Index, inRange);
    }

    /// <summary>Chain unit: hit up to <paramref name="maxTargets"/> nearest monsters in
    /// radius, decaying the multiplier per jump. Returns total damage and first index.</summary>
    private (double total, int first) HitChain(SkillProfile skill, double radius, int maxTargets)
    {
        var candidates = AliveMonstersInRadius(radius);
        if (candidates.Count == 0) return (0, -1);
        candidates.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
        // ChainLightning._DamageReductionPerJump = 0.25 (client default), so each jump keeps 75%.
        var decay = skill.Values.TryGetValue("Power_Chain_Lightning_Damage_Reduction_Percent", out var d)
            ? Math.Clamp(1 - d, 0.1, 1.0)
            : 0.75;
        double total = 0;
        var first = candidates[0].Index;
        var currentMultiplier = skill.Multiplier;
        foreach (var monster in candidates.Take(maxTargets))
        {
            total += HitTarget(monster, skill, currentMultiplier);
            currentMultiplier *= decay;
        }
        return (total, first);
    }

    /// <summary>Projectile unit (C03): launches a persistent projectile at the nearest monster
    /// in range. Flight, collision and damage resolve on later fixed steps; returns the aimed
    /// target index and damage resolved synchronously (normally zero).</summary>
    private (double total, int first) HitProjectile(SkillProfile skill, double range,
        Action<CombatMonster>? afterHit = null)
    {
        var target = NearestAliveMonster(range);
        if (target is null) return (0, -1);
        // PowerShot rolls its recovered pierce chance; a success keeps the projectile
        // going to the next monster on its path (client: per-hit pierce roll).
        var pierceChance = skill.Values.TryGetValue("Power_Power_Shot_Pierce_Chance_Percent", out var pc)
            ? pc
            : skill.Values.TryGetValue("Power_Projectile_Pierce_Chance", out var ppc) ? ppc : 0.0;
        pierceChance = Math.Clamp(pierceChance, 0, 1);
        var forkChance = skill.Values.TryGetValue("Power_Projectile_Fork_Chance", out var fc)
            ? Math.Clamp(fc, 0, 1) : 0.0;
        var chainChance = skill.Values.TryGetValue("Power_Projectile_Chain_Chance", out var cc)
            ? Math.Clamp(cc, 0, 1) : 0.0;
        var total = FireProjectiles(
            new Projectile { X = PlayerX, Z = PlayerZ, Source = skill.Name },
            target.X, target.Z,
            (m, mult) => HitTarget(m, skill, skill.Multiplier * mult),
            () => forkChance, () => chainChance, () => pierceChance, afterHit);
        return (total, target.Index);
    }

    /// <summary>Applies an impact explosion around the actor actually struck by a projectile.</summary>
    private double ExplodeAround(CombatMonster primary, SkillProfile skill, double multiplier, string source)
    {
        var radius = Math.Max(0, skill.Radius);
        var total = 0.0;
        foreach (var monster in AllCombatMonsters())
        {
            if (!monster.Alive || ReferenceEquals(monster, primary)) continue;
            var dx = monster.X - primary.X;
            var dz = monster.Z - primary.Z;
            if (dx * dx + dz * dz > radius * radius) continue;
            total += HitTarget(monster, skill, multiplier);
        }
        EmitEvent("projectile", primary.Index, total, $"explode {source} {primary.X:F1},{primary.Z:F1}");
        return total;
    }

    // ------------------------------------------------------------------
    // Heal / buff units
    // ------------------------------------------------------------------

    /// <summary>Heal unit: restore a fraction of max HP, capped at max.</summary>
    private void HealPercent(double percent)
    {
        if (percent <= 0) return;
        var before = PlayerHp;
        PlayerHp = Math.Min(PlayerMaxHp, PlayerHp + PlayerMaxHp * percent);
        EmitEvent("heal", -1, PlayerHp - before, "HealPercent");
    }

    /// <summary>Shield unit: gain a mana-shield-style absorb pool.</summary>
    private void GainShield(double lifeFactor)
    {
        if (lifeFactor <= 0) return;
        PlayerShield += PlayerMaxHp * lifeFactor;
        EmitEvent("buff", -1, lifeFactor, "shield");
    }

    /// <summary>Offense buff unit: refresh the offense buff timer/bonus.</summary>
    private void ApplyOffenseBuff(double bonus, double seconds)
    {
        if (bonus <= 0) return;
        // C02: single "offense" instance; re-application replaces only when stronger
        // (duration, then stacks) per Buff.IsStrongerThan.
        PlayerBuffs.Add(new BuffInstance
        {
            DefinitionId = "offense",
            Duration = seconds,
            Remaining = seconds,
            Magnitude = bonus,
        });
        EmitEvent("buff", -1, bonus, "offense");
    }

    /// <summary>Move-speed buff unit.</summary>
    private void ApplyMoveSpeedBuff(double bonus, double seconds)
    {
        if (bonus <= 0) return;
        PlayerBuffs.Add(new BuffInstance
        {
            DefinitionId = "movespeed",
            Duration = seconds,
            Remaining = seconds,
            Magnitude = bonus,
        });
        EmitEvent("buff", -1, bonus, "movespeed");
    }

    // ------------------------------------------------------------------
    // Summon unit
    // ------------------------------------------------------------------

    /// <summary>Summon unit: currently the web keeps the minion-inheritance bonus
    /// placeholder (D11); the spawn itself is owned by the unit for C04–C06 migration.</summary>
    private void SummonMinions(SkillProfile skill)
    {
        var bonus = skill.BuffBonus;
        if (bonus <= 0)
            bonus = skill.Values.TryGetValue("Minion_Inheritance_Weapon_Damage_Bonus_Percent", out var b) ? b : 0.15;
        ApplyOffenseBuff(bonus, skill.BuffSeconds);
        EmitEvent("summon", -1, 0, skill.Name);
    }

    // ------------------------------------------------------------------
    // Death-trigger unit
    // ------------------------------------------------------------------

    /// <summary>Death-trigger unit: everything that happens when a monster dies
    /// (kill count, XP, boss rewards, drops, pack progression). Called by
    /// <c>DamageMonster</c>; death-triggered skill effects hook in here.</summary>
    private void OnMonsterDeath(CombatMonster monster)
    {
        Kills++;
        Experience += CombatModel.ExperienceReward(monster.Level);
        LevelUpIfNeeded();
        EmitEvent("death", monster.Index, 0, monster.Name);

        // Niflheim monsters belong to their pack. Even if a future world modifier
        // produces a Boss rarity, its death must advance this run, not a regular dungeon.
        if (packMode)
        {
            if (rng.NextDouble() < TrashDropChance)
                pendingDrops.Add(RollDrop(monster.Level, minRarity: 0));
            if (pendingPackSize == 0 && monsters.All(m => !m.Alive)) FinishPack(monster.Level);
            return;
        }

        if (monster.IsBoss)
        {
            boss = null;
            DungeonsCleared++;
            Silver += 50 + monster.Level * 25;
            pendingDrops.Add(RollDrop(monster.Level, minRarity: 4));
            pendingDrops.Add(RollDrop(monster.Level + 2, minRarity: 5));
            // E02: the RegularBoss table's guaranteed drop is a HelheimKey.
            pendingDrops.Add(new LootDrop(0, 0, monster.Level, true, rng.NextUInt64(), "RegularBoss", ClassId,
                ForceType: "HelheimKey"));
            dungeonKills = 0;
            return;
        }

        if (rng.NextDouble() < TrashDropChance)
            pendingDrops.Add(RollDrop(monster.Level, minRarity: 0));

        dungeonKills++;
        if (dungeonKills >= BossKillGoal && boss is null)
            boss = CreateBoss();

        monster.RespawnTimer = MonsterRespawnSeconds;
    }

    private void FinishPack(int level)
    {
        packsCleared++;
        if (nativeNiflheimTail && packsCleared >= CombatPacks)
        {
            // The final native count is an exit event, not another monster pack.
            niflheimExitReady = true;
            pendingPackSpawn = false;
            pendingPackSize = 0;
            niflheimExitX = PlayerX;
            niflheimExitZ = PlayerZ;
            if (rng.NextDouble() <= 0.2)
            {
                niflheimChestSize = (int)(rng.NextDouble() * 101) < 5 ? 2 : 1;
                (niflheimChestX, niflheimChestZ) = ConstrainMove(PlayerX, PlayerZ, PlayerX + 2, PlayerZ);
            }
            Version++;
            return;
        }
        if (packsCleared >= totalPacks)
        {
            DungeonsCleared++;
            // NiflheimPortalGameMode.AddSilverForFinish calls GainSilver(5000).
            Silver += 5000;
            pendingDrops.Add(RollDrop(level, minRarity: 4));
            pendingDrops.Add(RollDrop(level + 2, minRarity: 5));
            packMode = false;
            totalPacks = 0;
            packsCleared = 0;
        }
        else pendingPackSpawn = true;
    }

    /// <summary>Open the optional exit chest. Native medium/large base loot rolls are 60–70
    /// and 100–125 respectively; the wider quantity/magic-find pipeline remains provisional.</summary>
    public int OpenNiflheimChest()
    {
        if (!NiflheimExitReady || niflheimChestSize == 0 || niflheimChestOpened) return 0;
        niflheimChestOpened = true;
        var min = niflheimChestSize == 2 ? 100 : 60;
        var max = niflheimChestSize == 2 ? 125 : 70;
        var count = min + (int)(rng.NextDouble() * (max - min + 1));
        for (var i = 0; i < count; i++) pendingDrops.Add(RollDrop(PlayerLevel, minRarity: 0));
        Version++;
        return count;
    }

    /// <summary>Use the spawned TownPortal. An early return does not call this and earns no
    /// completion reward.</summary>
    public bool CompleteNiflheimExit()
    {
        if (!NiflheimExitReady) return false;
        niflheimExitReady = false;
        DungeonsCleared++;
        Silver += 5000;
        Version++;
        return true;
    }
}
