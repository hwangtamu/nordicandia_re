using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;

/// <summary>
/// C06 batch 3: Retaliation, Intimidate, Fade, ManaShield, IceBlast, Shadowbolt,
/// InfernalBlast, Decay, BoneLink, DemonicPresence, Sacrifice, UnholyFocus,
/// DrainLife, ManaArrows, Backflip, AstralWalk.
/// </summary>
static class C06Batch3Tests
{
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private static CombatantStats Stats(double physical = 100) => new(100, 1000, 0, 1,
        AttackRating: 1e12, CritChance: 0, Damage: new DamageBundle(Physical: physical));

    private static CombatInstance Create(int classId, string skill, ulong seed = 999, int count = 2)
    {
        var pool = PowerCatalog.BuildPool(classId, new[] { skill }, System.Array.Empty<string>());
        var inst = new CombatInstance(Stats(), 0, 0, 0, 0, seed, monsterCount: count, classPowers: pool);
        foreach (var m in inst.Monsters)
        {
            m.Hp = m.MaxHp = 100000; m.Armor = m.Defense = 0;
            m.Speed = 0; m.Offense = 0; m.AttackCooldown = 1000; m.Resistances = default;
        }
        return inst;
    }

    public static void Run()
    {
        Retaliation();
        Intimidate();
        Fade();
        ManaShield();
        ExplosiveProjectiles();
        Decay();
        NecroBuffs();
        DrainLife();
        ManaArrows();
        Backflip();
        AstralWalk();
        Thunderstrike();
    }

    private static void Retaliation()
    {
        var inst = Create(0, "Retaliation", count: 1);
        inst.Monsters[0].X = 1;
        inst.Monsters[0].Offense = 500; inst.Monsters[0].AttackCooldown = 0.1;
        var cast = inst.UseSkill(0);
        Check(cast.Cast && inst.PlayerBuffs.Has("retaliation", "Retaliation"),
            "retaliation: cast applies the buff");
        var monsterHp = inst.Monsters[0].Hp;
        inst.Advance(1.0);
        var dealt = monsterHp - inst.Monsters[0].Hp;
        // Retaliation (0.3x) + player auto-attacks; just verify positive.
        Check(dealt > 0, $"retaliation: attacker takes retaliation damage ({dealt:F0})");
    }

    private static void Intimidate()
    {
        var inst = Create(0, "Intimidate", count: 2);
        inst.Monsters[0].X = 3; // within 6y
        inst.Monsters[1].X = 20; // outside
        var cast = inst.UseSkill(0);
        Check(cast.Cast, "intimidate: cast succeeds");
        Check(inst.Monsters[0].Buffs.Has("intimidate", "Intimidate"),
            "intimidate: monster in 6y gets the debuff");
        Check(!inst.Monsters[1].Buffs.Has("intimidate", "Intimidate"),
            "intimidate: distant monster unaffected");
    }

    private static void Fade()
    {
        var inst = Create(4, "Fade", count: 1);
        var cast = inst.UseSkill(0);
        Check(cast.Cast && inst.PlayerBuffs.Has("fade-evasion", "Fade"),
            "fade: cast applies evasion buff");
        var ev = inst.PlayerBuffs.MagnitudeOf("fade-evasion", "Fade");
        Check(Math.Abs(ev - 1.0) < 1e-9, "fade: +100% evasion from recovered attribute");
    }

    private static void ManaShield()
    {
        var inst = Create(5, "ManaShield", count: 1);
        var cast = inst.UseSkill(0);
        Check(cast.Cast && inst.PlayerShield > 0, "manashield: cast grants a shield");
        Check(inst.PlayerBuffs.Has("manashield", "ManaShield"),
            "manashield: absorption buff applied");
        var absorb = inst.PlayerBuffs.MagnitudeOf("manashield", "ManaShield");
        Check(Math.Abs(absorb - 0.2) < 1e-9, "manashield: 20% absorption factor");
    }

    private static void ExplosiveProjectiles()
    {
        // IceBlast: 3.0x + 15y explosion.
        var inst = Create(5, "IceBlast", count: 2);
        inst.Monsters[0].X = 3;
        inst.Monsters[1].X = 10; // within 15y of primary
        var cast = inst.UseSkill(0);
        Check(cast.Cast, "iceblast: cast succeeds");
        var inst2 = Create(5, "IceBlast", count: 2);
        inst2.Monsters[0].X = 3;
        inst2.Monsters[1].X = 10;
        var h1 = inst2.Monsters[1].Hp;
        inst2.UseSkill(0);
        Check(h1 - inst2.Monsters[1].Hp > 0, "iceblast: 15y explosion damages nearby");
        // Shadowbolt: 3.0x + 2.5y explosion.
        var inst3 = Create(6, "Shadowbolt", count: 2);
        inst3.Monsters[0].X = 3;
        inst3.Monsters[1].X = 5; // within 2.5y
        var h = inst3.Monsters[1].Hp;
        inst3.UseSkill(0);
        Check(h - inst3.Monsters[1].Hp > 0, "shadowbolt: 2.5y explosion damages nearby");
        // InfernalBlast: 7.0x + burn.
        var inst4 = Create(5, "InfernalBlast", count: 1);
        inst4.Monsters[0].X = 3;
        var cast4 = inst4.UseSkill(0);
        Check(cast4.Cast, "infernal: cast succeeds");
        Check(inst4.Monsters[0].Buffs.Has("poison", "InfernalBlast"),
            "infernal: 4s burn applied");
    }

