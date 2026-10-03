using Nordicandia.Simulation;

/// <summary>
/// F01 (audit): the recovered C01 damage-pipeline ratings must survive into the live combat
/// instance, not just the pure resolver.
/// </summary>
static class F01RatingsTests
{
    public static void Run()
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }

        var stats = CombatantStats.FromRealtime(100, 50, 0, 5) with
        {
            Conversion = new DamageBundle(Fire: 1),
            Penetration = new ResistanceBundle(Fire: 0.5),
            ArmorPenetration = 0.75,
            DodgeChance = 0.3,
            BlockChance = 0.2,
            BlockedDamageMultiplier = 0.5,
            AlwaysHits = true,
            IgnoresCrits = true,
            DeadlyStrikeChance = 0.25,
            DamageTakenAmplifyPercent = 0.1,
        };
        var instance = new CombatInstance(stats, 0, 0, 0, 0, seed: 1);

        Check(instance.Conversion == new DamageBundle(Fire: 1) && instance.Penetration == new ResistanceBundle(Fire: 0.5)
            && instance.ArmorPenetration == 0.75,
            "F01: conversion / penetration / armour-piercing reach the instance");
        Check(instance.DodgeChance == 0.3 && instance.BlockChance == 0.2 && instance.BlockedDamageMultiplier == 0.5,
            "F01: dodge / block / blocked-damage ratings reach the instance");
        Check(instance.AlwaysHits && instance.IgnoresCrits && instance.DeadlyStrikeChance == 0.25,
            "F01: hit-resolution flags (always-hits / ignores-crits / deadly strike) reach the instance");
        Check(instance.DamageTakenAmplifyPercent == 0.1,
            "F01: amplify-damage-taken reaches the instance");

        // The instance's reconstructed player stats must carry them to the resolver.
        Check(instance.PlayerStatsSnapshot().Conversion == new DamageBundle(Fire: 1)
            && instance.PlayerStatsSnapshot().AlwaysHits && instance.PlayerStatsSnapshot().IgnoresCrits,
            "F01: PlayerStats() forwards the recovered ratings to ResolveBundleAttack");
    }
}
