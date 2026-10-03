namespace Nordicandia.Simulation;

/// <summary>
/// C06 batch 3: remaining skills.
/// - Retaliation (Warrior): 12s buff, retaliate 0.3x weapon damage when hit.
/// - Intimidate (Warrior): 10s AoE debuff, enemies deal 10% less damage.
/// - Fade (Hunter): 6s buff, +100% evasion + 5% movement.
/// - ManaShield (Mage): absorption factor (20% of damage to shield).
/// - IceBlast/Shadowbolt: explosive projectiles (reuse FrozenArrow pattern).
/// - InfernalBlast: 7x projectile + 3y explosion + burn.
/// - Decay: 8s persistent AoE + 20% attack speed slow.
/// - BoneLink: -30% damage taken (defensive, provisional target).
/// - DemonicPresence: 16s aura (+30% life, +10% forcefield, +1% regen).
/// - Sacrifice: +50% mana, next spell +40% damage.
/// - UnholyFocus: 8s (+20% move, +10% attack speed; execute/demon not implemented).
/// - DrainLife: 6s channel, 0.7x + leech (field_0x158 unknown).
/// - ManaArrows: 8 charges, 1.2x, +4 mana per hit.
/// - Backflip: mobility + mirror image minion.
/// - AstralWalk: movement speed buff.
/// </summary>
public partial class CombatInstance
{
    /// <summary>Retaliation: 12s buff, 0.3x weapon damage to attackers.</summary>
    private void ApplyRetaliation(SkillProfile skill)
    {
        var duration = skill.Values.TryGetValue("Buff_Duration", out var dur) ? dur : 12.0;
        var mult = skill.Multiplier;
        PlayerBuffs.Add(new BuffInstance
        {
            DefinitionId = "retaliation",
            Source = "Retaliation",
            Duration = duration,
            Remaining = duration,
            Magnitude = mult,
        });
        EmitEvent("buff", -1, 0, "retaliation Retaliation");
    }

    /// <summary>Intimidate: 10s AoE debuff, enemies in 6y deal 10% less damage.</summary>
    private void ApplyIntimidate(SkillProfile skill)
    {
        var duration = skill.Values.TryGetValue("Buff_Duration", out var dur) ? dur : 10.0;
        var radius = skill.Radius > 0 ? skill.Radius : 6.0;
        var reduction = skill.Values.TryGetValue("Power_Intimidate_Opponent_All_Damage_Reduced_Percent", out var r) ? r : 0.1;
        foreach (var m in AllCombatMonsters())
        {
            if (!m.Alive) continue;
            var d = Math.Sqrt(Math.Pow(m.X - PlayerX, 2) + Math.Pow(m.Z - PlayerZ, 2));
            if (d > radius) continue;
            m.Buffs.Add(new BuffInstance
            {
                DefinitionId = "intimidate",
                Source = "Intimidate",
                Duration = duration,
                Remaining = duration,
                Magnitude = reduction,
            });
        }
        EmitEvent("debuff", -1, 0, "intimidate Intimidate");
    }

    /// <summary>Fade: 6s buff, +100% evasion + 5% movement speed.</summary>
    private void ApplyFade(SkillProfile skill)
    {
        var duration = skill.Values.TryGetValue("Buff_Duration", out var dur) ? dur : 6.0;
        var evasion = skill.Values.TryGetValue("Evasion_Bonus_Percent", out var e) ? e : 1.0;
        var move = skill.Values.TryGetValue("Movement_Speed_Bonus_Percent", out var m) ? m : 0.05;
        PlayerBuffs.Add(new BuffInstance
        {
            DefinitionId = "fade-evasion",
            Source = "Fade",
            Duration = duration,
            Remaining = duration,
            Magnitude = evasion,
        });
        ApplyMoveSpeedBuff(move, duration);
        EmitEvent("buff", -1, 0, "fade Fade");
    }