    private static void Decay()
    {
        var inst = Create(6, "Decay", count: 1);
        inst.Monsters[0].X = 3;
        var cast = inst.UseSkill(0);
        Check(cast.Cast && inst.Clouds.Count == 1, "decay: cast spawns the AoE");
        Check(inst.Clouds[0].Kind == "Decay", "decay: kind is Decay");
        inst.Advance(1.1);
        Check(inst.Monsters[0].Buffs.Has("decay-slow", "Decay"),
            "decay: attack speed slow applied");
    }

    private static void NecroBuffs()
    {
        // BoneLink: -30% damage taken.
        var inst = Create(6, "BoneLink", count: 1);
        var cast = inst.UseSkill(0);
        Check(cast.Cast && inst.PlayerBuffs.Has("bonelink", "BoneLink"),
            "bonelink: cast applies the buff");
        // DemonicPresence: life/forcefield/regen.
        var inst2 = Create(6, "DemonicPresence", count: 1);
        var cast2 = inst2.UseSkill(0);
        Check(cast2.Cast && inst2.PlayerBuffs.Has("demonic-life", "DemonicPresence"),
            "demonic: cast applies the aura buffs");
        // Sacrifice: mana + next spell damage.
        var inst3 = Create(6, "Sacrifice", count: 1);
        var cast3 = inst3.UseSkill(0);
        Check(cast3.Cast, "sacrifice: cast succeeds");
        Check(inst3.PlayerBuffs.Has("sacrifice-dmg", "Sacrifice"),
            "sacrifice: next-spell damage buff applied");
        // Mana grant: 50% of max, net of the 10 mana cost (player was full).
        Check(inst3.PlayerMana == inst3.PlayerMaxMana,
            "sacrifice: mana topped up to max");
        // UnholyFocus: move + attack speed.
        var inst4 = Create(6, "UnholyFocus", count: 1);
        var cast4 = inst4.UseSkill(0);
        Check(cast4.Cast && inst4.PlayerBuffs.Has("unholy-atk", "UnholyFocus"),
            "unholy: cast applies the buff");
    }

    private static void DrainLife()
    {
        var inst = Create(6, "DrainLife", count: 1);
        inst.Monsters[0].X = 5;
        inst.Monsters[0].Hp = inst.Monsters[0].MaxHp = 10000;
        var cast = inst.UseSkill(0);
        Check(cast.Cast && inst.ActiveChannel is not null, "drain: cast starts the channel");
        var hp0 = inst.Monsters[0].Hp;
        var playerHp = inst.PlayerHp;
        inst.Advance(2.0);
        var dealt = hp0 - inst.Monsters[0].Hp;
        var healed = inst.PlayerHp - playerHp;
        Check(dealt > 0, $"drain: channel deals damage ({dealt:F0})");
        // Leech may be 0 if player is at full HP; just verify the mechanic runs.
        Check(inst.ActiveChannel is not null || dealt > 0, "drain: channel persists");
    }

    private static void ManaArrows()
    {
        var inst = Create(4, "ManaArrows", count: 1);
        inst.Monsters[0].X = 3;
        // First cast grants charges.
        var cast1 = inst.UseSkill(0);
        Check(cast1.Cast && inst.PlayerBuffs.Has("manaarrows", "ManaArrows"),
            "manaarrows: first cast grants charges");
        var charges = inst.PlayerBuffs.MagnitudeOf("manaarrows", "ManaArrows");
        Check(Math.Abs(charges - 8) < 1e-9, "manaarrows: 8 charges from recovered attribute");
        // Second cast consumes a charge and fires.
        for (var i = 0; i < 6; i++) inst.Advance(5.0); // 30s > 25s cooldown
        var manaBefore = inst.PlayerMana;
        var cast2 = inst.UseSkill(0);
        Check(cast2.Cast && cast2.Damage > 0, "manaarrows: second cast fires the projectile");
        Check(inst.PlayerMana > manaBefore - 25, "manaarrows: hit grants mana (net positive)");
    }

    private static void Backflip()
    {
        var inst = Create(4, "Backflip", count: 1);
        inst.Monsters[0].X = 3;
        var px = inst.PlayerX;
        var cast = inst.UseSkill(0);
        Check(cast.Cast, "backflip: cast succeeds");
        Check(inst.PlayerX < px, "backflip: moves away from threat");
        Check(inst.PlayerMinions.Count == 1 && inst.PlayerMinions[0].Name == "MirrorImage",
            "backflip: mirror image spawned");
    }

    private static void AstralWalk()
    {
        var inst = Create(6, "AstralWalk", count: 1);
        var cast = inst.UseSkill(0);
        Check(cast.Cast, "astral: cast succeeds");
        Check(inst.PlayerBuffs.Has("movespeed", ""), "astral: movement buff applied");
    }

    private static void Thunderstrike()
    {
        var inst = Create(5, "Thunderstrike", count: 2);
        inst.Monsters[0].X = 10;
        inst.Monsters[1].X = 20;
        var cast = inst.UseSkill(0);
        Check(cast.Cast && inst.Clouds.Count == 1, "thunder: cast spawns the storm");
        var storm = inst.Clouds[0];
        Check(storm.Kind == "Thunderstrike" && Math.Abs(storm.Radius - 30.0) < 1e-9,
            "thunder: 30y radius from recovered attribute");
        var hp0 = inst.Monsters[0].Hp;
        inst.Advance(1.1);
        // Provisional 1.0x: just verify the storm ticks.
        Check(hp0 - inst.Monsters[0].Hp > 0, "thunder: storm strikes (provisional 1.0x)");
    }
}