    /// <summary>Explosive projectile: direct hit + radius explosion (primary excluded).</summary>
    private (double total, int first) HitExplosiveProjectile(SkillProfile skill, double explosionMult)
    {
        var (total, first) = HitProjectile(skill, 14);
        if (first < 0) return (0, -1);
        var primary = monsters.FirstOrDefault(m => m.Index == first)
            ?? (boss is { Alive: true } && boss.Index == first ? boss : null);
        if (primary is not null)
        {
            foreach (var m in AllCombatMonsters())
            {
                if (!m.Alive || ReferenceEquals(m, primary)) continue;
                var d = Math.Sqrt(Math.Pow(m.X - primary.X, 2) + Math.Pow(m.Z - primary.Z, 2));
                if (d > skill.Radius) continue;
                total += HitTarget(m, skill, explosionMult);
            }
            EmitEvent("projectile", first, 0, $"explode {skill.Name} {primary.X:F1},{primary.Z:F1}");
        }
        return (total, first);
    }

    /// <summary>Decay: 8s persistent AoE + 20% attack speed slow on victims.</summary>
    private void TickDecay(PoisonCloud cloud, double dt)
    {
        // Handled in TickClouds via Kind == "Decay"; the slow is applied there.
    }

    /// <summary>BoneLink: -30% damage taken for 15s (defensive; target provisional).</summary>
    private void ApplyBoneLink(SkillProfile skill)
    {
        var duration = skill.Values.TryGetValue("Buff_Duration", out var dur) ? dur : 15.0;
        var reduction = skill.Values.TryGetValue("Amplify_Damage_Taken_Percent", out var a) ? -a : 0.3;
        PlayerBuffs.Add(new BuffInstance
        {
            DefinitionId = "bonelink",
            Source = "BoneLink",
            Duration = duration,
            Remaining = duration,
            Magnitude = reduction,
        });
        EmitEvent("buff", -1, 0, "bonelink BoneLink");
    }

    /// <summary>DemonicPresence: 16s aura (+30% life, +10% forcefield, +1% regen).</summary>
    private void ApplyDemonicPresence(SkillProfile skill)
    {
        var duration = skill.Values.TryGetValue("Power_Duration", out var dur) ? dur : 16.0;
        var life = skill.Values.TryGetValue("Power_Demonic_Presence_Base_Life_Bonus_Percent", out var l) ? l : 0.3;
        var ff = skill.Values.TryGetValue("Power_Demonic_Presence_Base_ForceField_Bonus_Percent", out var f) ? f : 0.1;
        var regen = skill.Values.TryGetValue("Life_Regen_Bonus", out var r) ? r : 0.01;
        PlayerBuffs.Add(new BuffInstance
        {
            DefinitionId = "demonic-life",
            Source = "DemonicPresence",
            Duration = duration,
            Remaining = duration,
            Magnitude = life,
        });
        PlayerBuffs.Add(new BuffInstance
        {
            DefinitionId = "demonic-ff",
            Source = "DemonicPresence",
            Duration = duration,
            Remaining = duration,
            Magnitude = ff,
        });
        PlayerBuffs.Add(new BuffInstance
        {
            DefinitionId = "demonic-regen",
            Source = "DemonicPresence",
            Duration = duration,
            Remaining = duration,
            Magnitude = regen,
        });
        EmitEvent("buff", -1, 0, "demonic DemonicPresence");
    }

    /// <summary>Sacrifice: +50% mana granted, next offensive spell +40% damage.</summary>
    private void ApplySacrifice(SkillProfile skill)
    {
        var duration = skill.Values.TryGetValue("Power_Duration", out var dur) ? dur : 5.0;
        var mana = skill.Values.TryGetValue("Mana_Granted_Percent", out var m) ? m : 0.5;
        var dmg = skill.Values.TryGetValue("Next_Offensive_Spell_Cast_Damage_Increased_Percent", out var d) ? d : 0.4;
        // Grant mana immediately (50% of max).
        PlayerMana = Math.Min(PlayerMaxMana, PlayerMana + PlayerMaxMana * mana);
        PlayerBuffs.Add(new BuffInstance
        {
            DefinitionId = "sacrifice-dmg",
            Source = "Sacrifice",
            Duration = duration,
            Remaining = duration,
            Magnitude = dmg,
            // Consumed on next offensive cast (checked in UseSkill).
        });
        EmitEvent("buff", -1, 0, "sacrifice Sacrifice");
    }

    /// <summary>UnholyFocus: 8s (+20% move, +10% attack speed).</summary>
    private void ApplyUnholyFocus(SkillProfile skill)
    {
        var duration = skill.Values.TryGetValue("Buff_Duration", out var dur) ? dur : 8.0;
        var move = skill.Values.TryGetValue("Power_Unholy_Focus_Movement_Speed_Bonus_Percent", out var m) ? m : 0.2;
        var atk = skill.Values.TryGetValue("Power_Unholy_Focus_Attack_Speed_Bonus_Percent", out var a) ? a : 0.1;
        ApplyMoveSpeedBuff(move, duration);
        PlayerBuffs.Add(new BuffInstance
        {
            DefinitionId = "unholy-atk",
            Source = "UnholyFocus",
            Duration = duration,
            Remaining = duration,
            Magnitude = atk,
        });
        EmitEvent("buff", -1, 0, "unholy UnholyFocus");
        // Not implemented: execute threshold (30%), demon shadowbolt (0.9x).
    }

    /// <summary>ManaArrows: grants 8 charges; each projectile consumes one, each hit +4 mana.</summary>
    private void ApplyManaArrows(SkillProfile skill)
    {
        var charges = skill.Values.TryGetValue("Power_Mana_Arrows_Num_Charges", out var c) ? (int)c : 8;
        PlayerBuffs.Add(new BuffInstance
        {
            DefinitionId = "manaarrows",
            Source = "ManaArrows",
            Duration = 60, // Charges persist until used (provisional duration).
            Remaining = 60,
            Magnitude = charges, // Stacks = charges (abusing Magnitude for count).
        });
        EmitEvent("buff", -1, charges, "manaarrows ManaArrows");
    }

    /// <summary>Backflip: mobility + mirror image minion (5s, 20% HP).</summary>
    private void Backflip(SkillProfile skill)
    {
        // Move away from the nearest threat (like Teleport but shorter).
        var threat = NearestAliveMonster(14);
        if (threat is not null)
        {
            var dx = PlayerX - threat.X;
            var dz = PlayerZ - threat.Z;
            var d = Math.Max(1e-6, Math.Sqrt(dx * dx + dz * dz));
            PlayerX = Math.Clamp(PlayerX + dx / d * 6, -ArenaHalf, ArenaHalf);
            PlayerZ = Math.Clamp(PlayerZ + dz / d * 6, -ArenaHalf, ArenaHalf);
        }
        // Spawn a mirror image (fragile minion).
        var duration = skill.Values.TryGetValue("Power_Backflip_Mirror_Image_Duration", out var dur) ? dur : 5.0;
        var hpPct = skill.Values.TryGetValue("Power_Backflip_Mirror_Image_Health_Percent", out var h) ? h : 0.2;
        var mirror = new PlayerMinion
        {
            Name = "MirrorImage",
            X = PlayerX,
            Z = PlayerZ,
            Damage = PlayerDamageBundle().Total * 0.5, // Provisional: half damage.
            AttackInterval = 2.0,
        };
        mirror.MaxHp = mirror.Hp = Math.Max(1, PlayerMaxHp * hpPct);
        minions.Add(mirror);
        EmitEvent("mobility", -1, 0, $"backflip Backflip {PlayerX:F1},{PlayerZ:F1}");
        EmitEvent("summon", -1, 1, "MirrorImage");
    }

    /// <summary>AstralWalk: movement speed buff (10.0 = 1000%? provisional: 10x).</summary>
    private void ApplyAstralWalk(SkillProfile skill)
    {
        var bonus = skill.Values.TryGetValue("AstralWalk_Movement_Speed_Bonus_Percent", out var b) ? b : 10.0;
        // 10.0 likely means 1000% (following the 0.2=20% pattern). Cap at 3x for sanity.
        var capped = Math.Min(bonus, 3.0);
        ApplyMoveSpeedBuff(capped, 8.0); // Duration provisional.
        EmitEvent("buff", -1, 0, "astral AstralWalk");
    }

    /// <summary>DrainLife: 6s channeled drain, 0.7x per tick + leech.</summary>
    private void StartDrainLife(SkillProfile skill, CombatMonster target)
    {
        var duration = skill.Values.TryGetValue("Power_Duration", out var dur) ? dur : 6.0;
        activeChannel = new Channel
        {
            Remaining = duration,
            TickInterval = 1.0,
            Radius = 14, // Targeted, not radial.
            DamageMult = skill.Multiplier,
            Skill = skill,
        };
        activeChannel.TickTimer = activeChannel.TickInterval;
        // Store the target in the channel (reuse via a field).
        drainTarget = target;
        EmitEvent("channel", target.Index, 0, "start DrainLife");
    }

    private CombatMonster drainTarget;
}
